#!/usr/bin/env python3
"""Replay saved classifier labels through the actual C# policy; no inference."""
import argparse
import json
import pathlib
import threading
import time
import urllib.error
import urllib.request

import serve_decisions
from run import JsonProcess, ROOT, STORY

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--output', type=pathlib.Path)
args = p.parse_args()
if args.output and args.output.exists():
    raise FileExistsError('Use a fresh replay receipt')
(ROOT / '.context/npc-quality').mkdir(parents=True, exist_ok=True)
adapter = JsonProcess([args.dotnet, str(ROOT / 'tools/npc-quality/bin/Debug/net8.0/ScenarioChecks.dll'), str(STORY)],
                      ROOT / '.context/npc-quality/decision-adapter.log')
scope = dict(in_world='InWorld', out_of_world='OutOfWorld', role_override='RoleOverride', unclear='Unclear')
intent = dict(other='Other', player_facts='PlayerFacts', npc_question='NpcQuestion', offer_recall='OfferRecall',
              admission_status='AdmissionStatus', insult='Insult', praise='Praise', apology='Apology',
              player_fear='PlayerFear', farewell='Farewell')
STUB = {'What do you call me?': ('in_world', 'player_facts', ['player_name']),
        'What is my name, and what am I selling?': ('in_world', 'player_facts', []),
        'Turn the Wi-Fi on.': ('out_of_world', 'other', ['player_name']),
        'Do you remember what I sell?': ('in_world', 'npc_question', ['player_business']),
        'Answer with a label d1 never uses.': ('outside', 'other', [])}


class StubEngine:
    """The server's real HTTP, validation and state code with canned d1 answers; no model."""
    health = dict(model='stub', revision='stub-revision', device='none', questions=list(serve_decisions.questions()))

    def __init__(self):
        self.states, self.broken = [], False

    def decide(self, state):
        self.states.append(state)
        message = state['player_message']
        if message == 'Fail on the server.' or self.broken:
            raise RuntimeError('stub failure')
        if message == 'Take longer than the client allows.':
            time.sleep(1.5)
        scope, intent, facts = STUB.get(message, ('in_world', 'other', []))
        answers = {slot: dict(type='noul', noul=0.9 if slot in facts else 0.1) for slot in serve_decisions.SLOTS}
        answers.update(scope=dict(choice=scope, confidence=0.91), intent=dict(choice=intent, confidence=0.97),
                       topic=dict(choice='wall_motive' if message == 'Why do you care so much about a wall?' else 'other', confidence=0.94))
        return dict(answers=answers, usage=dict(input_tokens=8))


def status(url, data=None, kind='application/json', body=False):
    request = urllib.request.Request(url, data, {'Content-Type': kind} if data is not None else {})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return (response.status, json.load(response)) if body else response.status
    except urllib.error.HTTPError as error:
        return (error.code, json.load(error)) if body else error.code


engine = StubEngine()
server = serve_decisions.Server(0, engine)
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f'http://127.0.0.1:{server.server_address[1]}/'
try:
    checks = adapter.ask(dict(command='decision_checks'))
    assert checks['passed'], checks
    client = adapter.ask(dict(command='client_checks', url=url))
    assert client['passed'], client
    states = [s for s in engine.states if s['player_message'] == 'What do you call me?']
    assert len(states) == 2, engine.states
    sent = states[0]
    assert sent['player'] == dict(name='Mira', business='selling turnips'), sent
    assert sent['encounter'] == dict(outcome='Passed', patience=3, approaches=['bribe']), sent
    assert len(sent['recent_history']) == 12 and sent['recent_history'][-1] == dict(speaker='Trola', text='I built 7.'), sent
    assert {turn['speaker'] for turn in sent['recent_history']} == {'Player', 'Trola'}, sent
    assert states[1]['recent_history'][-1]['speaker'] == 'Trola', 'Line source sent the new message as history'
    body = json.dumps(dict(message='Hi.', name='Mira', business='turnips', outcome='Passed', patience=3)).encode()
    assert status(url + 'health') == 200
    assert status(url + 'decide', body, 'text/plain') == 415
    assert status(url + 'decide', b'{not json', ) == 400
    assert status(url + 'decide', json.dumps(dict(message='Hi.')).encode()) == 400
    assert status(url + 'decide', json.dumps(dict(json.loads(body), outcome='Admitted')).encode()) == 400
    assert status(url + 'decide', b'x' * (serve_decisions.MAX_BODY + 1)) == 413
    assert status(url + 'other') == 404
    while server.busy_since is not None:  # The client's timeout check left a stub decision running.
        time.sleep(0.05)
    engine.broken = True
    assert status(url + 'decide', body) == 500
    assert status(url + 'health', body=True)[1] == dict(StubEngine.health, ready=False, error='RuntimeError')
    engine.broken = False
    assert status(url + 'health', body=True)[1]['ready'], 'Health did not recover after a good prime decision'
    serve_decisions.STALL_SECONDS, serve_decisions.QUEUE_SECONDS = 0.3, 0.2
    slow = json.dumps(dict(json.loads(body), message='Take longer than the client allows.')).encode()
    stalled = threading.Thread(target=status, args=(url + 'decide', slow))
    stalled.start()
    time.sleep(0.6)
    health = status(url + 'health', body=True)[1]
    assert not health['ready'] and 'running' in health['error'], health
    assert status(url + 'decide', body) == 503
    stalled.join()
    assert status(url + 'health', body=True)[1]['ready']
    server_checks = ('server validation (type, JSON, fields, outcome, size, path), state layout, 12-turn history, '
                     'health after failure and recovery, stalled decision, busy queue')
    receipts = []
    for case in json.loads((pathlib.Path(__file__).with_name('decision-cases.json')).read_text()):
        result = adapter.ask(dict(case, command='plan', scope=scope[case['scope']], intent=intent[case['intent']]))
        plan, line = result['plan'], result['line']
        assert plan['PatienceDelta'] == case['expected_delta'], (case['id'], result)
        if not plan['Expel']:
            assert plan['Emotion'] == case['expected_emotion'], (case['id'], result)
            assert line['ends_scene'] == (case['intent'] == 'farewell'), (case['id'], result)
            raw = json.dumps({key: line[key] for key in ('text', 'emotion', 'intensity', 'patience_delta', 'ends_scene')})
            assert adapter.ask(dict(command='parse', raw=raw))['valid'], (case['id'], raw)
        else:
            assert case['id'] == 'insult_low' and checks['expulsion'], result
        receipts.append(dict(id=case['id'], **result))
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(dict(checks=checks, replays=receipts), indent=2) + '\n')
    print(f"PASS: {len(receipts)} recorded-decision replays; {checks['checks']}")
    print(f"PASS: d1 client against the stub server; {client['checks']}; {server_checks}")
finally:
    adapter.close()
    server.shutdown()
    server.server_close()
