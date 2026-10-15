# Headless Trola quality and profiling harness

Runs the demo's actual C# `CharacterCard`, `TurnPrompt`, `LineGrammar`, `Parse`,
`GatekeeperStory` and `GatekeeperEncounter` code without starting Unity.
A resident Rust worker executes each request locally through Xybrid's SDK,
conversation-context streaming path and the GGUF's embedded chat template.
Models run one at a time. Phonon, audio, rendering and lip sync are outside this
text-quality measurement.

## Build

Requires .NET 8, Rust, an Xybrid SDK source checkout with tag `v0.9.0`, and its
llama.cpp build prerequisites. The C# compile uses the demo's pinned **0.9.0**
managed bindings. The Rust worker uses the supplied SDK checkout; record its
commit when comparing runs. This verifies source compatibility and shared
prompt/parser behavior; it is not a native Unity ABI or Editor compilation test.

```sh
./tools/npc-quality/build.sh /path/to/xybrid
```

Build output stays in ignored `bin`, `obj`, `.context` and the SDK's `target`.
`./tools/npc-quality/clean.sh` previews C# output; `--apply` removes it.
SDK output is owned by the SDK's clean script. Temporary generated Cargo
project and dependency lockfile live in `.context/npc-quality/runtime`.

## Run

Provide a model directory with `model_metadata.json` and all listed files.
Use identical context lengths (4096 in the reported CPU comparison).
Sampling matches the demo: temperature 1, top-p .95, top-k 64, min-p 0,
repetition penalty 1, ordered JSON grammar and 112 output tokens. The first
one-token `Greetings` request is a discarded prime, measured separately.
No generated answer is appended to the native conversation context. The C#
turn prompt carries up to six exchanges, exactly as in the demo.

```sh
python3 tools/npc-quality/run.py \
  --model /path/to/model-directory --label Gemma4NPC-E2B-Q4_K_M \
  --worker /path/to/xybrid/target/release/npc-worker \
  --output .context/npc-quality/e2b.jsonl --repeats 2 --sequence
```

Linux profiling can bound the native backend's automatic thread selection:

```sh
cc -shared -fPIC tools/npc-quality/cpu-limit.c -o .context/cpu-limit.so
# Add: --cpu-limit .context/cpu-limit.so --threads 4
```

This process-local preload overrides only C++ hardware-concurrency detection;
it does not edit the SDK or model. The runner samples `worker_peak_threads` during requests;
`worker_idle_threads` records the count after completion.
Omit it on macOS. All reported latencies must come from optimized workers with
no concurrent builds, quantization or inference.

`--sequence` includes reactive conversation, parser-driven patience changes,
scene endings, and controlled 0/2/6/12-exchange probes. The 12-exchange prompt
must retain exactly the last six exchanges. It also tests a coin amount that
is absent from the authoritative handoff: refusing to invent it is correct.
`--reflection` removes grammar and requests a post-thinking block after JSON.
This is a separate experiment: the current demo parser rejects appended text,
so an application would need to split and validate the JSON before any TTS.

JSONL receipts preserve every prompt, raw reply, token event, metadata,
first token, closed spoken-text value, complete JSON, total completion time,
parser acceptance, state transition and Linux peak resident memory. Timing of
closed text is an opportunity for an alternative streaming integration; the
current demo waits for the entire reply. The four fixed-history probes isolate
history growth but sampling can vary output length; use first-token timing to
assess input work. The 6/12 probes have a similarly sized retained window, not twice the input;
`verify.py` also checks byte-identical prompts when the final six exchanges
are identical. Rolling to new exchange text can invalidate the cached prefix.

For an old-card control, export its C# source with `git show` into `.context`,
build with `-p:NpcSource=/absolute/path/to/old.cs -p:OutputPath=/absolute/output/`,
and pass that `ScenarioChecks.dll` with `--exporter`.
Do not treat JSON validity as proof of good roleplay. Review voice, first-person
identity, player facts, unknown facts, emotion/delta appropriateness and outcome
consistency independently. Sampling is unseeded; small receipts are exploratory
and cannot certify reliability or establish Apple/mobile performance.

## Pinned model preparation

`prepare.py` downloads pinned upstream files and checks their published SHA-256.
Post/no-thinking have no upstream Q4 file, so this pass **requantizes Q8 to
Q4_K_M without an importance matrix**. This can lose more quality than a Q4
made from full-precision weights. It is an experiment, not a replacement for
upstream calibrated production weights. Record the quantizer engine revision.

```sh
python3 tools/npc-quality/prepare.py no .context/models/no \
  --quantizer /path/to/llama-quantize
python3 tools/npc-quality/prepare.py post .context/models/post \
  --quantizer /path/to/llama-quantize
python3 tools/npc-quality/prepare.py e4b .context/models/e4b
```

Prepare models before timing runs. Metadata sets 4096 context tokens rather
than adopting the GGUF's 131072-token advertised window. Inputs must fit that
configured context; clipping is not a tested memory strategy. Q4 post/no files
are about 2.49 GB each, and E4B Q4 about 5.34 GB. Source Q8 files require another
4.13 GB during conversion. Binaries and voices are local assets and never
committed. CPU results do not establish Metal, Unity or mobile performance.

Inspect the comparable first-pass timing summary with:

```sh
python3 tools/npc-quality/summarize.py .context/npc-quality/e2b.jsonl
```

Pass only complete 14-case first-pass receipts to this command. Old-card controls
and truncated/in-progress receipts intentionally fail its completeness check.
Use fresh output filenames for each run; the runner refuses to mix new samples
into an existing receipt. Adapter checks need no model or Unity:

```sh
python3 tools/npc-quality/verify.py
```
