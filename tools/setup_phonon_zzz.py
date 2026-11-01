#!/usr/bin/env python3
"""Build pinned zzz v0.1.0-rc.9 and convert the supplied Phonon checkpoint and clone."""
import argparse
import hashlib
import importlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / 'tools/phonon-zzz-native'
REVISION = '5de2a037896a874f75efbdaf683888c5e3156020'  # v0.1.0-rc.9
WORK = ROOT / '.context/phonon-zzz-rc9'
PLUGIN = ROOT / 'Assets/XybridAvatarDemo/Plugins/macos-arm64/liborc_phonon_zzz.dylib'
MODEL = ROOT / 'Assets/StreamingAssets/PhononZzz'


def run(args, cwd=None):
    subprocess.run(args, cwd=cwd, check=True)


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', type=Path, required=True)
    parser.add_argument('--voice', type=Path, required=True)
    parser.add_argument('--engine-checkout', type=Path, default=WORK / 'engine')
    parser.add_argument('--zig', default=shutil.which('zig') or '/opt/homebrew/bin/zig')
    parser.add_argument('--skip-build', action='store_true')
    args = parser.parse_args()
    if sys.platform != 'darwin' or os.uname().machine != 'arm64':
        parser.error('This trial requires Apple Silicon macOS.')
    for path in [args.voice] + [args.checkpoint / n for n in ['config.json', 'tokenizer.json', 'model.q8.gguf']]:
        if not path.is_file():
            parser.error('Missing supplied asset: ' + str(path))
    engine = args.engine_checkout.resolve()
    if not (engine / '.git').exists():
        engine.parent.mkdir(parents=True, exist_ok=True)
        run(['git', 'clone', '--filter=blob:none', '--no-checkout',
             'https://github.com/xybrid-ai/zzz-research.git', str(engine)])
        run(['git', 'fetch', '--depth=1', 'origin', REVISION], engine)
        run(['git', 'checkout', '--detach', REVISION], engine)
    if subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=engine, text=True).strip() != REVISION:
        parser.error('Engine checkout must be pinned to ' + REVISION)
    run(['git', 'diff', '--exit-code', 'HEAD'], engine)
    if not args.skip_build:
        if subprocess.check_output([args.zig, 'version'], text=True).strip() != '0.16.0':
            parser.error('Use Zig 0.16.0 stable.')
        # The engine sources stay at the pinned revision. Only an untracked bridge
        # is added so its relative imports share the upstream module identity.
        shutil.copyfile(NATIVE / 'bridge.zig', engine / 'src/orc_phonon_bridge.zig')
        run([args.zig, 'fmt', '--check', str(NATIVE / 'build.zig'), str(NATIVE / 'bridge.zig')])
        run([args.zig, 'build', '-Doptimize=ReleaseFast', '-Dtarget=aarch64-macos.13.0',
             '-Dengine-path=' + str(engine), '-j2'], NATIVE)
    library = NATIVE / 'zig-out/lib/liborc_phonon_zzz.dylib'
    if subprocess.check_output(['lipo', '-archs', str(library)], text=True).strip() != 'arm64':
        parser.error('Expected ARM64 library.')
    exports = subprocess.check_output(['nm', '-gU', str(library)], text=True)
    for name in ['abi_version', 'open', 'synthesize', 'cancel', 'close']:
        if '_orc_phonon_zzz_' + name not in exports:
            parser.error('Missing native export: ' + name)
    stage = WORK / 'source'
    (stage / 'voices').mkdir(parents=True, exist_ok=True)
    for name in ['config.json', 'tokenizer.json', 'model.q8.gguf']:
        shutil.copyfile(args.checkpoint / name, stage / name)
    # Pin the locally supplied clone's digest while retaining every upstream
    # model/config/tokenizer, encoder and speaker-latent validation.
    filename = 'clone.ba7a5a45@300.safetensors'
    shutil.copyfile(args.voice, stage / 'voices' / filename)
    sys.path.insert(0, str(engine / 'scripts'))
    converter = importlib.import_module('convert_phonon_to_gguf')
    MODEL.mkdir(parents=True, exist_ok=True)
    # The public phonon-31m-en is the default; the 7e71a02d@200 drop remains convertible for rollback.
    if sha(args.checkpoint / 'config.json') == converter.PUBLIC_SOURCE_CONFIG_SHA256:
        if importlib.util.find_spec('numpy') is None:
            parser.error('The 31m conversion needs NumPy: uv run --no-project --python 3.12 --with numpy python tools/setup_phonon_zzz.py ...')
        converter.PUBLIC_VOICE_SOURCES = {**converter.PUBLIC_VOICE_SOURCES,
                                          'trola_clone': ('voices/' + filename, sha(args.voice))}
        manifest = converter.convert_public(stage, MODEL / 'model.gguf', ['trola_clone'])
    else:
        converter.LATENTS_VOICE_SOURCES = {'trola_clone': (filename, sha(args.voice))}
        manifest = converter.convert_speaker_latents(stage, MODEL / 'model.gguf', ['trola_clone'])
    PLUGIN.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(library, PLUGIN)
    meta = PLUGIN.with_suffix('.dylib.meta')
    meta.write_text('''fileFormatVersion: 2
guid: 5cc781bfe5314cba935d12bdca14bbed
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: ARM64
        DefaultValueInitialized: true
        OS: OSX
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: ARM64
  userData:
  assetBundleName:
  assetBundleVariant:
''')
    (MODEL / 'local-provenance.json').write_text(json.dumps({
        'engineRevision': REVISION, 'zig': '0.16.0', 'backend': 'cpu', 'threads': 6,
        'bundleRelease': manifest['bundle_release'], 'textFrontend': manifest['text_frontend'],
        'pluginSha256': sha(PLUGIN), 'bundleSha256': sha(MODEL / 'model.gguf'),
    }, indent=2) + '\n')
    print('Staged pinned zzz rc.9 Phonon (' + manifest['bundle_release'] + ') and cloned voice. Private assets remain ignored.')


if __name__ == '__main__':
    main()
