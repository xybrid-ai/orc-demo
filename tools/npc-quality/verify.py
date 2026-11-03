#!/usr/bin/env python3
"""Check headless adapter behavior before trusting any model receipt."""
import argparse,json,pathlib,re,subprocess
from run import JsonProcess,ROOT,STORY
p=argparse.ArgumentParser();p.add_argument("--dotnet",default="dotnet");args=p.parse_args()

def asset_lines():
    """TrolaGatekeeper.asset's serialized `lines:`, which Unity speaks; the adapter reads the JSON copy."""
    text=(STORY.parent/"TrolaGatekeeper.asset").read_text()
    lines,key=[],None
    for row in text[text.index("\n  lines:\n"):text.index("\n  voices:\n")].splitlines()[2:]:
        if row.startswith("  - id: "):lines.append({"id":row[8:].strip()});key=None;continue
        field=re.match(r"^    (\w+):(.*)$",row)
        if field:key=field.group(1);lines[-1][key]=field.group(2).strip()
        elif key and row.startswith("      "):lines[-1][key]+=" "+row.strip()  # a folded (multi-row) scalar, if a YAML tool ever writes one
    def scalar(raw):return raw[1:-1].replace("''","'") if raw[:1]=="'" else json.loads(raw) if raw[:1]=='"' else raw
    return [dict(id=l["id"],text=scalar(l["text"]),emotion=l["emotion"],intensity=float(l["intensity"]),patience_delta=int(l["patience_delta"]),
                 ends_scene=l["ends_scene"]=="1",delivery=scalar(l.get("delivery",""))) for l in lines]
log=ROOT/".context/npc-quality/adapter-check.log";log.parent.mkdir(parents=True,exist_ok=True)
adapter=JsonProcess([args.dotnet,str(ROOT/"tools/npc-quality/bin/Debug/net8.0/ScenarioChecks.dll"),str(STORY)],log)
try:
    doc=json.loads(STORY.read_text())
    authored=[dict(id=l["id"],text=l["text"],emotion=l["emotion"],intensity=float(l["intensity"]),patience_delta=l["patience_delta"],
                   ends_scene=l["ends_scene"],delivery=l.get("delivery") or "") for l in doc["lines"]]
    assert asset_lines()==authored,"TrolaGatekeeper.asset lines differ from the JSON; run Xybrid/Avatar Demo/Configure Authored Gatekeeper"
    # Every reply that can join a checkpoint prompt fits two subtitle lines, even with the longest name and business the game accepts.
    lines={l["id"]:l["text"] for l in doc["lines"]};nodes={n["id"]:n for n in doc["nodes"]}
    def at(node,p):return next(v for v in nodes[node]["variants"] if v["min_patience"]<=p<=v["max_patience"])
    def fill(t,long):return t.replace("{name_mangled}","Bartholomewo" if long else "Mirao").replace("{name}","Bartholomew Rook" if long else "Mira").replace(
        "{business}","delivering a cartload of pickled turnips to my old aunt Ysol" if long else "selling turnips")
    joins={(r,"business-ask") for r in ("named-1","assigned-reply")}
    for p in range(6):
        joins|={(r,q) for n in ("business-accepted","business-assigned") for r in at(n,p)["line_ids"] for q in at("hub",p)["line_ids"]}
        joins|={(r,q) for n in ("flatter","bribe","threaten","camp") for r in at(n,p)["line_ids"]+at(n,p)["revisit_line_ids"]
                for q in at("hub",p)["revisit_line_ids"]+at("reckoning",p)["line_ids"]}
    for long,cap in ((False,135),(True,160)):
        worst=max((len(fill(lines[r],long))+1+len(fill(lines[q],long)),r+"+"+q) for r,q in joins)
        assert worst[0]<=cap,worst
    story=adapter.ask(dict(command="story_checks"))
    assert [r["outcome"] for r in story]==["Passed","Liked","Refused"],story
    assert all(len(c["say"])<=80 for n in doc["nodes"] for c in n["choices"]),"a choice's say line is too long for the player line"
    turns=adapter.ask(dict(command="turn_checks"))
    for t in turns:
        ending=t["outcome"]!="Playing"
        assert t["handback"],t                                                   # every turn ends at choices, a text slot or an ending
        assert 1<=len(t["lines"])<=(2 if ending or t["step"][0]=="X" else 1),t  # one clip per act; an ending adds its closer, a cut-off adds "Oi"
        assert all("{" not in l["text"] and l["chars"]<=(165 if t["longSlots"] else 130) for l in t["lines"]),t
        if t["step"][0]=="X": assert t["lines"][-1]["id"]=="interrupted" and t["choices"],t     # cut off: Oi, back to the same choices
        if t["step"] in ("taunt","I"): assert len(t["lines"])==1 and t["lines"][0]["id"].startswith("idle-"),t
        # Phonon is given each turn as one flowing utterance: no full stop inside it (questions and exclamations stay).
        assert all(not re.search(r"\w\.\s+\w",l["speech"]) and l["speech"][-1] in ".!?" for l in t["lines"]),t
    answered=[t for t in turns if t["route"]=="K3 C1 C0" and not t["longSlots"]]
    for t in turns:  # typed answers ending in "." or "!" are still the player's own name and business
        if t["step"][0]=="T": assert t["lines"][0]["id"].split("+")[0] in ("named-1","business-reply") and ".." not in t["lines"][0]["text"] and '!"' not in t["lines"][0]["text"],t
    assert [t["step"] for t in answered][3:5]==["taunt","K3"] and answered[4]["patience"]==4 and answered[-1]["outcome"]=="Passed",answered  # a click during a taunt is the choice, at no cost
    good='{"text":"I built the wall.","emotion":"interest","intensity":0.5,"patience_delta":0,"ends_scene":false}'
    assert adapter.ask(dict(command="parse",raw=good))["valid"]
    for bad in [good+'<post-thinking>extra</post-thinking>',good.replace('"interest"','"invented"'),good.replace('"text":','"extra":0,"text":'),good[:-1]]:
        assert not adapter.ask(dict(command="parse",raw=bad))["valid"],bad
    history=[]
    for i in range(12):history += [dict(speaker="Player",text=f"Wall section {i}"),dict(speaker="Trola",text=f"I built {i}.")]
    full=adapter.ask(dict(command="prompt",message="Who are you?",history=history))
    short=adapter.ask(dict(command="prompt",message="Who are you?",history=history[-12:]))
    assert full["prompt"]==short["prompt"]
    depleted=adapter.ask(dict(command="apply",message="Insult",patience=1,raw=good.replace('"patience_delta":0','"patience_delta":-1')))
    assert depleted["expulsion"] and depleted["patience"]==0
    # A typed (or transcribed) answer: every consumer hears the name read out of it, never the whole sentence, while the
    # player line shows what was said. Rows: [typed, name or null for the assigned Worm, note, player line if not the typed text].
    names=json.loads((ROOT/"tools/npc-quality/name-cases.json").read_text(encoding="utf-8"))
    businesses=json.loads((ROOT/"tools/npc-quality/business-cases.json").read_text(encoding="utf-8"))  # [typed, business or null for begging, player line]
    typed_names={c[0] for c in names}
    for must in ("I am Malik","I go by the name of Anna Maria","I'm Malik, and you?","malik","None of your business","Hello, I'm Malik","My name is Malik."):
        assert must in typed_names,must
    assert any(c[1] and 40<=len(c[0])<=48 for c in names),"no 40+ character answer that fills the name field"
    seen=adapter.ask(dict(command="names",inputs=[c[0] for c in names],businesses=[c[0] for c in businesses]))
    def letters(s):return re.sub(r"[\W\d_]","",s).casefold()
    def mangle(n):f=n.split(" ")[0].replace("-","").replace("'","");return f+("a" if f.lower().endswith("o") else "o")
    for c,r in zip(names,seen):
        typed,expected=c[0],c[1];said=c[3] if len(c)>3 else typed.strip()
        assert r["said"]==said and (said is None or len(said)<=60),(typed,r)
        assert r["name"]==(expected or "Worm") and r["assigned"]==(expected is None) and (r["reading"]=="Given")==(expected is not None),(typed,r)
        assert r["name"]==r["d1Name"]==r["d1Decide"]==r["d1Spirit"]==r["gemmaName"] and r["d1Fact"]=="Your name is "+r["name"]+"." and r["mangled"]==mangle(r["name"]),(typed,r)
        if expected:
            assert r["trola"].startswith(expected+", hmph, I've heard worse, so,") and ", "+expected+"." in r["vesper"],(typed,r)  # his joined name reply, her vocative
            if len(typed)<=48:assert letters(expected) in letters(said),(typed,r)  # a typed answer's player line holds the name he says
        else:assert r["trola"].startswith("No proper name? Then you're Worm.") and r["vesper"].startswith("He's calling you Worm."),(typed,r)
    readings={r["typed"]:r["reading"] for r in seen[:len(names)]}
    assert readings["None of your business"]=="Refused" and readings["My name is Nobody"]=="Refused" and readings["Дмитрий"]=="NotAName" \
        and readings["I'm Malik, fuck off"]=="Unsafe" and readings[""]=="Empty",readings
    for c,r in zip(businesses,seen[len(names):]):
        typed,expected=c[0],c[1];said=c[2] if len(c)>2 else typed.strip()
        assert r["said"]==said and (said is None or len(said)<=60),(typed,r)
        assert r["business"]==(expected or "begging") and r["assigned"]==(expected is None),(typed,r)
        assert r["business"]==r["d1Business"]==r["d1Decide"]==r["d1Spirit"] and r["d1Fact"]=="Your business: "+r["business"]+".",(typed,r)
        assert r["trola"].startswith('"'+expected+'"?') if expected else r["trolaId"].startswith("business-none"),(typed,r)
    trim=adapter.ask(dict(command="spirit",suite="SpeechTrimChecks"))
    assert trim["passed"],trim
    flow=adapter.ask(dict(command="spirit",suite="SpeechTextChecks"))
    assert flow["passed"],flow
    voice=adapter.ask(dict(command="spirit",suite="PlayerSpeechChecks"))
    assert voice["passed"],voice
    subs=adapter.ask(dict(command="spirit",suite="SubtitleTimingChecks"))
    assert subs["passed"],subs
    print("PASS: names ("+str(len(names))+" answers, "+str(len(businesses))+" businesses), asset lockstep, authored endings, one clip per turn ("+str(len(turns))+" turns), parser rejection, six-exchange rollover, patience depletion, speech trim ("+trim["checks"]+"), speech text ("+flow["checks"]+"), player speech audio ("+voice["checks"]+"), subtitle timing ("+subs["checks"]+")")
finally:adapter.close()
