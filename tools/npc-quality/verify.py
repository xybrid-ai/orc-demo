#!/usr/bin/env python3
"""Check headless adapter behavior before trusting any model receipt."""
import argparse,json,pathlib,subprocess
from run import JsonProcess,ROOT,STORY
p=argparse.ArgumentParser();p.add_argument("--dotnet",default="dotnet");args=p.parse_args()
log=ROOT/".context/npc-quality/adapter-check.log";log.parent.mkdir(parents=True,exist_ok=True)
adapter=JsonProcess([args.dotnet,str(ROOT/"tools/npc-quality/bin/Debug/net8.0/ScenarioChecks.dll"),str(STORY)],log)
try:
    story=adapter.ask(dict(command="story_checks"))
    assert [r["outcome"] for r in story]==["Passed","Liked","Refused"],story
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
    trim=adapter.ask(dict(command="spirit",suite="SpeechTrimChecks"))
    assert trim["passed"],trim
    print("PASS: authored endings, parser rejection, six-exchange rollover, patience depletion, speech trim ("+trim["checks"]+")")
finally:adapter.close()
