#!/usr/bin/env python3
"""Stage Vesper's stock Phonon voice (gradium/phonon-voices, Apache-2.0) for the spirit's xn-ptts engine.

Downloads one pinned voice file (or takes --from PATH), checks its size and SHA-256, copies it to
Assets/StreamingAssets/Phonon/spirit.safetensors and writes Phonon/spirit-provenance.json. Standard library only.
The Phonon checkpoint must already be staged by tools/setup_phonon.py; Trola's files are never touched.
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
REPO = 'gradium/phonon-voices'
REVISION = 'da6c9ec0d47be8df5c87c730a3aa97187f3229c3'  # The revision zzz rc.9's voice catalog pins.
BYTES = 256136
LICENSE = 'Apache-2.0'
# key: (file id, catalogue name, SHA-256 from the Hugging Face tree API at REVISION)
VOICES = {
    'bryony': ('gfP44RlHwr2kEwk7', 'Bryony', 'bcc207a73b7885e45b49ab633d3377a10158292e3a7cef9cd6764ad107f1cb9b'),
    'delia': ('nGzMHMPAWGaJFHOo', 'Delia', 'a0c3dcb91b2247eca56536be9ae8e8becc1c807b1136c5fe101552d6a155b24a'),
    'fairy_godmother': ('QXNr0SvkukLB4wPq', 'Fairy Godmother', 'f390c09f5e279c0f2171995a534b92f5ca5c83443906879da68fc27445f89d89'),
    'morwenna': ('nOykTmcH9ydsGGIc', 'Morwenna of the Deep', '2ff2db91e70648623df8d42c0dcd7c336b2f14cb4362d602a20f292a4d862154'),
    'snow_queen': ('NIWoEurJHMJjyUTe', 'The Snow Queen', 'bc8a8fcb95bb4f32d2d4376aa69d81b186b3ba2f8e9b6cb6a403b2b194590e85'),
    'hadley': ('bXwrkHhURL1VFJdG', 'Hadley', 'a879938aa9a2df0a8220bdadef88137929f71e887bd91e7db205bd5fa11661d2'),
}
CHECKPOINT_FILES = ['config.json', 'tokenizer.json', 'model.q8.gguf']


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1 << 16), b''):
            digest.update(block)
    return digest.hexdigest()


def verify(path, expected):
    """None when the file is the pinned voice, otherwise the reason it is not."""
    if not path.is_file():
        return 'missing: ' + str(path)
    size = path.stat().st_size
    if size != BYTES:
        return f'{path} is {size:,} bytes, expected {BYTES:,}'
    actual = sha256(path)
    if actual != expected:
        return f'{path} has SHA-256 {actual}, expected {expected}'
    return None


def download(url, destination, expected):
    destination.parent.mkdir(parents=True, exist_ok=True)
    handle, partial = tempfile.mkstemp(prefix=destination.name + '.', suffix='.part', dir=destination.parent)
    os.close(handle)
    partial = Path(partial)
    try:
        request = urllib.request.Request(url, headers={'User-Agent': 'orc-demo-setup-spirit-voice'})
        with urllib.request.urlopen(request, timeout=60) as response, partial.open('wb') as out:
            shutil.copyfileobj(response, out)
        problem = verify(partial, expected)
        if problem:
            raise SystemExit('Downloaded voice failed verification: ' + problem)
        os.chmod(partial, 0o644)  # mkstemp creates 0600.
        os.replace(partial, destination)
    finally:
        if partial.exists():
            partial.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--voice', choices=sorted(VOICES), default='bryony', help='Stock voice to stage (default: bryony).')
    parser.add_argument('--from', dest='source', type=Path, help='Use this local copy instead of downloading (still size- and SHA-checked).')
    parser.add_argument('--dry-run', action='store_true', help='Verify size and SHA, print the plan, copy nothing.')
    parser.add_argument('--project-root', type=Path, default=ROOT, help='Unity project to stage into (default: this checkout).')
    args = parser.parse_args()

    file_id, name, expected = VOICES[args.voice]
    root = args.project_root.resolve()
    phonon = root / 'Assets/StreamingAssets/Phonon'
    target = phonon / 'spirit.safetensors'
    receipt = phonon / 'spirit-provenance.json'
    relative = f'voices/{file_id}.safetensors'
    url = f'https://huggingface.co/{REPO}/resolve/{REVISION}/{relative}'
    cache = root / '.context/phonon-voices' / relative
    missing = [f for f in CHECKPOINT_FILES if not (phonon / f).is_file()]
    blocked = ('Phonon checkpoint missing in ' + str(phonon) + ' (' + ', '.join(missing) + '). '
               'Stage it first with tools/setup_phonon.py (see docs/phonon-integration.md).') if missing else None

    source = args.source.resolve() if args.source else cache
    if args.source is not None:
        problem = verify(source, expected)
        if problem:
            parser.error(f'--from is not {name} ({args.voice}) at {REVISION[:8]}: {problem}')
        origin = 'local copy ' + str(source)
    elif source.is_file() and verify(source, expected) is None:
        origin = 'cached download ' + str(source)
    else:
        origin = 'download ' + url

    print(f'Voice: {name} ({args.voice}), {REPO}@{REVISION[:8]} {relative}, {LICENSE}')
    print(f'Source: {origin}')
    print(f'Target: {target}')
    if args.dry_run:
        if origin.startswith('download'):
            print(f'Would download {BYTES:,} bytes to {cache} and check SHA-256 {expected}.')
        else:
            print(f'Verified: {BYTES:,} bytes, SHA-256 {expected}.')
        print('Would copy it to ' + str(target) + ' and write ' + str(receipt) + '.')
        if blocked:
            print('A real run would stop here: ' + blocked)
        print('Dry run: nothing copied.')
        return

    if blocked:
        raise SystemExit(blocked)
    if origin.startswith('download'):
        print('Downloading...')
        download(url, cache, expected)
    # Copy to a temporary name, re-verify, then swap in, so Unity never sees a partial voice.
    partial = target.with_name(target.name + '.partial')
    shutil.copyfile(source, partial)
    problem = verify(partial, expected)
    if problem:
        partial.unlink()
        raise SystemExit('Copy failed verification: ' + problem)
    os.replace(partial, target)
    provenance = {
        'repo': REPO, 'revision': REVISION, 'file': relative, 'sha256': expected, 'bytes': BYTES,
        'name': name, 'key': args.voice, 'license': LICENSE, 'url': url,
        'staged': 'Assets/StreamingAssets/Phonon/spirit.safetensors', 'engine': 'xn-ptts 0.6.0 (liborc_phonon)',
        'source': 'local copy' if args.source is not None else 'huggingface',
    }
    receipt.write_text(json.dumps(provenance, indent=2) + '\n')
    print(f'Staged {name} as the spirit voice. Assets stay gitignored.')
    print('Next: python3 tools/local_assets.py, then press Play; F4 shows "Spirit: ... ' + name + ' / xn-ptts 0.6.0; ...".')


if __name__ == '__main__':
    main()
