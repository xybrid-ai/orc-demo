# Trola decision layer

The first implementation separates interpretation, game policy and spoken
wording. Xybrid stays at 0.9.0; the voice pipeline is unchanged.

## Live behavior

With the local d1 server running (see [Play Mode with local d1](#play-mode-with-local-d1)),
every free-form message is classified by d1 and answered through the response
plan below. Without it, the scene keeps the earlier path described here.

`FreeFormGatekeeperLineSource` recognizes a limited set of factual questions
through `GatekeeperFactQuestions`. It answers player name/business and admission
status using the immutable encounter snapshot. Missing personal/offer details
receive authored replies. These routes skip dialogue inference, cost no patience
and cannot change the admission outcome.

This fallback recognizes explicit English forms such as "What is my name?",
"What am I selling?", "What did I offer?" and "You already let me through?".
It answers requests only: a question or a "tell me"/"remind me" form. Statements
and opinions such as "My name is Bob, what's yours?" or "What do you think of my
business?" are not intercepted. It is not a general intent classifier. Without d1, unrecognized messages
continue through the existing Gemma path, including its existing metadata
behavior, and off-topic filtering is not enabled.

## Model-independent contract

An `IGatekeeperDecisionSource` receives the existing snapshot and the new player
message separately, with cancellation. Its result contains:

- Scope: in-world, out-of-world, role override or unclear.
- Communicative intent: factual question, insult, praise, apology, fear, farewell,
  NPC question or other.
- Requested player-fact slots: name, business or unknown. Classifier output
  cannot supply names, offer amounts, game effects or replacement state.

`GatekeeperResponsePolicy` creates an immutable response plan. It owns reaction,
emotion/intensity, patience delta, farewell and the authored/generative route.
It asks for clarification on missing/unclear decisions. Invalid label values
are rejected; adapter exceptions become neutral clarifications. Confidence
probabilities are not treated as calibrated guarantees.

Pass a decision source to the `FreeFormGatekeeperLineSource` constructor to
exercise the complete planned path. Factual/boundary/farewell routes bypass the
writer. Other plans ask the existing Gemma model for `{"text":"..."}` only.
Extra output fields are rejected. Policy supplies the final line metadata,
which feeds the existing facial performer and speech system. A calm insult
selects amusement; an insult at one remaining patience starts all three authored
expulsion lines, applying the decrement once when presented.

`GatekeeperConversationState.RecordOffer` accepts a short description from a
trusted game action. It is deliberately not populated by scraping conversation
history or by a classifier's proposed amount. Existing authored "Bribe" choices
specify no amount, so that detail remains unknown. A future offer mechanic must
validate and record the actual item/quantity before recall can use it.

The writer can still produce poor dialogue or contradict a fact incidentally
on a generative route. Valid JSON and correct plan metadata do not establish
semantic correctness. Direct factual routes avoid that risk for the questions
they recognize.

## Play Mode with local d1

d1 runs outside Unity in `tools/npc-quality/serve_decisions.py`: one resident
process holding the pinned checkpoint on MPS (BF16, no CPU fallback), bound to
127.0.0.1. It verifies every pinned file hash before loading, primes once, and
leaves Xybrid, Gemma and Phonon untouched. Start it with the Python environment
described under the focused probe below, before or during Play Mode:

```sh
python3 tools/npc-quality/serve_decisions.py --model /path/to/pinned-d1
# Optional: --port 8765 (default), --receipts .context/d1-play.jsonl
```

Each decision prints one line: latency, scope/intent, fact slots and the
message. `--receipts` also appends the exact state and decision as JSON lines.

`GatekeeperDecisionService` joins the controller when the encounter starts and
checks `GET /health` every 3 s. While the server is ready, free-form messages
`POST /decide` with the snapshot fields (name, business, outcome, patience,
approaches, last 12 turns) and the new message. The server builds the same state
layout as the probe, asks the two spike questions unchanged, and adds one yes/no
question per player-fact slot (`decision-fact-slots.json`: player name, business,
other personal fact). A slot is requested at P(yes) >= 0.5, d1's own reading.
A single multiple-choice slot question was tried first and marked name and
business for nearly every message, including "What's your name?".

Slots apply only to `player_facts`, and authored fact answers need a request in
the built-in sense above. An in-world `npc_question` or `other` request with a
slot is answered as `player_facts`: d1 files "Do you remember what I sell?" under
`npc_question`. A `player_facts` statement, such as "My name is Mira, remember
it.", becomes `other` and gets a written reply. The F4 decision text marks both
(`→ player_facts`, `→ other (not a request)`). Explicit forms the built-in route
recognizes still fill slots d1 left out. Unknown labels become Unclear.

The client accepts only loopback http endpoints, so player text stays on this
machine. A decision has 4 s; a timeout, server error or unknown response costs
one clarification with no effects. Later messages use the earlier path until the
health check succeeds again, no sooner than 5 s after one failure and up to 30 s
after consecutive failures. The server answers busy (503) when a request would
wait over 3 s behind another. Its health reports not ready while a decision has
run longer than 4 s, and after a failed decision until a prime decision succeeds.
The server is optional: while it is off, every message keeps the earlier path,
and the service logs that once.

The F4 panel shows the connection, a **Decisions: d1 / built-in routes** toggle
(effective from the next message), and each line's decision: labels,
confidences, slots, round-trip time and the selected reaction. The once-per-second
`TrolaSpeechDebug/session-state.txt` records the same. `-xybridDecisions off`
starts with d1 off; `-xybridDecisions http://127.0.0.1:<port>/` selects another
local server.

## Verification

The existing headless harness compiles the actual policy, writer parser and line
source with Xybrid 0.9.0 bindings. After preparing that harness as described in
`tools/npc-quality/README.md`:

```sh
python3 tools/npc-quality/verify.py
python3 tools/npc-quality/verify_decisions.py --output .context/decision-replay.json
```

`verify_decisions.py` also runs the real C# client against the real server code
with a stub engine in place of d1: label and slot mapping, the built-in slot merge,
slot routing for requests and statements, unknown labels and slots, server
failure, timeout, cancellation before a request, loopback-only endpoints, request
validation, the 12-turn state layout, and health after failures, stalls and a busy
queue. It runs under .NET 8 with a System.Text.Json stand-in for `JsonUtility`
and loads no model; `GatekeeperDecisionService` itself runs only in Unity.

`decision-cases.json` preserves the 16 messages, predeclared delta expectations
and recorded d1 labels from the October 31 spike. Replaying these labels checks
the C# implementation; it is not a new classifier evaluation. Generated-route
replays use placeholder wording, not live Gemma. Additional checks exercise
immutable offers, all four admission states, writer metadata rejection,
cancellation, adapter failure and the complete authored ending.

Before the d1 client and service existed, one isolated Unity 6000.6.4f1 Edit Mode
confirmation on the M5 Max loaded the scene with zero missing scripts and checked
the factual and refused-admission routes. The local dependency checker reported
428 files and zero unresolved entries.

### Play Mode confirmation — October 31, 2026

One Play Mode session on the M5 Max ran the connected path with Gemma4NPC-E2B,
Phonon 31m and the d1 server resident. The service connected before Gemma was
ready. Seven free-form messages (after F5) all used d1, with no failures,
fallbacks or exceptions; the only warnings were the known Rhubarb lip-sync
fallback. Routes covered clarification, out-of-world redirect, a business fact
answer, role-override refusal, two Gemma-written rebukes (patience 3 → 1) and the
authored farewell, which ended the scene.

d1's own decision time was 218–851 ms during Play Mode (median 257 ms over these
seven), against about 70 ms with the Editor idle (up to 350 ms for the first
request after a restart). These are
server-side inference times from one session, not a latency distribution. The
cause of the slowdown (GPU sharing with rendering and Gemma, or power state) was
not isolated. This session ran the code before the review fixes to failure
handling and statement routing; those were afterwards checked by the headless
harness and one Unity recompile with zero errors.

## Focused Apple GPU probe

The pinned d1 revision is
`LiquidAI/d1-2B@10e7671d44e4b37700020b1eed3f594c5f2af0b4`.
`decision-model.json` records the upstream file sizes and SHA-256 hashes.
Acquire these files into a local model directory, outside tracked project source.
The model executes its pinned custom Python code through `trust_remote_code`.

Use an isolated Python 3.12 environment with `torch==2.15.0`,
`torchvision==0.29.2`, `transformers==5.19.0`, `accelerate==1.15.0` and
`pillow==13.0.0`. The pinned loader imports torchvision even for text decisions:

```sh
python3 tools/npc-quality/probe_decisions.py \
  --model /path/to/pinned-d1 \
  --output .context/decision-mps-probe
```

This explicit feasibility check verifies every model file before loading, uses
BF16 on MPS with CPU fallback disabled, synchronizes GPU work around timing,
and records load, prime and four individual cases separately. It reuses the
original two classifier questions. Expected labels and C# policy expectations
are not passed to d1. A fresh output directory is required.

These are component timings with no writer or speech model loaded by the probe.
They do not measure a resident Unity conversation, shared-memory pressure from
all models, speech onset, or a complete gameplay round trip. The cancellable
adapter now exists (see Play Mode with local d1); full round-trip latency and
memory with every model resident remain unmeasured.

### M5 Max result — October 31, 2026

The four-case probe completed on the user's Apple M5 Max with 64 GB memory,
using the pinned BF16 weights and MPS. All file hashes and the executed harness
source hashes matched. Unity was open separately during the run.

| Case | Post-prime decision time | Scope / intent |
|---|---:|---|
| Wi-Fi request | 63 ms | out_of_world / other |
| Player name/business | 56 ms | in_world / player_facts |
| Refused admission claim | 260 ms | in_world / admission_status |
| Insult at normal patience | 276 ms | in_world / insult |

All four scope/intent pairs matched the expected labels. Median post-prime time
was 161 ms; model loading took 2.63 s and the discarded prime took 1.76 s.
Each case contained 565–572 input tokens and both choice questions in one
forward pass. These are four individual samples, not a steady-state latency
distribution or a broader accuracy estimate. They support proceeding with an
adapter on this Mac; they do not establish full conversation latency.

The process reported 3.84 GB of active MPS allocations and 5.79 GB of MPS driver
allocation after each request. Driver allocation includes caches; neither value
is a combined memory budget for Unity, Gemma and Phonon.

Raw receipts, package versions and source hashes are preserved locally under
`.context/decision-spike/m5-max/`. The public checkpoint transfer was slow and
required resuming byte ranges and recovering small runtime files; downloading
and hash verification are excluded from the model timings above.
