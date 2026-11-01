"""Convert an A/B bundle with the pinned zzz converter, adding the Trola clone.

Usage: python -I convert_ab.py <zzz checkout> public|latents <stage> <out.gguf> [<phonon-voices folder>]
The stage holds config.json, tokenizer.json, model.q8.gguf and voices/clone.ba7a5a45@300.safetensors.
"""
import hashlib, importlib, json, sys
from pathlib import Path
engine, kind, stage, out = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3]), Path(sys.argv[4])
sys.path.insert(0, str(engine / 'scripts'))
c = importlib.import_module('convert_phonon_to_gguf')
f = 'clone.ba7a5a45@300.safetensors'
d = hashlib.sha256((stage / 'voices' / f).read_bytes()).hexdigest()
if kind == 'public':
    c.PUBLIC_VOICE_SOURCES = {**c.PUBLIC_VOICE_SOURCES, 'trola_clone': ('voices/' + f, d)}
    m = c.convert_public(stage, out, ['trola_clone', 'freya', 'grub_the_ogre'], Path(sys.argv[5]))
else:
    c.LATENTS_VOICE_SOURCES = {**c.LATENTS_VOICE_SOURCES, 'trola_clone': (f, d)}
    m = c.convert_speaker_latents(stage, out, ['trola_clone', 'freya'])
print(json.dumps({k: m[k] for k in ('bundle_profile', 'bundle_release', 'text_frontend')}), [v['name'] for v in m['voices']])
