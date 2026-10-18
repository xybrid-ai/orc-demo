#!/usr/bin/env python3
"""Build the pinned CPU bridge and stage separately supplied Phonon files locally."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / 'tools/phonon-native'
PLUGIN = ROOT / 'Assets/XybridAvatarDemo/Plugins/macos-arm64/liborc_phonon.dylib'
MODEL = ROOT / 'Assets/StreamingAssets/Phonon'


def sha(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', type=Path, required=True)
    parser.add_argument('--voice', type=Path, required=True)
    parser.add_argument('--skip-build', action='store_true', help='Stage an already-built release plugin.')
    args = parser.parse_args()
    if sys.platform != 'darwin' or os.uname().machine != 'arm64':
        parser.error('This plugin recipe requires an Apple Silicon Mac.')
    required = [args.checkpoint / name for name in ['config.json', 'tokenizer.json', 'model.q8.gguf']]
    for path in required + [args.voice]:
        if not path.is_file():
            parser.error('Missing supplied file: ' + str(path))
    config = json.loads(required[0].read_text())
    if not config:
        parser.error('Empty checkpoint config.')
    # ptts performs encoder/config/dtype/shape checks when registering this voice.
    if not args.skip_build:
        env = dict(os.environ, MACOSX_DEPLOYMENT_TARGET='13.0')
        subprocess.run([str(Path.home() / '.cargo/bin/cargo'), '+1.98.0', 'build', '--release', '--locked', '-j', '2',
                        '--manifest-path', str(NATIVE / 'Cargo.toml')], check=True, env=env)
    library = NATIVE / 'target/release/liborc_phonon.dylib'
    if not library.is_file():
        parser.error('Build the native bridge before --skip-build.')
    arch = subprocess.check_output(['lipo', '-archs', str(library)], text=True).strip()
    if arch != 'arm64':
        parser.error('Expected ARM64-only plugin; found ' + arch)
    exports = subprocess.check_output(['nm', '-gU', str(library)], text=True)
    for symbol in ['abi_version', 'open', 'synthesize', 'cancel', 'close']:
        if '_orc_phonon_' + symbol not in exports:
            parser.error('Missing native export: ' + symbol)
    PLUGIN.parent.mkdir(parents=True, exist_ok=True)
    MODEL.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(library, PLUGIN)
    # Regular copies, never links into private Downloads or the original checkpoint.
    for source in required:
        destination = MODEL / source.name
        if source.resolve() != destination.resolve():
            shutil.copyfile(source, destination)
    destination = MODEL / 'voice.safetensors'
    if args.voice.resolve() != destination.resolve():
        shutil.copyfile(args.voice, destination)
    # Keep this importer stable on re-staging, even though plugin artifacts are gitignored.
    meta = PLUGIN.with_suffix(PLUGIN.suffix + '.meta')
    if not meta.exists():
        meta.write_text('''fileFormatVersion: 2
guid: 824886b886ef4bc2a2b10df296c18ad9
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
    receipt = {'runtime': 'ptts 0.4.0-rc.1', 'backend': 'cpu', 'threads': 6,
               'pluginSha256': sha(PLUGIN), 'files': {p.name: sha(p) for p in MODEL.iterdir() if p.is_file()},
               'cargoLockSha256': sha(NATIVE / 'Cargo.lock')}
    (MODEL / 'local-provenance.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print('Staged ARM64 Phonon plugin and cloned voice. Assets remain gitignored.')
    print('Open GoblinSpeakingDemo and press Play. F4 should show Voice: live Phonon / cloned voice.')


if __name__ == '__main__':
    main()
