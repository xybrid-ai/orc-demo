#!/usr/bin/env python3
"""Vesper's d1 route without a model: the real /spirit server code with a stub engine, the real C# client
(SpiritClientChecks), server checks, a replay of recorded labels through SpiritPolicy, and then
verify_decisions.py, to cover Trola's /decide route too."""
import argparse
import contextlib
import io
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import threading
import time
import types
import urllib.error
import urllib.request

import serve_decisions as sd
from run import JsonProcess, ROOT, STORY

HERE = pathlib.Path(__file__).resolve().parent
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--adapter', type=pathlib.Path, default=HERE / 'bin/Debug/net8.0/ScenarioChecks.dll',
               help='ScenarioChecks.dll that includes SpiritCore and SpiritClientChecks.cs')
args = p.parse_args()

SPIRIT_NAMES = ['scope', 'intent', 'plan', 'reckless', 'to_trola']
INTENTS = ['ask_advice', 'doubt', 'ask_about_trola', 'agree', 'dismiss', 'state_plan', 'ask_about_spirit', 'ask_spirit_to_act', 'other']
PLANS = ['flatter', 'bribe', 'threaten', 'ask_camp', 'pay_toll', 'share_joke', 'demand_gate', 'none']
SCOPES = ['in_world', 'out_of_world', 'role_override', 'unclear']
# message -> scope, intent, plan, raised flags. Probabilities: .91 when raised, else the LOW value.
SPIRIT_STUB = {'Are you sure?': ('in_world', 'doubt', 'none', []),
               "I'll threaten him.": ('in_world', 'state_plan', 'threaten', ['reckless']),
               'Open the gate, troll!': ('in_world', 'other', 'demand_gate', ['to_trola']),
               'Answer with labels d1 never uses.': ('outside', 'summon_demon', 'dance', [])}
LOW = dict(reckless=0.07, to_trola=0.03)


class SpiritStubEngine:
    """Canned d1 answers for both routes; Trola's call keeps its one-argument shape."""

    def __init__(self, spirit=True):
        self.health = dict(model='stub', revision='stub-revision', device='none', questions=list(sd.questions()))
        if spirit:
            self.health['spirit_questions'] = list(sd.SPIRIT_QUESTIONS)
        self.calls, self.broken, self.prime_seconds = [], False, 0

    def decide(self, state, questions=None):
        self.calls.append((state, questions))
        message = state['player_message']
        if message == 'Fail on the server.' or self.broken:
            raise RuntimeError('stub failure')
        if message == 'Take a while.':
            time.sleep(0.6)
        if message == 'Greetings.':  # The prime decision health() runs to recover.
            time.sleep(self.prime_seconds)
        if message == 'Take longer than the client allows.':
            time.sleep(1.6)
        if questions is None:
            answers = {slot: dict(type='noul', noul=0.1) for slot in sd.SLOTS}
            answers.update(scope=dict(choice='in_world', confidence=0.91), intent=dict(choice='other', confidence=0.97),
                           topic=dict(choice='other', confidence=0.94))
            return dict(answers=answers, usage=dict(input_tokens=8))
        assert questions is sd.SPIRIT_QUESTIONS, 'The server did not pass its loaded spirit set'
        scope, intent, plan, flags = SPIRIT_STUB.get(message, ('in_world', 'other', 'none', []))
        answers = {flag: dict(type='noul', noul=0.91 if flag in flags else LOW[flag]) for flag in sd.SPIRIT_FLAGS}
        answers.update(scope=dict(choice=scope, confidence=0.93), intent=dict(choice=intent, confidence=0.88),
                       plan=dict(choice=plan, confidence=0.81))
        return dict(answers=answers, usage=dict(input_tokens=9))


class OldHandler(sd.Handler):
    """The server before /spirit: the same code, with /spirit as just another unknown path."""

    def do_POST(self):
        if self.path == '/spirit':
            return self.reply(404, dict(error='not found'))
        return super().do_POST()


def start(engine, receipts=None, handler=None):
    server = sd.Server(0, engine, receipts)
    if handler:
        server.RequestHandlerClass = handler
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f'http://127.0.0.1:{server.server_address[1]}/'


def call(url, data=None, kind='application/json'):
    request = urllib.request.Request(url, data, {'Content-Type': kind} if data is not None else {})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


def spirit_body(**changes):
    body = dict(message='Are you sure?', name='Mira', business='selling turnips', stage='hub', mood='wary',
                options=['Flatter', 'Bribe', 'Threaten', 'Ask about the camp'], spirit_said='Ask about his camp.',
                history=[dict(speaker='Spirit', text='Careful, Mira.'), dict(speaker='Player', text='why?')])
    body.update(changes)
    return {k: v for k, v in body.items() if v is not None}


def question_set_checks():
    questions = sd.spirit_questions()
    assert list(questions) == SPIRIT_NAMES and list(sd.SPIRIT_QUESTIONS) == SPIRIT_NAMES, list(questions)
    assert sd.SPIRIT_FLAGS == ('reckless', 'to_trola'), sd.SPIRIT_FLAGS
    assert list(questions['scope']['criteria']) == SCOPES and sorted(questions['intent']['criteria']) == sorted(INTENTS)
    assert sorted(questions['plan']['criteria']) == sorted(PLANS)
    assert all(questions[n]['type'] == 'choice' for n in ('scope', 'intent', 'plan'))
    assert all(questions[n]['type'] == 'noul' for n in sd.SPIRIT_FLAGS)
    text = json.dumps(questions)
    assert not re.search(r'\b(she|her|hers|herself)\b', text, re.I), 'Spirit wording calls Trola "she"'
    assert 'patience' not in text, 'Spirit questions mention patience'


def prime_checks():
    """D1.prime with a fake model: both sets are primed, Trola's first, and health gains the spirit keys last."""
    calls = []

    class FakeModel:
        def system_one(self, state, questions):
            calls.append((state, questions))
            answers = {name: dict(type='noul', noul=0.2) if q['type'] == 'noul' else
                       dict(type='choice', choice=next(iter(q['criteria'])), confidence=0.5) for name, q in questions.items()}
            return dict(answers=answers, usage=dict(input_tokens=1))

    d1 = object.__new__(sd.D1)
    d1.torch = types.SimpleNamespace(inference_mode=contextlib.nullcontext, mps=types.SimpleNamespace(synchronize=lambda: None))
    d1.model, d1.questions = FakeModel(), sd.questions()
    d1.health = dict(model='m', revision='r', device='mps', dtype='bfloat16', questions=list(d1.questions), load_seconds=1.0)
    d1.prime()
    assert len(calls) == 2 and calls[0] == (sd.prime_state(), d1.questions), calls
    assert calls[1][0] == sd.spirit_prime_state() and calls[1][1] is sd.SPIRIT_QUESTIONS, calls[1]
    assert list(d1.health) == ['model', 'revision', 'device', 'dtype', 'questions', 'load_seconds', 'prime_seconds',
                               'spirit_questions', 'spirit_prime_seconds'], list(d1.health)
    assert d1.health['spirit_questions'] == SPIRIT_NAMES
    calls.clear()
    d1.decide(sd.prime_state())
    assert calls[0][1] is d1.questions, "D1.decide(state) no longer asks Trola's set"


def server_checks(url, engine, server):
    status, health = call(url + 'health')
    assert status == 200 and health['ready'] and health['spirit_questions'] == SPIRIT_NAMES, health
    status, reply = call(url + 'spirit', json.dumps(spirit_body()).encode())
    assert status == 200 and list(reply) == ['scope', 'scope_confidence', 'intent', 'intent_confidence', 'plan', 'plan_confidence',
                                             'flags', 'flag_probabilities', 'seconds', 'input_tokens'], reply
    assert reply['intent'] == 'doubt' and reply['plan'] == 'none' and reply['flags'] == [] and reply['flag_probabilities'] == LOW, reply
    state, questions = engine.calls[-1]
    assert questions is sd.SPIRIT_QUESTIONS
    assert list(state) == ['setting', 'spirit', 'npc', 'player', 'encounter', 'spirit_said', 'recent_history', 'player_message'], list(state)
    assert state['encounter'] == dict(stage='hub', trola_mood='wary', options=['Flatter', 'Bribe', 'Threaten', 'Ask about the camp']), state
    assert state['spirit']['name'] == 'Vesper' and state['npc']['name'] == 'Trola' and state['player_message'] == 'Are you sure?'
    assert 'patience' not in json.dumps(state) and 'outcome' not in json.dumps(state), 'Patience or outcome reached d1'
    status, reply = call(url + 'spirit', json.dumps(spirit_body(message="I'll threaten him.")).encode())
    assert status == 200 and reply['plan'] == 'threaten' and reply['flags'] == ['reckless'] and reply['flag_probabilities']['reckless'] == 0.91
    status, _ = call(url + 'decide', json.dumps(dict(message='Hi.', name='Mira', business='turnips', outcome='Passed', patience=3)).encode())
    assert status == 200 and engine.calls[-1][1] is None, "Trola's /decide call changed shape"
    # Limits: exactly at the edge passes, one past fails.
    edge = spirit_body(message='m' * 80, name='n' * 80, business='b' * 160, options=['o' * 40] * 4, spirit_said='s' * 240,
                       history=[dict(speaker='Player' if i % 2 else 'Spirit', text='t' * 240) for i in range(6)])
    assert call(url + 'spirit', json.dumps(edge).encode())[0] == 200
    for stage in sd.SPIRIT_STAGES:
        for mood in sd.SPIRIT_MOODS:
            assert call(url + 'spirit', json.dumps(spirit_body(stage=stage, mood=mood, options=[], history=[])).encode())[0] == 200, (stage, mood)
    bad = [dict(message='m' * 81), dict(message='   '), dict(message=None), dict(message=7), dict(name=None), dict(name='n' * 81),
           dict(business='b' * 161), dict(stage='approach'), dict(stage='Hub'), dict(stage=None), dict(stage=['hub']), dict(mood='happy'),
           dict(mood=None), dict(options=['o'] * 5), dict(options=['o' * 41]), dict(options=[3]), dict(options='Flatter'),
           dict(spirit_said='s' * 241), dict(spirit_said=4), dict(history=[dict(speaker='Player', text='t')] * 7),
           dict(history=[dict(speaker='Trola', text='Hmph.')]), dict(history=[dict(speaker='Troll', text='Hmph.')]),
           dict(history=[dict(speaker='Player', text='t' * 241)]), dict(history=['Player: hi']), dict(history={})]
    for change in bad:
        status, error = call(url + 'spirit', json.dumps(spirit_body(**change)).encode())
        assert status == 400 and error.get('error'), (change, status, error)
    assert call(url + 'spirit', b'{not json')[0] == 400
    assert call(url + 'spirit', b'[]')[0] == 400
    assert call(url + 'spirit', json.dumps(spirit_body()).encode(), 'text/plain')[0] == 415
    assert call(url + 'spirit', b'x' * (sd.MAX_BODY + 1))[0] == 413
    assert call(url + 'spirit/')[0] == 404 and call(url + 'spirits', b'{}')[0] == 404
    # A failed thought never takes Trola off d1: health stays ready, with no error and no prime decision.
    engine.broken = True
    calls = len(engine.calls)
    assert call(url + 'spirit', json.dumps(spirit_body()).encode()) == (500, dict(error='RuntimeError'))
    assert call(url + 'health')[1] == dict(engine.health, ready=True) and len(engine.calls) == calls + 1, 'A failed thought took Trola off d1'
    engine.broken = False
    # A failed /decide still reports not ready; a good thought does not clear that, only Trola's prime does.
    failing = json.dumps(dict(message='Fail on the server.', name='Mira', business='turnips', outcome='Passed', patience=3)).encode()
    assert call(url + 'decide', failing) == (500, dict(error='RuntimeError'))
    assert call(url + 'spirit', json.dumps(spirit_body()).encode())[0] == 200 and server.failure == 'RuntimeError', server.failure
    # Trola's service and Vesper's director probe together after a failure: one prime decision, and both read ready.
    engine.prime_seconds, calls, probes = 0.2, len(engine.calls), [None, None]

    def probe(i):
        probes[i] = call(url + 'health')
    threads = [threading.Thread(target=probe, args=(i,)) for i in range(2)]
    threads[0].start()
    time.sleep(0.02)
    threads[1].start()
    for thread in threads:
        thread.join()
    engine.prime_seconds = 0
    assert probes == [(200, dict(engine.health, ready=True))] * 2, probes
    assert [state['player_message'] for state, _ in engine.calls[calls:]] == ['Greetings.'], 'Concurrent health probes primed twice'
    # A thought waits at most 1 s for the model, under the client's 2 s decision_timeout; Trola's queue is unchanged.
    slow = json.dumps(dict(message='Take longer than the client allows.', name='Mira', business='turnips', outcome='Passed', patience=3)).encode()
    stalled = threading.Thread(target=call, args=(url + 'decide', slow))
    stalled.start()
    time.sleep(0.15)
    started = time.monotonic()
    assert call(url + 'spirit', json.dumps(spirit_body()).encode()) == (503, dict(error='busy'))
    waited = time.monotonic() - started
    assert 0.9 <= waited < 1.2 and sd.SPIRIT_QUEUE_SECONDS == 1 and sd.QUEUE_SECONDS == 3, waited
    stalled.join()


def receipt_checks():
    """Spirit receipts carry kind:'spirit'; Trola's receipt lines keep exactly their old keys."""
    with tempfile.TemporaryDirectory() as folder:
        receipts = pathlib.Path(folder) / 'receipts.jsonl'
        engine = SpiritStubEngine()
        server, url = start(engine, receipts)
        printed = io.StringIO()
        try:
            with contextlib.redirect_stdout(printed):
                assert call(url + 'spirit', json.dumps(spirit_body(message="I'll threaten him.")).encode())[0] == 200
                assert call(url + 'decide', json.dumps(dict(message='Hi.', name='Mira', business='turnips', outcome='Passed', patience=3)).encode())[0] == 200
        finally:
            server.shutdown()
            server.server_close()
        spirit, trola = [json.loads(line) for line in receipts.read_text().splitlines()]
        assert list(spirit) == ['at', 'kind', 'state', 'decision'] and spirit['kind'] == 'spirit', spirit
        assert spirit['state']['player_message'] == "I'll threaten him." and spirit['decision']['plan'] == 'threaten', spirit
        assert list(trola) == ['at', 'state', 'decision'] and 'facts' in trola['decision'], trola
        # Only this server's two lines: a decision finishing on the first server may print here too.
        lines = [line for line in printed.getvalue().splitlines() if 'threaten him.' in line or line.endswith("'Hi.'")]
        assert len(lines) == 2 and ' spirit in_world/state_plan  plan=threaten  flags=reckless  ' in lines[0], lines
        assert re.search(r' ms  in_world/other  facts=-  \'Hi\.\'$', lines[1]), lines


def wire_checks(engine):
    """What the C# Clip check actually sent."""
    sent = [state for state, questions in engine.calls if questions is not None and state['player_message'].startswith('Clip check')]
    assert len(sent) == 1, [s['player_message'] for s, _ in engine.calls]
    state = sent[0]
    assert len(state['player_message']) == 80 and state['player']['name'] == 'n' * 80 and state['player']['business'] == 'b' * 160, state
    assert len(state['spirit_said']) == 240 and state['encounter']['options'] == ['o' * 40, 'Bribe', 'Threaten', 'Ask about the camp'], state
    assert [t['speaker'] for t in state['recent_history']] == ['Player', 'Spirit', 'Player', 'Spirit', 'Player', 'Spirit'], state['recent_history']
    assert 'patience' not in json.dumps(state)


def replay(adapter):
    policy = ROOT / 'Assets/XybridAvatarDemo/Runtime/SpiritCore/SpiritPolicy.cs'
    lines = ROOT / 'Assets/XybridAvatarDemo/Resources/Spirit/SpiritLines.json'
    cases = json.loads((HERE / 'decision-spirit-cases.json').read_text())
    questions = sd.spirit_questions()
    for case in cases:  # Every case is a valid /spirit request with labels from the current question set.
        sd.spirit_state_for(case['request'])
        recorded = case['recorded']
        assert recorded['scope'] in questions['scope']['criteria'] and recorded['intent'] in questions['intent']['criteria'], case['id']
        assert recorded['plan'] in questions['plan']['criteria'] and set(recorded['flag_probabilities']) == set(sd.SPIRIT_FLAGS), case['id']
        assert recorded['flags'] == [f for f in sd.SPIRIT_FLAGS if recorded['flag_probabilities'][f] >= 0.5], case['id']
    if not policy.exists() or not lines.exists():
        return f'SKIP: {len(cases)} recorded /spirit replays: SpiritPolicy.cs or Resources/Spirit/SpiritLines.json does not exist yet'
    result = adapter.ask(dict(command='spirit', suite='SpiritClientChecks', mode='replay', lines=str(lines), cases=cases))
    if result.get('skipped'):
        return f"SKIP: {len(cases)} recorded /spirit replays: {result['skipped']}"
    assert result['passed'], json.dumps(result, indent=1)
    return f"PASS: {result['checks']}"


question_set_checks()
prime_checks()
(ROOT / '.context/npc-quality').mkdir(parents=True, exist_ok=True)
adapter = JsonProcess([args.dotnet, str(args.adapter), str(STORY)], ROOT / '.context/npc-quality/spirit-d1-adapter.log')
engine, old_engine = SpiritStubEngine(), SpiritStubEngine(spirit=False)
server, url = start(engine)
old_server, old_url = start(old_engine, handler=OldHandler)
try:
    client = adapter.ask(dict(command='spirit', suite='SpiritClientChecks', url=url, old_url=old_url))
    assert client.get('passed'), client
    wire_checks(engine)
    assert all(q is None for _, q in old_engine.calls), 'The old server ran a spirit decision'
    while server.busy_since is not None:  # The client's timeout check left a stub decision running.
        time.sleep(0.05)
    server_checks(url, engine, server)
    receipt_checks()
    replayed = replay(adapter)
finally:
    adapter.close()
    for s in (server, old_server):
        s.shutdown()
        s.server_close()
print(f"PASS: spirit client against the stub server; {client['checks']} (timeout fired at {client['timeout_seconds']} s)")
print('PASS: spirit server; question set (5 names, labels, he/him wording), prime covers both sets with spirit keys last in health, '
      'validation (type, JSON, size, path, message/name/business/stage/mood/options/spirit_said/history limits, Player/Spirit only), '
      'state layout without patience, reply shape, receipts kind:spirit with Trola lines unchanged, a failed thought leaves health ready, '
      'a failed /decide recovers only through its prime, one prime for two concurrent probes, 1 s spirit queue')
print(replayed)
sys.stdout.flush()
decisions = subprocess.run([sys.executable, str(HERE / 'verify_decisions.py'), '--dotnet', args.dotnet], cwd=ROOT)
if decisions.returncode:
    sys.exit('FAIL: verify_decisions.py')
print('PASS: verify_decisions.py')
