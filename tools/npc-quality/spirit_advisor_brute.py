#!/usr/bin/env python3
"""Independent brute-force spirit advisor over the story JSON (no C#).

verify_spirit.py compares its table with SpiritAdvisor.Advise. It replays the story rules directly:
repeat_delta at the hub, the 0..5 clamp, the hub -> reckoning hand-off after hub_rounds, and the
patience variant of every node entered. Run alone to print the table:
  python3 tools/npc-quality/spirit_advisor_brute.py [--preference passage|friendship] [--json]
"""
import argparse
import itertools
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
STORY = ROOT / 'Assets/XybridAvatarDemo/Scenarios/TrolaAuthoredConversation.json'
TIE = ['camp', 'flatter', 'bribe', 'passage', 'friendship', 'threaten', 'expelled']
FAIL = ('Refused', 'Playing')


class Brute:
    def __init__(self, doc, preference='passage'):
        self.doc = doc
        self.nodes = {n['id']: n for n in doc['nodes']}
        self.preference = 'friendship' if preference == 'friendship' else 'passage'

    def utility(self, outcome):
        top = 'Passed' if self.preference == 'passage' else 'Liked'
        return 3 if outcome == top else 2 if outcome in ('Passed', 'Liked') else 0

    def variant(self, node, p):
        found = [v for v in node['variants'] if v['min_patience'] <= p <= v['max_patience']]
        return found[0] if found else None

    def enter(self, node_id, p, rounds, used, depth=0):
        """Outcome reached from entering node_id with patience p, playing every later checkpoint optimally."""
        node = self.nodes.get(node_id)
        if node is None or depth > 64:
            return 'Playing'
        v = self.variant(node, p)
        if v is None:
            return 'Playing'
        if v.get('outcome'):
            return v['outcome']
        if node.get('choices'):
            outcomes = [self.choose(node, c, p, rounds, used)[3] for c in node['choices']]
            return max(outcomes, key=self.utility)  # first maximum, like the C# loop
        if node.get('input_slot'):
            return 'Playing'
        nxt = node.get('next') or ''
        if nxt == 'hub' and rounds >= self.doc['hub_rounds']:
            nxt = 'reckoning'
        return self.enter(nxt, p, rounds, used, depth + 1) if nxt else 'Playing'

    def choose(self, node, c, p, rounds, used):
        hub = node['id'] == 'hub'
        repeat = hub and c['target'] in used
        delta = c['repeat_delta'] if repeat else c['patience_delta']
        after = max(0, min(5, p + delta))
        later_used = used | {c['target']} if hub else used
        return delta, after, repeat, self.enter(c['target'], after, rounds + (1 if hub else 0), later_used)

    def advise(self, node_id, p, rounds, used):
        node = self.nodes[node_id]
        options = []
        for c in node['choices']:
            delta, after, repeat, outcome = self.choose(node, c, p, rounds, frozenset(used))
            options.append(dict(label=c['label'], target=c['target'], delta=delta, after=after, outcome=outcome,
                                repeat=repeat, aggressive=delta <= -2))
        hopeless = all(o['outcome'] in FAIL for o in options)
        rank = lambda o: (self.utility(o['outcome']), o['after'], o['delta'], not o['repeat'],
                          -(TIE.index(o['target']) if o['target'] in TIE else len(TIE)))
        best = None if hopeless else max(options, key=rank)
        worst = min(options, key=lambda o: (self.utility(o['outcome']), o['after'], o['delta']))
        for o in options:
            o['verdict'] = ('Hopeless' if hopeless else 'Fatal' if o['outcome'] in FAIL else 'Best' if o is best
                            else 'Costly' if o['delta'] < 0 else 'Good')
        almost = not hopeless and (node_id == 'reckoning' or node_id == 'hub' and rounds >= self.doc['hub_rounds'] - 1 and p >= 3)
        return dict(preference=self.preference, node=node_id, patience=p, rounds=rounds, used=sorted(used),
                    best=best['target'] if best else None, worst=worst['target'], hopeless=hopeless, almost=almost, options=options)

    def states(self):
        targets = list(dict.fromkeys(c['target'] for c in self.nodes['hub']['choices']))
        for rounds in range(self.doc['hub_rounds']):
            sizes = [0] if rounds == 0 else range(1, rounds + 1)
            for size in sizes:
                for used in itertools.combinations(targets, size):
                    for p in range(6):
                        yield 'hub', p, rounds, set(used)
        for p in range(6):
            yield 'reckoning', p, self.doc['hub_rounds'], set()


def table(doc, preferences=('passage', 'friendship')):
    rows = []
    for preference in preferences:
        brute = Brute(doc, preference)
        rows += [brute.advise(*state) for state in brute.states()]
    return rows


def key(row):
    return row['preference'], row['node'], row['patience'], row['rounds'], tuple(sorted(row['used']))


def compare(brute_rows, csharp_rows):
    """Field-by-field differences between two advisor tables; empty means they agree on every state."""
    fields = ('label', 'target', 'delta', 'after', 'outcome', 'verdict', 'aggressive', 'repeat')
    mine = {key(r): r for r in brute_rows}
    theirs = {key(r): r for r in csharp_rows}
    problems = [f'missing in C#: {k}' for k in mine if k not in theirs] + [f'extra in C#: {k}' for k in theirs if k not in mine]
    for k in mine.keys() & theirs.keys():
        a, b = mine[k], theirs[k]
        for f in ('best', 'worst', 'hopeless', 'almost'):
            if a[f] != b[f]:
                problems.append(f'{k}: {f} brute={a[f]} C#={b[f]}')
        if len(a['options']) != len(b['options']):
            problems.append(f'{k}: option count'); continue
        for x, y in zip(a['options'], b['options']):
            for f in fields:
                if x[f] != y[f]:
                    problems.append(f'{k} {x["label"]}: {f} brute={x[f]} C#={y[f]}')
    return problems


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--story', type=pathlib.Path, default=STORY)
    p.add_argument('--preference', choices=('passage', 'friendship'), action='append')
    p.add_argument('--json', action='store_true')
    args = p.parse_args()
    rows = table(json.loads(args.story.read_text()), tuple(args.preference or ('passage', 'friendship')))
    if args.json:
        print(json.dumps(rows, indent=1)); return
    for r in rows:
        opts = '; '.join(f"{o['label']}: d{o['delta']:+d}->p{o['after']} {o['outcome']} {o['verdict']}" for o in r['options'])
        print(f"{r['preference']:10} {r['node']:9} r{r['rounds']} used={','.join(r['used']) or '-':16} p{r['patience']} "
              f"best={r['best']} hopeless={r['hopeless']} almost={r['almost']} :: {opts}")


if __name__ == '__main__':
    main()
