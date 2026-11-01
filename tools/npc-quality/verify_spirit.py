#!/usr/bin/env python3
"""Run the headless spirit suites in the C# adapter; no Unity, no model.

SpiritCoreChecks (H1 advisor, H2 lines, H3 policy, H4 window, H5 keywords) also returns its advisor
table, which is compared state by state with the independent Python search in spirit_advisor_brute.py.
"""
import argparse
import json
import pathlib
import sys

import spirit_advisor_brute
from run import JsonProcess, ROOT, STORY

LINES = ROOT / 'Assets/XybridAvatarDemo/Resources/Spirit/SpiritLines.json'
HERE = pathlib.Path(__file__).resolve().parent

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--adapter', default=str(HERE / 'bin/Debug/net8.0/ScenarioChecks.dll'))
p.add_argument('--suite', action='append', help='repeatable; default: SpiritCoreChecks and SpiritAudioChecks if present')
p.add_argument('--lines', type=pathlib.Path, default=LINES, help='SpiritLines.json to validate')
args = p.parse_args()
suites = args.suite or [s for s in ('SpiritCoreChecks', 'SpiritAudioChecks') if (HERE / f'{s}.cs').exists()]

log = ROOT / '.context/npc-quality/spirit-adapter.log'
log.parent.mkdir(parents=True, exist_ok=True)
adapter = JsonProcess([args.dotnet, args.adapter, str(STORY)], log)
failed = []
try:
    for suite in suites:
        try:
            result = adapter.ask(dict(command='spirit', suite=suite, lines=str(args.lines.resolve())))
        except RuntimeError as error:
            failed.append(suite)
            print(f'FAIL {suite}: adapter exited ({error}); is the suite compiled into {args.adapter}? See {log}')
            break
        ok = bool(result.get('passed'))
        print(('PASS' if ok else 'FAIL'), suite + ':', result.get('checks', ''))
        for failure in result.get('failures') or []:
            print('  -', failure)
        if suite == 'SpiritCoreChecks':
            rows = result.get('advisor') or []
            brute = spirit_advisor_brute.table(json.loads(STORY.read_text()))
            problems = spirit_advisor_brute.compare(brute, rows)
            if not rows:
                problems.append('C# suite returned no advisor table')
            print(('PASS' if not problems else 'FAIL'), f'advisor vs spirit_advisor_brute.py: {len(brute)} states, both preferences')
            for problem in problems[:40]:
                print('  -', problem)
            ok = ok and not problems
        if not ok:
            failed.append(suite)
finally:
    adapter.close()
if not suites:
    failed.append('no suites')
    print('FAIL: no spirit suites found')
print('PASS: spirit checks' if not failed else 'FAIL: ' + ', '.join(failed))
sys.exit(1 if failed else 0)
