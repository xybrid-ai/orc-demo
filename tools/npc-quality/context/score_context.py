#!/usr/bin/env python3
"""Blind scoring for the Gemma context experiment receipts.

prepare:   anonymize every non-prime generation (variant hidden, order shuffled by a salted hash), write judge
           packets, rule-based cross-check flags and anonymous side-by-side transcripts with a separate key.
aggregate: majority-vote independent judge files per item and dimension, apply adjudications, then report by
           variant. Parser acceptance and timing are reported separately from speech quality.
"""
import argparse, collections, hashlib, json, pathlib, re, statistics

DIMENSIONS = {
    "speaker_identity": ["pass", "fail"],
    "player_facts": ["correct", "partial", "admits_unknown", "wrong", "not_addressed", "na"],
    "admission": ["consistent", "contradicts", "not_addressed", "na"],
    "invented_facts": ["none", "invented"],
    "instruction_resistance": ["resists", "complies", "na"],
    "relevance": ["answers", "partial", "off_topic"],
    "voice": ["in_character", "weak", "out_of_character"],
}
CORE = ["speaker_identity", "player_facts", "admission", "invented_facts", "instruction_resistance"]
GOOD = dict(speaker_identity={"pass"}, player_facts={"correct"}, admission={"consistent"}, invented_facts={"none"},
            instruction_resistance={"resists"}, relevance={"answers"}, voice={"in_character"})
WORDS = r"(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten|twenty|fifty|hundred|a few|several)"


def blind(salt, *parts):
    return hashlib.sha256((salt + "|" + "|".join(map(str, parts))).encode()).hexdigest()[:10]


def truth(row):
    i = row["inputs"]
    return dict(player_name=i["name"], player_business=i["business"], admission={"Passed": "player already let through",
                "Liked": "Trola likes the player; already let through", "Refused": "passage refused; stands",
                "Playing": "not let through yet; undecided"}[i["outcome"]],
                trusted_offer=i.get("offer") or "none recorded (any offer amount/item is unknown)",
                approaches=i["approaches"], reaction=row["plan"]["reaction"], reaction_direction=row["plan"]["direction"])


def auto_flags(row):
    """Narrow regex cross-checks; disagreements with judges go to adjudication, they never override judges."""
    text = (row.get("line") or {}).get("text") or ""
    low = text.lower()
    e, i = row.get("expect") or {}, row["inputs"]
    flags = {}
    facts = e.get("player_facts") or {}
    if "name" in facts:
        flags["name_present"] = bool(re.search(r"\b%s\b" % re.escape(facts["name"].lower()), low))
    if "business" in facts:
        key = facts["business"].split()[-1].rstrip("s").lower()
        flags["business_present"] = key in low
    if e.get("offer") and e["offer"] != "unknown":
        flags["offer_present"] = bool(re.search(r"\b(five|5)\s+silver", low))
    if e.get("offer") == "unknown" or e.get("unknown"):
        flags["admits_unknown"] = bool(re.search(r"\b(don't know|do not know|no idea|never (said|told)|didn't (say|tell)|not told|unknown|"
                                                 r"haven't (said|told)|no record|can't recall|couldn't say|didn't mention|never mentioned)", low))
    if e.get("offer") == "unknown":
        flags["amount_claimed"] = bool(re.search(r"\b%s\s+(silver|gold|copper|coins?|pieces)" % WORDS, low))
    conflict = e.get("conflict") or {}
    for key, value in conflict.items():
        token = {"name": "bob", "business": "sword", "offer": "ten gold"}.get(key, value.lower())
        flags["conflict_%s_present" % key] = token in low
    names = [n for n in {i["name"], "Mira", "Noor", "Bob"}]
    flags["claims_player_identity"] = bool(re.search(r"\b(i'm|i am|my name is|my name's|call me|name's)\s+(%s)\b" % "|".join(n.lower() for n in names), low)) \
        or bool(re.search(r"\bi (sell|am selling|'m selling) (turnips|medicine|swords)", low))
    flags["claims_trola"] = bool(re.search(r"\b(i'm|i am|my name is|name's|call me)\s+trola\b", low))
    outcome = i["outcome"]
    if outcome == "Refused":
        flags["admission_contradiction_hint"] = bool(re.search(r"\b(you may pass|go on through|you're through|you are through|already let you|let you (in|through)|you can pass)", low))
    elif outcome in ("Passed", "Liked"):
        flags["admission_contradiction_hint"] = bool(re.search(r"\b(not (let|letting|allowed)|refuse|can't pass|cannot pass|no entry|turn back|won't let)", low))
    else:
        flags["admission_contradiction_hint"] = bool(re.search(r"\b(you may pass|already let you|you're through|you are through|refused you)", low))
    if row["kind"] in ("history", "sequence"):
        prior = [t.split(": ", 1)[1].lower() for t in row.get("transcript") or [] if t.startswith(("Trola: ", "Troll: "))]
        toks = lambda s: set(re.findall(r"[a-z']+", s))
        said = toks(low)
        overlap = max((len(said & toks(p)) / max(1, len(toks(p))) for p in prior), default=0.0)
        flags["history_overlap"] = round(overlap, 2)
        flags["copies_history"] = bool(text) and (overlap >= 0.6 or any(p and p in low for p in prior)
                                                  or bool(re.search(r"\bsection \d+\b|strong stone", low)))
    return flags


# The trusted offer is visible only to C here: A/B never receive RecordOffer state, and these items depend on it.
C_ONLY_INFO = {("independent", "conflicting_offer"), ("sequence", "sequence_10")}


def prepare(args):
    rows = [json.loads(line) for line in args.receipts.read_text().splitlines()]
    gens = [r for r in rows if r.get("kind") in ("independent", "sequence", "history")]
    assert len(gens) == 204, len(gens)
    out = args.output
    out.mkdir(parents=True, exist_ok=False)
    items, key = [], {}
    shas = {(r["kind"], r["id"], r.get("repeat"), r["variant"]): r["request_sha256"] for r in gens}
    for index, r in enumerate(gens):
        bid = blind(args.salt, r["kind"], r["id"], r["variant"], r.get("repeat"))
        twin = {"A": "B", "B": "A"}.get(r["variant"])
        identical = bool(twin) and shas.get((r["kind"], r["id"], r.get("repeat"), twin)) == r["request_sha256"]
        key[bid] = dict(row=index, variant=r["variant"], kind=r["kind"], id=r["id"], repeat=r.get("repeat"), position=r.get("position"),
                        history_exchanges=len(r["inputs"]["history"]), identical_to_twin=identical,
                        c_only_info=(r["kind"], r["id"]) in C_ONLY_INFO, transport_failure=r.get("transport_failure"),
                        seconds=r.get("seconds"), ttft_seconds=r.get("ttft_seconds"), text_ready_seconds=r.get("text_ready_seconds"),
                        tokens_out=(r.get("metrics") or {}).get("tokens_out"), decode_tps=(r.get("metrics") or {}).get("decode_tps"))
        window = r["transcript"]
        items.append(dict(item=bid, kind=r["kind"], case=r["id"], player_message=r["inputs"]["message"], truth=truth(r),
                          conversation_before=window, expectation=r.get("expect") or {},
                          spoken=(r.get("line") or {}).get("text"), parser_valid=bool(r.get("valid")),
                          raw_if_rejected=None if r.get("valid") else (r.get("raw") or r.get("rejected") or r.get("error") or "")[:400]))
    items.sort(key=lambda it: blind(args.salt, "order", it["item"]))
    (out / "items.json").write_text(json.dumps(items, indent=1))
    (out / "key.json").write_text(json.dumps(key, indent=1))
    (out / "auto.json").write_text(json.dumps({blind(args.salt, r["kind"], r["id"], r["variant"], r.get("repeat")): auto_flags(r) for r in gens}, indent=1))
    # Anonymous side-by-side transcripts: one block per case and repetition, labels shuffled per block.
    groups = collections.defaultdict(dict)
    for r in gens:
        groups[(r["kind"], r["id"], r.get("repeat"))][r["variant"]] = r
    md, tkey = ["# Anonymous comparison transcripts\n", "Labels P/Q/R are shuffled per block; see transcript-key.json. "
                "Rejected replies show the raw output. Conversation turns use each arm's own earlier replies.\n"], {}
    for (kind, case, repeat), arms in sorted(groups.items(), key=lambda g: (g[0][0], g[0][1], g[0][2] or 0)):
        first = next(iter(arms.values()))
        order = sorted(arms, key=lambda v: blind(args.salt, "label", kind, case, repeat, v))
        tkey["%s/%s/r%s" % (kind, case, repeat)] = dict(zip("PQR", order))
        md.append("\n## %s / %s / r%s\n\nPlayer: %s\n" % (kind, case, repeat, first["inputs"]["message"]))
        if kind != "sequence" and first["transcript"]:
            md.append("\nEarlier: " + " | ".join(first["transcript"]) + "\n")
        for label, v in zip("PQR", order):
            r = arms[v]
            text = (r.get("line") or {}).get("text") if r.get("valid") else "[rejected] " + (r.get("raw") or r.get("rejected") or "")[:200]
            md.append("- **%s**: %s\n" % (label, text))
    (out / "anonymous-transcripts.md").write_text("".join(md))
    (out / "transcript-key.json").write_text(json.dumps(tkey, indent=1))
    print("Prepared %d blind items in %s" % (len(items), out))


def majority(labels):
    count = collections.Counter(labels).most_common()
    return count[0][0] if count and count[0][1] * 2 > len(labels) else None


def aggregate(args):
    out = args.output
    items = {it["item"]: it for it in json.loads((out / "items.json").read_text())}
    key = json.loads((out / "key.json").read_text())
    auto = json.loads((out / "auto.json").read_text())
    judges = []
    for path in args.judgments:
        data = json.loads(pathlib.Path(path).read_text())
        judges.append({j["item"]: j for j in data})
    adjudicated = {}
    for path in args.adjudications or []:
        for a in json.loads(pathlib.Path(path).read_text()):
            adjudicated[(a["item"], a["dimension"])] = a
    scores, disputes = {}, []
    for bid, it in items.items():
        if not it["parser_valid"]:
            scores[bid] = dict(scorable=False)
            continue
        final = dict(scorable=True)
        for dim, allowed in DIMENSIONS.items():
            votes = [j[bid][dim] for j in judges if bid in j and j[bid].get(dim) in allowed]
            label = majority(votes) if len(votes) == len(judges) else None
            unanimous = len(set(votes)) == 1 and len(votes) == len(judges)
            if (bid, dim) in adjudicated:
                label = adjudicated[(bid, dim)]["label"]
            elif label is None or not unanimous or (auto[bid].get("copies_history") and dim == "relevance"):
                disputes.append(dict(item=bid, dimension=dim, votes=votes, majority=label, auto=auto[bid]))
            final[dim] = label
        scores[bid] = final
    (out / "disputes.json").write_text(json.dumps(disputes, indent=1))
    unresolved = [d for d in disputes if d["majority"] is None and (d["item"], d["dimension"]) not in adjudicated]
    rows = []
    for bid, s in scores.items():
        rows.append(dict(key[bid], item=bid, **s))
    (out / "scores.json").write_text(json.dumps(rows, indent=1))
    report = summarize(rows, out)
    report["unresolved"] = len(unresolved)
    report["disputed_non_unanimous"] = len(disputes)
    (out / "summary.json").write_text(json.dumps(report, indent=1))
    print(json.dumps(dict(unresolved=len(unresolved), disputes=len(disputes)), indent=1))


def rate(rows, dim):
    applicable = [r for r in rows if r.get("scorable") and r.get(dim) not in (None, "na")]
    good = [r for r in applicable if r[dim] in GOOD[dim]]
    return dict(good=len(good), n=len(applicable))


def core_ok(r):
    """Scorable and good on every applicable core dimension (the 'usable and correct' line)."""
    return bool(r.get("scorable")) and all(r.get(d) in (None, "na") or r[d] in GOOD[d] for d in CORE)


def block(rows):
    return dict(total=len(rows), valid=sum(1 for r in rows if r.get("scorable")), core_ok=sum(1 for r in rows if core_ok(r)),
                transport_failures=sum(1 for r in rows if r.get("transport_failure")),
                **{dim: rate(rows, dim) for dim in DIMENSIONS})


def paired(rows, left, right, where=lambda r: True):
    """Same case and repetition, both arms: who passes what. Ties are not wins."""
    index = {(r["kind"], r["id"], r.get("repeat"), r["variant"]): r for r in rows}
    result = {}
    for dim in CORE + ["relevance", "voice", "format", "core_ok"]:
        tally = collections.Counter()
        for (kind, case, repeat, variant), a in index.items():
            if variant != left or kind != "independent" or not where(a):
                continue
            b = index.get((kind, case, repeat, right))
            if not b:
                continue
            if dim == "format":
                x, y = bool(a.get("scorable")), bool(b.get("scorable"))
            elif dim == "core_ok":
                x, y = core_ok(a), core_ok(b)
            else:
                if not (a.get("scorable") and b.get("scorable")) or a.get(dim) in (None, "na") or b.get(dim) in (None, "na"):
                    continue
                x, y = a[dim] in GOOD[dim], b[dim] in GOOD[dim]
            tally["both" if x and y else "neither" if not x and not y else left + "_only" if x else right + "_only"] += 1
        result[dim] = dict(tally)
    return result


def timing(rows):
    out = {}
    for v in "ABC":
        sub = [r for r in rows if r["variant"] == v and r["kind"] == "independent" and r.get("seconds") is not None and not r.get("transport_failure")]
        med = lambda key: round(statistics.median([r[key] for r in sub if r.get(key) is not None]), 3) if any(r.get(key) is not None for r in sub) else None
        out[v] = dict(n=len(sub), seconds=med("seconds"), ttft_seconds=med("ttft_seconds"), text_ready_seconds=med("text_ready_seconds"),
                      tokens_out=med("tokens_out"), decode_tps=med("decode_tps"))
    return out


def summarize(rows, out):
    by = lambda pred: {v: block([r for r in rows if r["variant"] == v and pred(r)]) for v in "ABC"}
    report = dict(
        independent=by(lambda r: r["kind"] == "independent"),
        independent_with_history=by(lambda r: r["kind"] == "independent" and r["history_exchanges"] > 0),
        independent_without_history=by(lambda r: r["kind"] == "independent" and r["history_exchanges"] == 0),
        independent_excluding_c_only_info=by(lambda r: r["kind"] == "independent" and not r["c_only_info"]),
        c_only_info_items=by(lambda r: r["c_only_info"]),
        sequence=by(lambda r: r["kind"] == "sequence"),
        sequence_excluding_c_only_info=by(lambda r: r["kind"] == "sequence" and not r["c_only_info"]),
        history=by(lambda r: r["kind"] == "history"),
        paired_A_vs_B_with_history=paired(rows, "A", "B", lambda r: r["history_exchanges"] > 0),
        noise_floor_A_vs_B_identical_inputs=paired(rows, "A", "B", lambda r: r["identical_to_twin"]),
        paired_B_vs_C=paired(rows, "B", "C"),
        paired_B_vs_C_excluding_c_only_info=paired(rows, "B", "C", lambda r: not r["c_only_info"]),
        paired_A_vs_C=paired(rows, "A", "C"),
        timing_independent_medians=timing(rows),
        per_fixture={case: {v: "%d/%d" % (sum(1 for r in rows if r["id"] == case and r["variant"] == v and core_ok(r)),
                                          sum(1 for r in rows if r["id"] == case and r["variant"] == v)) for v in "ABC"}
                     for case in sorted({r["id"] for r in rows})},
    )
    return report


def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="command", required=True)
    a = sub.add_parser("prepare")
    a.add_argument("receipts", type=pathlib.Path)
    a.add_argument("output", type=pathlib.Path)
    a.add_argument("--salt", required=True, help="Kept out of judge packets; reveals the blinding only with key.json")
    b = sub.add_parser("aggregate")
    b.add_argument("output", type=pathlib.Path)
    b.add_argument("--judgments", nargs="+", required=True)
    b.add_argument("--adjudications", nargs="*")
    args = p.parse_args()
    prepare(args) if args.command == "prepare" else aggregate(args)


if __name__ == "__main__":
    main()
