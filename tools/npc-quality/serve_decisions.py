#!/usr/bin/env python3
"""Local d1 decisions for Play Mode: the pinned checkpoint on the Apple GPU, 127.0.0.1 only.

Unity's GatekeeperDecisionService posts the encounter snapshot and the player's new message.
The reply carries scope, intent and requested player-fact slots; the game's C# policy owns
every effect. Start it before or during Play Mode and leave it running across sessions.
"""
import argparse
import hashlib
import json
import os
import pathlib
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = pathlib.Path(__file__).resolve().parent
DEFAULT_PORT = 8765
MAX_BODY = 32 * 1024
# Unity waits 4 s for a decision. A request still queued after 3 s answers busy, and health
# reports a decision running longer than 4 s, so stalls and failures send Play Mode back to
# the earlier path instead of costing a clarification on every message.
QUEUE_SECONDS, STALL_SECONDS = 3, 4
OUTCOMES = ('Playing', 'Passed', 'Liked', 'Refused')
SPEAKERS = ('Player', 'Trola')


def questions():
    """The two spike questions, unchanged, plus one yes/no question per player-fact slot."""
    asked = json.loads((ROOT / 'decision-questions.json').read_text())
    asked.update(json.loads((ROOT / 'decision-fact-slots.json').read_text()))
    return asked


SLOTS = tuple(json.loads((ROOT / 'decision-fact-slots.json').read_text()))


def text(request, key, limit, default=None):
    value = request.get(key, default)
    if not isinstance(value, str) or len(value) > limit:
        raise ValueError(f'{key}: text up to {limit} characters')
    return value


def state_for(request):
    """The state layout of probe_decisions.py, from validated snapshot fields only."""
    if not isinstance(request, dict):
        raise ValueError('request: a JSON object')
    message = text(request, 'message', 480)
    if not message.strip():
        raise ValueError('message: empty')
    outcome, patience = text(request, 'outcome', 16), request.get('patience')
    if outcome not in OUTCOMES:
        raise ValueError('outcome: one of ' + ', '.join(OUTCOMES))
    if not isinstance(patience, int) or isinstance(patience, bool) or not 0 <= patience <= 100:
        raise ValueError('patience: a whole number from 0 to 100')
    approaches, history = request.get('approaches', []), request.get('history', [])
    if not isinstance(approaches, list) or len(approaches) > 8 or not all(isinstance(a, str) and len(a) <= 40 for a in approaches):
        raise ValueError('approaches: up to 8 short names')
    if not isinstance(history, list) or len(history) > 12:
        raise ValueError('history: up to 12 turns')
    turns = []
    for turn in history:
        if not isinstance(turn, dict) or turn.get('speaker') not in SPEAKERS:
            raise ValueError('history: turns of Player or Trola')
        turns.append(dict(speaker=turn['speaker'], text=text(turn, 'text', 480)))
    return dict(setting='A fantasy camp gate. Modern devices and Wi-Fi are outside this world.',
                npc=dict(name='Trola', role='troll gatekeeper', personality='Proud wall builder; dry jokes, gruff, grudging respect.'),
                player=dict(name=text(request, 'name', 80), business=text(request, 'business', 160)),
                encounter=dict(outcome=outcome, patience=patience, approaches=approaches),
                recent_history=turns, player_message=message)


def prime_state():
    return state_for(dict(message='Greetings.', name='Mira', business='selling turnips',
                          outcome='Passed', patience=3, approaches=['bribe']))


def summarize(answers):
    """Choice labels with their confidence; a slot is requested at P(yes) >= 0.5, d1's own reading of a noul."""
    scope, intent = answers['scope'], answers['intent']
    slots = {slot: answers[slot]['noul'] for slot in SLOTS}
    return dict(scope=scope['choice'], scope_confidence=scope['confidence'],
                intent=intent['choice'], intent_confidence=intent['confidence'],
                facts=[slot for slot, p in slots.items() if p >= 0.5], fact_probabilities=slots)


def verify(model):
    manifest = json.loads((ROOT / 'decision-model.json').read_text())
    for entry in manifest['files']:
        file = model / entry['path']
        with file.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        if file.stat().st_size != entry['size'] or digest != entry['sha256']:
            raise ValueError('Pinned model verification failed: ' + entry['path'])
    return manifest


class D1:
    """One resident model. The pinned loader executes its own Python, so files are hashed first."""

    def __init__(self, model):
        manifest = verify(model)
        os.environ['TOKENIZERS_PARALLELISM'] = 'false'
        os.environ.pop('PYTORCH_ENABLE_MPS_FALLBACK', None)
        import torch
        from transformers import AutoModel
        if not torch.backends.mps.is_available():
            raise RuntimeError('MPS unavailable; refusing to serve CPU decisions to Play Mode')
        self.torch, self.questions = torch, questions()
        started = time.perf_counter()
        self.model = AutoModel.from_pretrained(str(model), trust_remote_code=True, dtype=torch.bfloat16).to('mps').eval()
        torch.mps.synchronize()
        self.health = dict(model=manifest['repo'], revision=manifest['revision'], device='mps', dtype='bfloat16',
                           questions=list(self.questions), load_seconds=round(time.perf_counter() - started, 3))
        started = time.perf_counter()
        summarize(self.decide(prime_state())['answers'])  # Also fails fast on a question/answer mismatch.
        self.health['prime_seconds'] = round(time.perf_counter() - started, 3)

    def decide(self, state):
        with self.torch.inference_mode():
            result = self.model.system_one(state, self.questions)
        self.torch.mps.synchronize()
        return result


class Handler(BaseHTTPRequestHandler):
    timeout = 5  # A stalled client cannot hold a connection open.

    def do_GET(self):
        if self.path != '/health':
            return self.reply(404, dict(error='not found'))
        self.reply(200, self.server.health())

    def do_POST(self):
        if self.path != '/decide':
            return self.reply(404, dict(error='not found'))
        # JSON content also keeps browser pages from posting without a preflight this server never answers.
        if self.headers.get_content_type() != 'application/json':
            return self.reply(415, dict(error='application/json only'))
        try:
            length = int(self.headers.get('Content-Length') or 0)
        except ValueError:
            length = -1
        if not 0 < length <= MAX_BODY:
            return self.reply(413, dict(error=f'body: 1 to {MAX_BODY} bytes'))
        try:
            state = state_for(json.loads(self.rfile.read(length)))
        except ValueError as error:  # Includes malformed JSON.
            return self.reply(400, dict(error=str(error)))
        if not self.server.lock.acquire(timeout=QUEUE_SECONDS):
            return self.reply(503, dict(error='busy'))
        try:
            decision = self.server.run(state)
        except Exception as error:  # The game turns a failure into one clarification.
            print(f'decision failed: {error!r}', file=sys.stderr, flush=True)
            return self.reply(500, dict(error=type(error).__name__))
        finally:
            self.server.lock.release()
        self.reply(200, decision)
        self.server.record(state, decision)

    def reply(self, status, body):
        data = json.dumps(body).encode()
        try:
            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            pass  # Play Mode cancelled the request.

    def log_message(self, format, *args):
        pass  # Decisions are recorded below; request lines add nothing.


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, port, engine, receipts=None):
        super().__init__(('127.0.0.1', port), Handler)
        self.engine, self.receipts, self.lock = engine, receipts, threading.Lock()
        self.failure, self.busy_since, self.receipts_lock = None, None, threading.Lock()

    def run(self, state):
        """One decision, with the lock held. The last outcome and any running decision feed health()."""
        started = self.busy_since = time.monotonic()
        try:
            result = self.engine.decide(state)
            decision = dict(summarize(result['answers']), seconds=round(time.monotonic() - started, 4),
                            input_tokens=result.get('usage', {}).get('input_tokens', 0))
        except Exception as error:
            self.failure = type(error).__name__
            raise
        finally:
            self.busy_since = None
        self.failure = None
        return decision

    def health(self):
        busy = self.busy_since
        if busy is not None and time.monotonic() - busy > STALL_SECONDS:
            return dict(self.engine.health, ready=False, error=f'decision running over {STALL_SECONDS} s')
        # After a failure, ready again only once the prime decision succeeds.
        if self.failure and self.lock.acquire(timeout=0.5):
            try:
                self.run(prime_state())
            except Exception:
                pass
            finally:
                self.lock.release()
        return dict(self.engine.health, ready=self.failure is None, **({'error': self.failure} if self.failure else {}))

    def record(self, state, decision):
        facts = ','.join(decision['facts']) or '-'
        print(f"{decision['seconds'] * 1000:6.0f} ms  {decision['scope']}/{decision['intent']}  facts={facts}  "
              f"{state['player_message'][:72]!r}", flush=True)
        if self.receipts:
            with self.receipts_lock, self.receipts.open('a') as output:
                output.write(json.dumps(dict(at=time.time(), state=state, decision=decision)) + '\n')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--model', type=pathlib.Path, required=True, help='directory holding the pinned d1 files')
    p.add_argument('--port', type=int, default=DEFAULT_PORT)
    p.add_argument('--receipts', type=pathlib.Path, help='append each state and decision as JSON lines')
    args = p.parse_args()
    if args.receipts:  # Fail before loading, not on every request.
        args.receipts.parent.mkdir(parents=True, exist_ok=True)
        args.receipts.open('a').close()
    engine = D1(args.model)
    server = Server(args.port, engine, args.receipts)
    print(f"d1 ready on http://127.0.0.1:{server.server_address[1]}/ "
          f"(load {engine.health['load_seconds']} s, prime {engine.health['prime_seconds']} s)", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    sys.exit(main())
