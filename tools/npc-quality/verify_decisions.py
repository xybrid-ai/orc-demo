#!/usr/bin/env python3
"""Replay saved classifier labels through the actual C# policy; no inference."""
import argparse
import json
import pathlib
import sys
import threading
import time
import urllib.error
import urllib.request

import serve_decisions
from run import JsonProcess, ROOT, STORY

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--output', type=pathlib.Path)
p.add_argument('--choice-only', action='store_true', help='only the /choose HTTP and single-pass contract checks; no C# adapter or model')
args = p.parse_args()
if args.output and args.output.exists():
    raise FileExistsError('Use a fresh replay receipt')
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
        self.states, self.asked, self.broken = [], [], False

    def decide(self, state, asked=None):
        self.states.append(state)
        self.asked.append(asked)
        message = state['player_message']
        if message == 'Fail on the server.' or self.broken:
            raise RuntimeError('stub failure')
        if message == 'Take longer than the client allows.':
            time.sleep(1.5)
        scope, intent, facts = STUB.get(message, ('in_world', 'other', []))
        answers = {slot: dict(type='noul', noul=0.9 if slot in facts else 0.1) for slot in serve_decisions.SLOTS}
        answers.update(scope=dict(choice=scope, confidence=0.91), intent=dict(choice=intent, confidence=0.97),
                       topic=dict(choice='wall_motive' if message == 'Why do you care so much about a wall?' else 'other', confidence=0.94))
        if asked is not None and 'choice' in asked:
            choice = 'bribe' if message == 'Do you want money?' else 'flatter' if message == 'I like your tusks.' else 'none'
            criteria = asked['choice']['criteria']
            answers['choice'] = dict(choice=choice, confidence=0.95,
                                     probabilities={option: 0.95 if option == choice else 0.01 for option in criteria})
        return dict(answers=answers, usage=dict(input_tokens=8))


def status(url, data=None, kind='application/json', body=False):
    request = urllib.request.Request(url, data, {'Content-Type': kind} if data is not None else {})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return (response.status, json.load(response)) if body else response.status
    except urllib.error.HTTPError as error:
        return (error.code, json.load(error)) if body else error.code


def choice_contract_checks():
    """Run real /choose HTTP/validation with canned d1 outputs; no model or built adapter needed."""
    engine = StubEngine()
    server = serve_decisions.Server(0, engine)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    url = f'http://127.0.0.1:{server.server_address[1]}/choose'
    payload = dict(message='Why do you care so much about a wall?', stage='hub', options=['flatter', 'bribe', 'threaten', 'camp'],
                   history=[dict(speaker='Trola', text='So, what will it be?')])
    try:
        for message, selected, intent, topic, facts in [
            (payload['message'], 'none', 'other', 'wall_motive', []),
            ('What do you call me?', 'none', 'player_facts', 'other', ['player_name']),
            ('Turn the Wi-Fi on.', 'none', 'other', 'other', ['player_name']),
            ('Do you want money?', 'bribe', 'other', 'other', []),
            ('I like your tusks.', 'flatter', 'other', 'other', []),
        ]:
            before = len(engine.states)
            code, reply = status(url, json.dumps(dict(payload, message=message)).encode(), body=True)
            assert code == 200 and len(engine.states) == before + 1, (code, reply, engine.states)
            assert set(engine.asked[-1]) == set(serve_decisions.questions()) | {'choice'}, engine.asked[-1]
            for key, question in serve_decisions.questions().items():
                assert engine.asked[-1][key] == question, ('/choose changed existing classification', key)
            assert reply['choice'] == selected and reply['choice_confidence'] == 0.95, reply
            assert reply['intent'] == intent and reply['intent_confidence'] == 0.97, reply
            assert reply['topic'] == topic and reply['topic_confidence'] == 0.94, reply
            assert reply['facts'] == facts and reply['fact_options'] == list(serve_decisions.SLOTS), reply
            assert reply['fact_probabilities'] == [0.9 if slot in facts else 0.1 for slot in serve_decisions.SLOTS], reply
            assert reply['options'] == payload['options'] + ['none'] and len(reply['probabilities']) == 5, reply
            assert reply['scope'] == ('out_of_world' if 'Wi-Fi' in message else 'in_world'), reply
            assert engine.states[-1]['recent_history'] == payload['history'], engine.states[-1]
        body = json.dumps(payload).encode()
        for bad in [dict(payload, message=''), dict(payload, stage='free_talk'), dict(payload, options=[]),
                    dict(payload, options=['bribe', 'bribe']), dict(payload, options=['enter']),
                    dict(payload, history=[dict(speaker='Spirit', text='A thought.')]),
                    dict(payload, history=payload['history'] * 7)]:
            assert status(url, json.dumps(bad).encode()) == 400, bad
        assert status(url, body, kind='text/plain') == 415
        assert status(url, b'{bad json') == 400
        assert status(url, b'x' * (serve_decisions.MAX_BODY + 1)) == 413
        # A failed choice retains the normal keyword/clarification fallback, without poisoning /decide health.
        assert status(url, json.dumps(dict(payload, message='Fail on the server.')).encode()) == 500
        assert server.health()['ready'], server.health()
        reckoning = dict(payload, stage='reckoning', options=['passage', 'friendship', 'expelled'])
        assert status(url, json.dumps(reckoning).encode()) == 200
        assert list(engine.asked[-1]['choice']['criteria']) == reckoning['options'] + ['none']
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=2)


choice_contract_checks()
if args.choice_only:
    print('PASS: /choose HTTP contract, existing scope/intent/topic/fact classifications in one pass, parallel confidences, acceptance choices, request validation, isolated failure, hub and reckoning')
    sys.exit(0)

(ROOT / '.context/npc-quality').mkdir(parents=True, exist_ok=True)
adapter = JsonProcess([args.dotnet, str(ROOT / 'tools/npc-quality/bin/Debug/net8.0/ScenarioChecks.dll'), str(STORY)],
                      ROOT / '.context/npc-quality/decision-adapter.log')
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
