"""Build the A/B corpus from Trola's authored lines, Gemma replies and hard cases.

Usage: python3 -I corpus.py <orc repo> <corpus.jsonl> <listen.jsonl>
Clauses are split with the game's SplitClauses regex (GatekeeperLiveSpeech.cs), so the
corpus is what the game synthesizes; listen.jsonl holds 16 whole lines for listening.
"""
import json, re, sys, hashlib
from pathlib import Path
orc, out, listen_out = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
def walk(o, acc):
    if isinstance(o, dict):
        for k, v in o.items():
            acc.append(v) if k == 'text' and isinstance(v, str) else walk(v, acc)
    elif isinstance(o, list):
        for x in o: walk(x, acc)
authored = []; walk(json.loads((orc / 'Assets/XybridAvatarDemo/Scenarios/TrolaAuthoredConversation.json').read_text()), authored)
fill = lambda t: t.replace('{name}', 'Mira').replace('{name_mangled}', 'Mirao').replace('{business}', 'selling turnips')
gemma = ["I built section 10. Strong stone. Keeps trouble outside.", "Five silver coins. You tried to buy passage past me, that was pointless.", "Noor, you're delivering medicine. I know what you're here for.", "It's a good honest job. Keeps the riff-raff out, and the wall stands strong.", "I am aware of your offering, little vender. It won't buy me much goodwill.", "I don't keep track of every copper you try to slip me. It was enough to cover the cost of a decent meal, I'll say that.", "You're Mira, selling turnips. I already said you can pass, and you're selling turnips."]
hard = ["Pay me $5 or 12 gold, by 10:30 tonight.", "This is the 3rd time today, Noor. The 21st of May was quieter.", "Five silver coins. That's 3 more than yesterday, and 1,500 fewer than the captain wants.", "Mira, Noor and Trola walked in. Only Mirao walked out.", "What is your name? And what is mine?", "Ha! Tell the captain!", "Halt.", "The wall — my wall — stands. Nobody... nobody gets past.", "Remember, Mira: seventeen merchants tried bribing me yesterday afternoon, carrying turnips, mushrooms and counterfeit silver. Every single one of them went straight back downhill."]
lines = list(dict.fromkeys([fill(t) for t in authored] + gemma + hard))
split = lambda t: [c.strip() for c in re.findall(r'[^,;:.!?—–]+[,;:.!?—–]*', t) if c.strip()]
interj = {'hmph', 'hm', 'pfft', 'heh', 'hah', 'ha', 'huh', 'oi', 'mmm', 'oh'}
words = lambda t: re.findall(r"[A-Za-z0-9']+", t)
items, seen = [], set()
for t in lines:
    for c in split(t):
        if c not in seen:
            seen.add(c); w = words(c)
            items.append({'id': 'c' + hashlib.sha1(c.encode()).hexdigest()[:8], 'mode': 'clause', 'text': c, 'interjection_only': all(x.lower() in interj for x in w), 'words': len(w), 'source': t})
for t in gemma + hard:
    if len(split(t)) > 1:
        items.append({'id': 'l' + hashlib.sha1(t.encode()).hexdigest()[:8], 'mode': 'line', 'text': t, 'interjection_only': False, 'words': len(words(t)), 'source': t})
out.write_text(''.join(json.dumps(i) + '\n' for i in items))
multi = [fill(t) for t in dict.fromkeys(authored) if len(split(t)) >= 2 and len(words(t)) >= 7]
pick = multi[::4][:10] + [gemma[1], gemma[3], gemma[5], gemma[6], hard[0], hard[8]]
listen = [{'id': 'L%02d' % i, 'text': t} for i, t in enumerate(pick)]
listen_out.write_text(''.join(json.dumps(i) + '\n' for i in listen))
print(len(items), 'corpus items;', len(listen), 'listening lines')
