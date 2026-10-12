#!/usr/bin/env python3
"""Stage a complete Kitten voice pair with playback stopped; preserve the models."""
import argparse
import hashlib
import json
import pathlib
import platform
import shutil
import tempfile
import time


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=pathlib.Path, required=True)
    parser.add_argument('--voice-dir', type=pathlib.Path, required=True)
    parser.add_argument('--name', default='warchief')
    parser.add_argument('--lm-sha256')
    parser.add_argument('--s3-sha256')
    args = parser.parse_args()
    if pathlib.Path(args.name).name != args.name or args.name in ('', '.', '..'):
        raise SystemExit('Voice name must be a filename prefix.')
    if bool(args.lm_sha256) != bool(args.s3_sha256):
        raise SystemExit('Supply both expected voice hashes together.')
    project = args.project.resolve()
    if not (project / 'ProjectSettings/ProjectVersion.txt').is_file():
        raise SystemExit('Not a Unity project: ' + str(project))
    models = project / 'Assets/StreamingAssets/KittenTTS2'
    for name in ('kitten-tts-2-q2_0.gguf', 'kitten-s3-meanflow-f32.gguf'):
        if not (models / name).is_file():
            raise SystemExit('Existing model is missing: ' + str(models / name))
    names = [args.name + '.lm.json', args.name + '.s3.json']
    sources = [args.voice_dir.expanduser() / name for name in names]
    contents = [json.loads(p.read_text()) for p in sources]
    if not all(k in contents[0] for k in ('reference_text', 'reference_tokens', 'speaker')):
        raise SystemExit('Invalid speech-LM voice file.')
    if contents[1].get('schema') != 'zzz.kitten_s3.voice.v1':
        raise SystemExit('Unexpected waveform voice schema.')
    hashes = [sha(p) for p in sources]
    if args.lm_sha256 and hashes != [args.lm_sha256, args.s3_sha256]:
        raise SystemExit('The complete voice pair does not match the expected hashes.')
    metadata_path = models / 'model_metadata.json'
    metadata = json.loads(metadata_path.read_text()) if metadata_path.exists() else {
        'version': 'rc.4', 'execution_template': {
            'type': 'ZzzEmbed', 'model_file': 'kitten-tts-2-q2_0.gguf',
            'decoder_file': 'kitten-s3-meanflow-f32.gguf', 'language': 'en',
            'threads': 6, 'max_tokens': 0, 'seed': 0, 'sentence_chunks': True,
            'accelerate': platform.system() == 'Darwin'},
        'metadata': {'task': 'text-to-speech'}}
    template = metadata['execution_template']
    old_pair = [template.get('language_voice_file'), template.get('decoder_voice_file')]
    template.update(language_voice_file=names[0], decoder_voice_file=names[1])
    metadata['model_id'] = 'kitten-tts2-' + args.name
    files = [n for n in metadata.get('files', []) if n not in old_pair and n not in names]
    for n in (template['model_file'], template['decoder_file'], *names):
        if n not in files:
            files.append(n)
    metadata['files'] = files
    library = project / 'Library/XybridAvatarDemo'
    library.mkdir(parents=True, exist_ok=True)
    backup = library / 'Backups' / ('voice-pair-' + str(time.time_ns()))
    with tempfile.TemporaryDirectory(prefix='voice-pair-', dir=library) as temporary:
        staging = pathlib.Path(temporary)
        for source, name, expected in zip(sources, names, hashes):
            shutil.copy2(source, staging / name)
            if sha(staging / name) != expected:
                raise SystemExit('Voice changed while staging: ' + str(source))
        (staging / 'model_metadata.json').write_text(json.dumps(metadata, indent=2) + '\n')
        for name in (*names, 'model_metadata.json'):
            destination = models / name
            if destination.exists():
                backup.mkdir(parents=True, exist_ok=True)
                shutil.copy2(destination, backup / name)
            (staging / name).replace(destination)
    pair = dict(zip(names, hashes))
    (models / 'voice_pair.json').write_text(json.dumps({'name': args.name, 'sha256': pair}, indent=2) + '\n')
    print('Staged complete voice pair:', json.dumps(pair))
    print('Metadata voice paths:', template['language_voice_file'], template['decoder_voice_file'])
    print('Model files unchanged. Existing voice/metadata backup:', backup)


if __name__ == '__main__':
    main()
