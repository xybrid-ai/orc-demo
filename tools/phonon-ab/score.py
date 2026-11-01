"""Score the Phonon A/B renders: intelligibility, final words, stops, timing.

Usage: uv run --no-project --python 3.12 --with jiwer==4.0.0 \
         --with whisper-normalizer==0.1.12 --with numpy python -I score.py <AB dir>
Reads renders/<arm>/<voice>/s<seed>/<id>.wav (+ .16k.wav.json from whisper-cli,
+ .log with zzz's tts_summary) and corpus.jsonl. Writes metrics.csv, per_item.csv,
worst_cases.csv and summary.md next to them.
"""
import csv, json, re, sys, wave
from pathlib import Path

import jiwer
import numpy as np
from whisper_normalizer.english import EnglishTextNormalizer

AB = Path(sys.argv[1])
REFERENCE = 'xn-31m'
PAIRS = [('zzz-31m', 'xn-31m', 'Engine: zzz rc.9 vs xn-ptts 0.6.0 (both Phonon 31m)'),
         ('zzz-31m', 'zzz-7e71', 'Model: Phonon 31m vs current 7e71a02d (both zzz rc.9)')]
INTERJECTIONS = {'hmph', 'hm', 'hmm', 'pfft', 'heh', 'hah', 'ha', 'huh', 'oi', 'mmm', 'oh'}
ALIASES = {'mirra': 'mira', 'meera': 'mira', 'myra': 'mira', 'mirow': 'mirao', 'mirau': 'mirao',
           'nour': 'noor', 'trolla': 'trola'}
normalizer = EnglishTextNormalizer()


def norm(text):
    words = normalizer(text).split()
    return ' '.join(ALIASES.get(w, w) for w in words if w not in INTERJECTIONS)


def audio_stats(path):
    with wave.open(str(path)) as w:
        rate = w.getframerate()
        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype='<i2').astype(np.float64) / 32768.0
    frame = rate // 100
    n = len(pcm) // frame
    db = 20 * np.log10(np.sqrt((pcm[:n * frame].reshape(n, frame) ** 2).mean(axis=1)) + 1e-9) if n else np.array([])
    voiced = np.nonzero(db > -50)[0]
    lead = voiced[0] * 10 if len(voiced) else len(pcm) / rate * 1000
    trail = (n - 1 - voiced[-1]) * 10 if len(voiced) else len(pcm) / rate * 1000
    tail = pcm[-rate // 50:] if len(pcm) >= rate // 50 else pcm
    tail_db = 20 * np.log10(np.sqrt((tail ** 2).mean()) + 1e-9) if len(tail) else -120
    return {'duration_s': len(pcm) / rate, 'lead_ms': float(lead), 'trail_ms': float(trail),
            'peak': float(np.abs(pcm).max()) if len(pcm) else 0.0,
            'clipped': float((np.abs(pcm) >= 32767 / 32768).mean()) if len(pcm) else 0.0,
            'hard_cut': bool(trail < 50 and tail_db > -35), 'silent': bool(len(voiced) == 0)}


def repeats(words):
    for n in range(1, 5):
        for i in range(len(words) - 4 * n + 1):
            if all(words[i + k * n:i + (k + 1) * n] == words[i:i + n] for k in range(1, 4)):
                return True
    return False


def bootstrap(diffs, seed=20261031, b=10000):
    diffs = np.asarray(diffs, dtype=float)
    if len(diffs) == 0:
        return float('nan'), float('nan'), float('nan')
    rng = np.random.default_rng(seed)
    means = diffs[rng.integers(0, len(diffs), (b, len(diffs)))].mean(axis=1)
    return float(diffs.mean()), float(np.percentile(means, 2.5)), float(np.percentile(means, 97.5))


items = {d['id']: d for d in map(json.loads, (AB / 'corpus.jsonl').open())}
rows = []
for wav in sorted((AB / 'renders').glob('*/*/s*/*.wav')):
    if wav.name.endswith('.16k.wav'):
        continue
    arm, voice, seed, iid = wav.parts[-4], wav.parts[-3], int(wav.parts[-2][1:]), wav.stem
    if iid not in items:
        continue
    item = items[iid]
    asr = wav.with_name(iid + '.16k.wav.json')
    hyp_raw = ' '.join(s['text'] for s in json.loads(asr.read_text(errors='replace'))['transcription']).strip() if asr.exists() else None
    row = {'arm': arm, 'voice': voice, 'seed': seed, 'id': iid, 'mode': item['mode'], 'text': item['text'],
           'hyp': hyp_raw, **audio_stats(wav)}
    log = wav.with_suffix('.log')
    if log.exists():
        summary = [json.loads(l) for l in log.read_text().splitlines() if l.startswith('{') and 'tts_summary' in l]
        if summary:
            row['natural_stop'] = summary[-1].get('natural_stop')
            row['limited_chunks'] = summary[-1].get('limited_chunks')
    ref = norm(item['text'])
    if hyp_raw is not None and ref:
        hyp = norm(hyp_raw)
        out = jiwer.process_words(ref, hyp if hyp else '<empty>')
        rw, hw = ref.split(), hyp.split()
        row.update({'ref_words': len(rw), 'word_errors': out.substitutions + out.deletions + out.insertions,
                    'wer': out.wer, 'cer': jiwer.cer(ref, hyp if hyp else '<empty>'),
                    'last_word_miss': bool(rw[-1] not in hw[-2:]),
                    'trailing_insertions': bool(rw[-1] in hw and len(hw) - 1 - max(i for i, w in enumerate(hw) if w == rw[-1]) >= 3),
                    'repeat_loop': repeats(hw)})
    rows.append(row)

medians = {}
for iid in items:
    durs = [r['duration_s'] for r in rows if r['id'] == iid]
    medians[iid] = float(np.median(durs)) if durs else 0.0
for r in rows:
    r['runaway'] = bool(r['duration_s'] > 2.5 * medians[r['id']]) or r.get('natural_stop') is False or bool(r.get('limited_chunks'))

fields = ['arm', 'voice', 'seed', 'id', 'mode', 'text', 'hyp', 'duration_s', 'lead_ms', 'trail_ms', 'peak', 'clipped',
          'hard_cut', 'silent', 'natural_stop', 'limited_chunks', 'ref_words', 'word_errors', 'wer', 'cer',
          'last_word_miss', 'trailing_insertions', 'repeat_loop', 'runaway']
with (AB / 'metrics.csv').open('w', newline='') as f:
    w = csv.DictWriter(f, fields, extrasaction='ignore'); w.writeheader(); w.writerows(rows)

arms = sorted({r['arm'] for r in rows})
METRICS = ['cer', 'wer', 'last_word_miss', 'hard_cut', 'runaway', 'duration_s', 'lead_ms', 'trail_ms']
per_item = {}
for r in rows:
    per_item.setdefault((r['arm'], r['voice'], r['id']), []).append(r)
agg = {}
for key, rs in per_item.items():
    agg[key] = {m: float(np.mean([float(x[m]) for x in rs if m in x and x[m] is not None])) if any(m in x and x[m] is not None for x in rs) else None for m in METRICS}
with (AB / 'per_item.csv').open('w', newline='') as f:
    w = csv.writer(f); w.writerow(['arm', 'voice', 'id'] + METRICS)
    for (a, v, i), m in sorted(agg.items()):
        w.writerow([a, v, i] + [m[k] for k in METRICS])

lines = ['# Phonon A/B summary (quick profile)', '',
         f'Clips scored: {len(rows)}; arms: {", ".join(arms)}; voice: trola_clone; seeds 1-3; 174 corpus items '
         '(game clauses plus multi-clause lines). ASR: whisper.cpp large-v3-turbo; text normalized with the '
         'Whisper English normalizer; interjections dropped.', '',
         '## Per arm (all clips)', '',
         '| arm | clips | corpus WER | mean CER | final word missed | hard cut ending | runaway/limited | mean duration s | mean trailing silence ms |',
         '|---|---:|---:|---:|---:|---:|---:|---:|---:|']
for a in arms:
    rs = [r for r in rows if r['arm'] == a]
    scored = [r for r in rs if 'wer' in r]
    werc = sum(r['word_errors'] for r in scored) / max(1, sum(r['ref_words'] for r in scored))
    lines.append(f"| {a} | {len(rs)} | {werc:.2%} | {np.mean([r['cer'] for r in scored]):.2%} | "
                 f"{sum(r['last_word_miss'] for r in scored)} / {len(scored)} | {sum(r['hard_cut'] for r in rs)} | "
                 f"{sum(r['runaway'] for r in rs)} | {np.mean([r['duration_s'] for r in rs]):.2f} | {np.mean([r['trail_ms'] for r in rs]):.0f} |")
for a, b, title in PAIRS:
    if a not in arms or b not in arms:
        continue
    lines += ['', f'## {title}', '', f'Paired by item (mean over seeds), difference = {a} − {b}, 95% bootstrap CI over items.', '',
              '| metric | ' + a + ' | ' + b + ' | difference [95% CI] |', '|---|---:|---:|---|']
    for m in METRICS:
        pairs = [(agg[(a, v, i)][m], agg[(b, v, i)][m]) for (arm, v, i) in agg if arm == a and (b, v, i) in agg
                 and agg[(a, v, i)][m] is not None and agg[(b, v, i)][m] is not None]
        if not pairs:
            continue
        x, y = np.array(pairs).T
        mean, lo, hi = bootstrap(x - y)
        pct = m in ('cer', 'wer', 'last_word_miss', 'hard_cut', 'runaway')
        f = (lambda v: f'{v:.2%}') if pct else (lambda v: f'{v:.3f}' if m == 'duration_s' else f'{v:.0f}')
        lines.append(f'| {m} | {f(x.mean())} | {f(y.mean())} | {f(mean)} [{f(lo)}, {f(hi)}] |')
    ratio = [agg[(a, v, i)]['duration_s'] / agg[(b, v, i)]['duration_s'] for (arm, v, i) in agg if arm == a and (b, v, i) in agg and agg[(b, v, i)]['duration_s']]
    lines.append(f'| duration ratio (geo mean) | | | {float(np.exp(np.mean(np.log(ratio)))):.3f} |')

worst = sorted([r for r in rows if 'cer' in r], key=lambda r: (-r['cer'], r['arm']))[:40]
with (AB / 'worst_cases.csv').open('w', newline='') as f:
    w = csv.DictWriter(f, fields, extrasaction='ignore'); w.writeheader(); w.writerows(worst)
lines += ['', '## Worst clips by CER', '', '| arm | seed | text | heard |', '|---|---:|---|---|']
for r in worst[:15]:
    lines.append(f"| {r['arm']} | {r['seed']} | {r['text']} | {r['hyp']} |")
(AB / 'summary.md').write_text('\n'.join(lines) + '\n')
print('\n'.join(lines))
