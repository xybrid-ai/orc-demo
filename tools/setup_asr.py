#!/usr/bin/env python3
"""Stage the player's speech recognizer: Whisper (MIT) as a whisper.cpp GGML bundle for Xybrid's GgmlWhisper runtime.

Downloads the pinned model (or takes --from PATH), checks its size and SHA-256, and writes
Assets/StreamingAssets/XybridModels/whisper-<model>/ with model.bin, model_metadata.json and provenance.json.
Optional, like Vesper's voices: without it the game has no speech input and typing works as before.
Standard library only.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
REPO = 'ggerganov/whisper.cpp'
REVISION = '5359861c739e955e79d9a303bcbc70fb988958b1'
LICENSE = 'MIT'
# key: (file, bytes, SHA-256 from the Hugging Face tree API at REVISION, parameters). base.en is the default: tiny.en
# misses names and small.en costs about three times the GPU time for a better hit rate on them.
MODELS = {
    'base-en': ('ggml-base.en-q5_1.bin', 59721011, '4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f', 74000000),
    'small-en': ('ggml-small.en-q5_1.bin', 190098681, 'bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30', 244000000),
}


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            digest.update(block)
    return digest.hexdigest()


def verify(path, size, expected):
    """None when the file is the pinned model, otherwise the reason it is not."""
    if not path.is_file():
        return 'missing: ' + str(path)
    if path.stat().st_size != size:
        return f'{path} is {path.stat().st_size:,} bytes, expected {size:,}'
    actual = sha256(path)
    return None if actual == expected else f'{path} has SHA-256 {actual}, expected {expected}'


def fetch(url, destination, size, expected):
    handle, partial = tempfile.mkstemp(prefix=destination.name + '.', suffix='.part', dir=destination.parent)
    os.close(handle)
    partial = Path(partial)
    try:
        request = urllib.request.Request(url, headers={'User-Agent': 'orc-demo-setup-asr'})
        with urllib.request.urlopen(request, timeout=60) as response, partial.open('wb') as out:
            shutil.copyfileobj(response, out, 1 << 20)
        problem = verify(partial, size, expected)
        if problem:
            raise SystemExit('Downloaded model failed verification: ' + problem)
        os.chmod(partial, 0o644)
        os.replace(partial, destination)
    finally:
        if partial.exists():
            partial.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--model', choices=sorted(MODELS), default='base-en')
    parser.add_argument('--from', dest='source', type=Path, help='use an already downloaded model file instead')
    args = parser.parse_args()
    name, size, expected, parameters = MODELS[args.model]
    folder = ROOT / 'Assets/StreamingAssets/XybridModels' / ('whisper-' + args.model)
    folder.mkdir(parents=True, exist_ok=True)
    model = folder / 'model.bin'
    if verify(model, size, expected) is None:
        print('Already staged:', model)
    elif args.source:
        problem = verify(args.source, size, expected)
        if problem:
            raise SystemExit('--from failed verification: ' + problem)
        partial = model.with_name('model.bin.partial')
        shutil.copyfile(args.source, partial)
        os.replace(partial, model)
    else:
        url = f'https://huggingface.co/{REPO}/resolve/{REVISION}/{name}'
        print('Downloading', url)
        fetch(url, model, size, expected)
    metadata = {
        'model_id': 'whisper-' + args.model,
        'version': '1.0',
        'description': f'Whisper {args.model.replace("-", ".")} GGML Q5_1: the player speech recognizer, via whisper.cpp',
        # audio_ctx 0: no encoder truncation, which makes the decoder loop on short clips.
        'execution_template': {'type': 'GgmlWhisper', 'model_file': 'model.bin', 'language': 'en', 'audio_ctx': 0, 'translate': False},
        'preprocessing': [],
        'postprocessing': [],
        'files': ['model.bin'],
        'metadata': {'task': 'speech-recognition', 'family': 'openai', 'language': 'english', 'license': LICENSE,
                     'runtime': 'whispercpp', 'quantization': 'q5_1', 'sample_rate': 16000, 'parameters': parameters},
    }
    (folder / 'model_metadata.json').write_text(json.dumps(metadata, indent=2) + '\n')
    (folder / 'provenance.json').write_text(json.dumps({'repo': REPO, 'revision': REVISION, 'file': name, 'bytes': size,
                                                         'sha256': expected, 'license': LICENSE}, indent=2) + '\n')
    print('Staged', folder.relative_to(ROOT), f'({size / 1e6:.0f} MB). The game loads it at startup; hold Left Option to talk.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
