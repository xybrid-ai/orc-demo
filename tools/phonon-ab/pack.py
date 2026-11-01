"""Build a blind A/B listening page from listen/<arm>/trola_clone/s1/<id>.wav.

Usage: python3 -I pack.py <AB dir>  ->  <AB>/listening/index.html (open in a browser).
Clips are copied under anonymous names; sides are randomized and the key is only
decoded by the page's Reveal button.
"""
import base64, html, json, random, secrets, shutil, sys
from pathlib import Path

AB = Path(sys.argv[1])
OUT = AB / 'listening'
SECTIONS = [
    ('engine', 'Engine: upstream xn-ptts 0.6.0 vs xybrid zzz rc.9 — same Phonon 31m model, same Trola voice',
     ('xn-31m', 'xn-ptts 0.6.0'), ('zzz-31m', 'zzz rc.9')),
    ('model', 'Model: current game model (7e71a02d) vs Phonon 31m — both on zzz rc.9, same Trola voice',
     ('zzz-7e71', 'current 7e71a02d'), ('zzz-31m', 'Phonon 31m')),
]
lines = [json.loads(l) for l in (AB / 'listen.jsonl').open()]
if OUT.exists():
    shutil.rmtree(OUT)
(OUT / 'audio').mkdir(parents=True)
rng = random.Random(secrets.randbits(64))
key, trials = {}, []
for sid, title, x, y in SECTIONS:
    order = list(lines)
    rng.shuffle(order)
    for n, line in enumerate(order, 1):
        clips = {arm: AB / 'listen' / arm / 'trola_clone' / 's1' / (line['id'] + '.wav') for arm, _ in (x, y)}
        if not all(p.exists() for p in clips.values()):
            continue
        tid = f'{sid}{n:02d}'
        a, b = (x, y) if rng.random() < 0.5 else (y, x)
        for side, (arm, _) in (('a', a), ('b', b)):
            shutil.copyfile(clips[arm], OUT / 'audio' / f'{tid}{side}.wav')
        key[tid] = {'a': a[1], 'b': b[1], 'line': line['id']}
        trials.append({'id': tid, 'section': sid, 'text': line['text']})
(OUT / 'key.json').write_text(json.dumps(key, indent=1))
blob = base64.b64encode(json.dumps(key).encode()).decode()
conditions = {sid: [x[1], y[1]] for sid, _, x, y in SECTIONS}

parts = ['''<!doctype html><meta charset="utf-8"><title>Phonon blind A/B — Trola</title>
<style>body{font:15px -apple-system,system-ui,sans-serif;max-width:860px;margin:2em auto;padding:0 1em;color:#222}
h2{margin-top:2em;border-bottom:1px solid #ddd}.t{border:1px solid #e3e3e3;border-radius:8px;padding:.7em 1em;margin:.8em 0}
.q{color:#555;font-style:italic;margin:.2em 0 .5em}.row{display:flex;gap:1.2em;align-items:center;flex-wrap:wrap}
audio{height:32px}.scale label{margin-right:.5em;white-space:nowrap}.flags label{margin-right:.8em;font-size:13px;color:#555}
.key{display:none;color:#06c;font-weight:600}.revealed .key{display:inline}button{font-size:15px;padding:.4em 1em;margin-right:.6em}
#tally{white-space:pre;font-family:ui-monospace,monospace;background:#f6f6f6;padding:1em;display:none}</style>
<h1>Phonon blind A/B — Trola's voice</h1>
<p>Each trial plays the same line twice, A and B, in random order. Use headphones. Pick which sounds better
(clarity, natural rhythm, final words, sounds like Trola). When finished, press <b>Reveal</b> to see which was which
and a tally, then <b>Download results</b>.</p>''']
for sid, title, _, _ in SECTIONS:
    parts.append(f'<h2>{html.escape(title)}</h2>')
    for t in (t for t in trials if t['section'] == sid):
        i = t['id']
        scale = ''.join(f'<label><input type="radio" name="{i}" value="{v}">{lab}</label>'
                        for v, lab in [(-3, 'A ≫'), (-2, 'A >'), (-1, 'A ≥'), (0, 'same'), (1, 'B ≥'), (2, 'B >'), (3, 'B ≫')])
        flags = ''.join(f'<label><input type="checkbox" data-t="{i}" value="{f}">{f}</label>'
                        for f in ['A cut off', 'B cut off', 'A mispronounced', 'B mispronounced', 'A artifact', 'B artifact'])
        parts.append(f'''<div class="t" id="t_{i}"><div class="q">“{html.escape(t["text"])}”</div>
<div class="row"><span>A <span class="key" id="ka_{i}"></span></span><audio controls preload="none" src="audio/{i}a.wav"></audio>
<span>B <span class="key" id="kb_{i}"></span></span><audio controls preload="none" src="audio/{i}b.wav"></audio></div>
<div class="scale">{scale}</div><div class="flags">{flags}</div></div>''')
parts.append(f'''<p><button onclick="reveal()">Reveal</button><button onclick="save()">Download results</button></p>
<div id="tally"></div>
<script>
const KEY = JSON.parse(atob("{blob}")), COND = {json.dumps(conditions)};
function answers() {{
  const out = {{}};
  for (const id in KEY) {{
    const r = document.querySelector(`input[name="${{id}}"]:checked`);
    const flags = [...document.querySelectorAll(`input[data-t="${{id}}"]:checked`)].map(e => e.value);
    out[id] = {{score: r ? +r.value : null, flags}};
  }}
  return out;
}}
function reveal() {{
  document.body.classList.add('revealed');
  const ans = answers(), tally = {{}};
  for (const id in KEY) {{
    document.getElementById('ka_' + id).textContent = '= ' + KEY[id].a;
    document.getElementById('kb_' + id).textContent = '= ' + KEY[id].b;
    const s = id.replace(/\\d+$/, ''), [first, second] = COND[s];
    tally[s] = tally[s] || {{n: 0, sum: 0, wins: {{[first]: 0, [second]: 0, same: 0}}}};
    const sc = ans[id].score; if (sc === null) continue;
    // Positive = second condition preferred.
    const signed = KEY[id].b === second ? sc : -sc;
    tally[s].n++; tally[s].sum += signed;
    tally[s].wins[signed > 0 ? second : signed < 0 ? first : 'same']++;
  }}
  let txt = '';
  for (const s in tally) {{
    const [first, second] = COND[s], t = tally[s];
    txt += `${{first}} vs ${{second}}: ${{t.n}} rated; preferred ${{first}} ${{t.wins[first]}}, ${{second}} ${{t.wins[second]}}, same ${{t.wins.same}}; ` +
           `mean score ${{t.n ? (t.sum / t.n).toFixed(2) : '–'}} (−3 = strongly ${{first}}, +3 = strongly ${{second}})\\n`;
  }}
  const el = document.getElementById('tally'); el.textContent = txt; el.style.display = 'block';
}}
function save() {{
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([JSON.stringify({{answers: answers(), key: KEY}}, null, 1)], {{type: 'application/json'}}));
  a.download = 'phonon-listening-results.json'; a.click();
}}
</script>''')
(OUT / 'index.html').write_text('\n'.join(parts))
print(f'{len(trials)} trials -> {OUT / "index.html"}')
