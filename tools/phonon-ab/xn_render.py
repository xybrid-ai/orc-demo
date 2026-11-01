"""Render corpus items with the upstream xn-ptts Python wheel in one process.

Usage: XN_KAI=0 python -I xn_render.py <label> <checkpoint> <corpus.jsonl> <out root> name=voice.safetensors[,...] 1,2,3
"""
import json, sys, pathlib, ptts
label, model, corpus, out = sys.argv[1], sys.argv[2], pathlib.Path(sys.argv[3]), pathlib.Path(sys.argv[4])
voices = dict(a.split('=', 1) for a in sys.argv[5].split(','))
seeds = [int(s) for s in sys.argv[6].split(',')]
ptts.set_num_threads(6)
t = ptts.TTS(model, device='cpu', quant='q8', lang='en', temperature=0.3)
for n, p in voices.items():
    t.add_voice(n, p)
root = out / label
root.mkdir(parents=True, exist_ok=True)
(root / 'build_info.json').write_text(json.dumps({'build': ptts.build_info(), 'quant': t.quant, 'voices': voices}, default=str))
items = [json.loads(l) for l in corpus.open()]
for v in voices:
    for s in seeds:
        d = root / v / f's{s}'
        d.mkdir(parents=True, exist_ok=True)
        for it in items:
            w = d / (it['id'] + '.wav')
            if not w.exists():
                t.save(str(w), it['text'], voice=v, seed=s)
