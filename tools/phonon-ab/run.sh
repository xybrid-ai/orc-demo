#!/bin/bash
# Quick Phonon A/B: convert, render, transcribe, score and build the listening page.
# See README.md. Outputs stay under the ignored .context/phonon-ab.
set -u
HERE=$(cd "$(dirname "$0")" && pwd); ORC=$(cd "$HERE/../.." && pwd)
AB=${AB:-$ORC/.context/phonon-ab}
ZZZ_ENGINE=${ZZZ_ENGINE:?set ZZZ_ENGINE to a zzz v0.1.0-rc.9 checkout built with zig build engine-build}
P31=${P31:?set P31 to the phonon-31m-en folder (commit 52433c46)}
P7E=${P7E:-}
PV=${PV:?set PV to a gradium/phonon-voices folder}
CLONE=${CLONE:?set CLONE to the Trola clone .safetensors}
WHISPER_MODEL=${WHISPER_MODEL:?set WHISPER_MODEL to a whisper.cpp ggml model}
SEEDS=${SEEDS:-1 2 3}
ZZZ=$ZZZ_ENGINE/zig-out/bin/zzz
PY="uv run -q --no-project --python 3.12"
log(){ echo "$(date +%H:%M:%S) $*"; }
mkdir -p "$AB/bundles" "$AB/asr"; cd "$AB"

log convert
mkdir -p stage31/voices && cp "$P31"/{config.json,tokenizer.json,model.q8.gguf} stage31/ && cp "$CLONE" stage31/voices/clone.ba7a5a45@300.safetensors
$PY --with numpy python -I "$HERE/convert_ab.py" "$ZZZ_ENGINE" public stage31 bundles/31m-ab.gguf "$PV"
ARMS="31m"
if [ -n "$P7E" ]; then
  mkdir -p stage7e/voices && cp "$P7E"/{config.json,tokenizer.json,model.q8.gguf} stage7e/ && cp "$P7E/voices/Freya.safetensors" stage7e/voices/ && cp "$CLONE" stage7e/voices/clone.ba7a5a45@300.safetensors
  $PY --with numpy python -I "$HERE/convert_ab.py" "$ZZZ_ENGINE" latents stage7e bundles/7e71-ab.gguf && ARMS="31m 7e71"
fi

log corpus
python3 -I "$HERE/corpus.py" "$ORC" corpus.jsonl listen.jsonl
for f in corpus listen; do python3 -I -c 'import json,sys; [print(d["id"]+"\t"+d["text"]) for d in map(json.loads, open(sys.argv[1]))]' $f.jsonl > $f.tsv; done

zrender(){ # bundle label tsv outroot seeds
  for s in $5; do o=$4/zzz-$2/trola_clone/s$s; mkdir -p "$o"
    while IFS=$'\t' read -r id text; do [ -f "$o/$id.wav" ] || "$ZZZ" speak bundles/$1-ab.gguf --voice trola_clone --language en --temperature 0.3 --threads 6 --seed $s --text "$text" --output "$o/$id.wav" 2> "$o/$id.log" < /dev/null; done < $3
  done; }
for a in $ARMS; do log "zzz $a"; zrender $a $a corpus.tsv renders "$SEEDS"; zrender $a $a listen.tsv listen 1; done
log "xn 31m"
XN_KAI=0 $PY --with ptts==0.6.0 --with numpy python -I "$HERE/xn_render.py" xn-31m "$P31" corpus.jsonl renders trola_clone="$CLONE" "$(echo $SEEDS | tr ' ' ,)"
XN_KAI=0 $PY --with ptts==0.6.0 --with numpy python -I "$HERE/xn_render.py" xn-31m "$P31" listen.jsonl listen trola_clone="$CLONE" 1

log asr
find renders -name '*.wav' ! -name '*.16k.wav' | while read -r f; do g="${f%.wav}.16k.wav"; [ -f "$g" ] || ffmpeg -nostdin -loglevel error -y -i "$f" -af 'adelay=delays=500:all=1,apad=pad_dur=0.5' -ar 16000 -ac 1 -c:a pcm_s16le "$g"; done
rm -f asr/batch.*; find renders -name '*.16k.wav' | sort | split -l 100 - asr/batch.
for b in asr/batch.*; do args=(); while read -r f; do [ -f "$f.json" ] || args+=(-f "$f"); done < "$b"; [ ${#args[@]} -gt 0 ] && whisper-cli -m "$WHISPER_MODEL" -l en -t 8 -bs 5 -bo 5 -nf -oj -np "${args[@]}" > /dev/null 2>> asr/whisper.err; done
log "asr $(find renders -name '*.16k.wav.json' | wc -l | tr -d ' ') transcripts"

log score
$PY --with jiwer==4.0.0 --with whisper-normalizer==0.1.12 --with numpy python -I "$HERE/score.py" . > /dev/null
python3 -I "$HERE/pack.py" .
log "done: $AB/summary.md and $AB/listening/index.html"
