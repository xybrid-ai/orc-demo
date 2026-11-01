#!/usr/bin/env python3
"""Focused d1/MPS feasibility check. No writer, Unity, voice, or CPU fallback."""
import argparse
import hashlib
import json
import os
import pathlib
import platform
import subprocess
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--model', type=pathlib.Path, required=True)
p.add_argument('--output', type=pathlib.Path, required=True)
args = p.parse_args()
root = pathlib.Path(__file__).resolve().parent
args.output.mkdir(parents=True, exist_ok=False)
manifest = json.loads((root / 'decision-model.json').read_text())
for entry in manifest['files']:
    file = args.model / entry['path']
    with file.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if file.stat().st_size != entry['size'] or digest != entry['sha256']:
        raise ValueError('Pinned model verification failed: ' + entry['path'])

os.environ['TOKENIZERS_PARALLELISM'] = 'false'
os.environ.pop('PYTORCH_ENABLE_MPS_FALLBACK', None)
import torch
import transformers
from transformers import AutoModel

if not torch.backends.mps.is_available():
    raise RuntimeError('MPS unavailable; refusing to report CPU timing as GPU timing')
questions = json.loads((root / 'decision-questions.json').read_text())
cases = json.loads((root / 'decision-cases.json').read_text())


def state_for(case):
    return dict(setting='A fantasy camp gate. Modern devices and Wi-Fi are outside this world.',
                npc=dict(name='Trola', role='troll gatekeeper', personality='Proud wall builder; dry jokes, gruff, grudging respect.'),
                player=dict(name=case.get('name', 'Mira'), business=case.get('business', 'selling turnips')),
                encounter=dict(outcome=case.get('outcome', 'Passed'), patience=case.get('patience', 3),
                               approaches=case.get('approaches', ['bribe'])),
                recent_history=case.get('history', [])[-12:], player_message=case['message'])


started = time.perf_counter()
model = AutoModel.from_pretrained(str(args.model), trust_remote_code=True, dtype=torch.bfloat16).to('mps').eval()
torch.mps.synchronize()
metadata = dict(revision=manifest['revision'], hashes_verified=True, device='mps', dtype='bfloat16',
                torch=torch.__version__, transformers=transformers.__version__, python=platform.python_version(),
                hardware=subprocess.check_output(['sysctl', '-n', 'machdep.cpu.brand_string'], text=True).strip(),
                load_seconds=time.perf_counter() - started,
                note='Original two questions, one prime and four sequential cases. No co-resident writer or speech timing.')
(args.output / 'metadata.json').write_text(json.dumps(metadata, indent=2) + '\n')
with torch.inference_mode(), (args.output / 'receipts.jsonl').open('x') as output:
    selected = [case for case in cases if case['id'] in ('wifi', 'player_facts', 'refused', 'insult_calm')]
    for case in [dict(id='prime', message='Greetings.')] + selected:
        torch.mps.synchronize()
        started = time.perf_counter()
        result = model.system_one(state_for(case), questions)
        torch.mps.synchronize()
        receipt = dict(id=case['id'], seconds=time.perf_counter() - started, result=result,
                       mps_allocated_bytes=torch.mps.current_allocated_memory(), mps_driver_bytes=torch.mps.driver_allocated_memory())
        output.write(json.dumps(receipt) + '\n')
        output.flush()
        print(json.dumps(receipt), flush=True)
