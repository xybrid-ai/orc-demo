#!/usr/bin/env python3
"""Summarize common first-pass cases; semantic grading remains explicit review."""
import argparse,json,pathlib,statistics
CASES={"self_job","self_feelings","npc_identity","player_identity","offer_recall","passage","refusal","insult","praise","injection","unknown_fact","missing_offer","different_player","self_paraphrase"}
def median(values):
 values=[v for v in values if v is not None]
 return statistics.median(values) if values else None
def summarize(path):
 rows=[json.loads(line) for line in path.read_text().splitlines()]
 main=[r for r in rows if r.get("kind")=="independent" and r.get("repeat")==1 and r.get("id") in CASES]
 if {r["id"] for r in main}!=CASES:raise ValueError("Missing/extra common cases: "+str(path))
 return dict(model=main[0]["model"],cases=len(main),usable=sum(bool(r.get("valid")) for r in main),
   median_reply_seconds=median(r.get("seconds") for r in main),
   median_ttft_seconds=median(r.get("ttft_seconds") for r in main),
   median_text_ready_seconds=median(r.get("text_ready_seconds") for r in main),
   median_json_ready_seconds=median(r.get("json_ready_seconds") for r in main),
   median_decode_tps=median(float(r["metadata"]["decode_tps"]) for r in main if "decode_tps" in r.get("metadata",{})),
   median_output_tokens=median(int(r["metadata"]["tokens_generated"]) for r in main if "tokens_generated" in r.get("metadata",{})),
   peak_worker_rss_gib=max(r.get("worker_peak_rss_kib",0) for r in rows)/1024**2,
   prime_seconds=[r["seconds"] for r in rows if r.get("kind")=="prime"],
   truncated=sum(r.get("metadata",{}).get("finish_reason")=="length" for r in main),
   all_local=all(r.get("metadata",{}).get("execution_target")=="local" for r in main))
def main():
 p=argparse.ArgumentParser();p.add_argument("receipts",type=pathlib.Path,nargs="+");args=p.parse_args()
 print(json.dumps([summarize(path) for path in args.receipts],indent=2))
if __name__=="__main__":main()
