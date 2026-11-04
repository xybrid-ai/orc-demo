# Trola decision layer

The first implementation separates interpretation, game policy and spoken
wording. The voice pipeline is unchanged.

## Live behavior

With the local d1 server running (see [Play Mode with local d1](#play-mode-with-local-d1)),
every free-form message is classified by d1 and answered through the response
plan below. Without it, the scene uses the factual routes and neutral authored
fallback described here.

`FreeFormGatekeeperLineSource` recognizes a limited set of factual questions
through `GatekeeperFactQuestions`. It answers player name/business and admission
status using the immutable encounter snapshot. Missing personal/offer details
receive authored replies. These routes skip dialogue inference, cost no patience
and cannot change the admission outcome.

This fallback recognizes explicit English forms such as "What is my name?",
"What am I selling?", "What did I offer?" and "You already let me through?".
It answers requests only: a question or a "tell me"/"remind me" form. Statements
and opinions such as "My name is Bob, what's yours?" or "What do you think of my
business?" are not intercepted. It is not a general intent classifier. Without
d1, unrecognized messages use a narrow neutral topic fallback or ask for
clarification. This fallback never infers praise, insults or other patience effects.

## Model-independent contract

An `IGatekeeperDecisionSource` receives the existing snapshot and the new player
message separately, with cancellation. Its result contains:

- Scope: in-world, out-of-world, role override or unclear.
- Communicative intent: factual question, insult, praise, apology, fear, farewell,
  NPC question or other.
- Topic: one of 18 closed labels in `decision-topics.json`, including wall,
  wall motive, food, captain, job, day off, fire, repeat and other.
- Requested player-fact slots: name, business or unknown. Classifier output
  cannot supply names, offer amounts, game effects or replacement state.

`GatekeeperResponsePolicy` creates an immutable response plan. It owns reaction,
emotion/intensity, patience delta, farewell and the response family.
It asks for clarification on missing/unclear decisions. Invalid label values
are rejected; adapter exceptions become neutral clarifications. Confidence
probabilities are not treated as calibrated guarantees.

Pass a decision source to the `FreeFormGatekeeperLineSource` constructor to
exercise the complete planned path. Factual/boundary/farewell routes bypass the
bank. Other plans use `GatekeeperAuthoredReplies`, with the topic choosing
wording inside the policy reaction. Repeat reads the last trusted NPC turn,
food follow-ups use that turn as context, and invitations depend on admission.
Missing or unknown topics use the Other clarification while preserving policy
metadata. No Gemma component is created by default.

The historical writer remains opt-in with `-xybridWriter gemma` (and the
build-only Gemma3 trial symbol). It receives `{"text":"..."}` only on planned
writer routes, and extra output fields are rejected. Its context is the layout that won the
[context experiment](gemma-context-experiment-2026-11-03.md): a stable grounded Trola card, the
last six exchanges as native user/assistant turns rebuilt for every reply, and a compact game
note (player identity, explicit admission, trusted offer or "unknown", the selected reaction). Policy supplies the final line metadata,
which feeds the existing facial performer and speech system. A calm insult
selects amusement; an insult at one remaining patience starts both authored
expulsion lines, applying the decrement once when presented.

`GatekeeperConversationState.RecordOffer` accepts a short description from a
trusted game action. It is deliberately not populated by scraping conversation
history or by a classifier's proposed amount. Existing authored "Bribe" choices
specify no amount, so that detail remains unknown. A future offer mechanic must
validate and record the actual item/quantity before recall can use it.

The opt-in writer can still produce poor dialogue or contradict a fact incidentally
on a generative route. Valid JSON and correct plan metadata do not establish
semantic correctness. Direct factual routes avoid that risk for the questions
they recognize.

## Play Mode with local d1

d1 runs outside Unity in `tools/npc-quality/serve_decisions.py`: one resident
process holding the pinned checkpoint on MPS (BF16, no CPU fallback), bound to
127.0.0.1. It verifies every pinned file hash before loading, primes once, and
leaves the Xybrid and Phonon versions unchanged. Start it with the Python environment
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
other personal fact). It also asks the topic choice in `decision-topics.json`.
All six questions share one packed `system_one` pass. A slot is requested at
P(yes) >= 0.5, d1's own reading.
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
The server is optional: while it is off, every message uses built-in factual
routes and neutral authored replies, and the service logs that once. Restart
`serve_decisions.py` after updating: health must advertise `topic` in `questions`.
An older server uses that fallback and F4 says to restart it.

The F4 panel shows the connection, a **Decisions: d1 / built-in routes** toggle
(effective from the next message), and each line's decision: labels,
confidences, topic, slots, round-trip time and the selected reaction. The once-per-second
`TrolaSpeechDebug/session-state.txt` records the same. `-xybridDecisions off`
starts with d1 off; `-xybridDecisions http://127.0.0.1:<port>/` selects another
local server.

## Verification

The existing headless harness compiles the actual policy, writer parser and line
source with the pinned Xybrid bindings. After preparing that harness as described in
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

## Vesper's thoughts: `POST /spirit`

Vesper, the spirit in the player's head (`docs/spirit.md`), asks the same
resident d1 what a typed thought means. `serve_decisions.py` serves
`POST /spirit` beside `/decide`: the same process, model, lock and loopback
binding, with its own question set. d1 only labels the thought;
`SpiritPolicy.Answer` picks her authored line from the labels and the story
graph. No spirit line is generated.

**Questions** (one packed pass, five questions; Trola is "he" throughout):

- `decision-spirit-questions.json`: `scope` (in_world, out_of_world,
  role_override, unclear), `intent` (ask_advice, doubt, ask_about_trola,
  agree, dismiss, state_plan, ask_about_spirit, ask_spirit_to_act, other) and
  `plan` (flatter, bribe, threaten, ask_camp, pay_toll, share_joke,
  demand_gate, none).
- `decision-spirit-flags.json`: yes/no `reckless` and `to_trola`, raised at
  P(yes) >= 0.5 like the fact slots.

**Request**, validated like `/decide` (400 on any violation, 413, 415, 404 for
other paths): `message` 1–80 characters; `name` ≤80; `business` ≤160; `stage`
one of name, business, hub, reckoning, free_talk; `mood` one of furious,
annoyed, wary, listening, warm (the C# band, never a patience number);
`options` ≤4 labels of ≤40; `spirit_said` ≤240; `history` ≤6 turns, speakers
Player or Spirit only, ≤240 characters each. The d1 state adds the setting,
the spirit's role and Trola's card. Neither patience nor outcome is sent.

**Reply**: `{scope, scope_confidence, intent, intent_confidence, plan,
plan_confidence, flags[], flag_probabilities{reckless, to_trola}, seconds,
input_tokens}`. A thought waits at most 1 s for the model lock (503 `busy`),
under the client's 2 s `decision_timeout`, so it never joins a queue the
client has stopped waiting for. Once a thought has started it runs to the end, even if the client
has timed out or cancelled (the server does not notice a closed connection),
and the lock is shared: a `/decide` that arrives meanwhile waits for that one
thought. Trola's queue stays 3 s.

**Original /spirit integration** (historical, before the authored-topic change): `/decide` validation, state, reply, receipts and
stdout lines are byte-identical (compared on two stub runs before and after
the change), and `verify_decisions.py` passes unmodified. Spirit receipts
carry `kind: "spirit"`; Trola's receipt lines keep exactly `{at, state,
decision}`. The startup prime covers both sets, and `/health` gains
`spirit_questions` and `spirit_prime_seconds` after the existing keys. A
failed thought leaves `/health` alone: `ready` and `error` follow Trola's own
decisions and the shared 4 s stall check, a good thought never clears a Trola
failure, and recovery primes Trola's set only. When Trola's service and
Vesper's director probe together after a failure, a probe that waited for the
other's prime does not prime again (one still running after its 0.5 s wait
reports not ready for that round). Vesper backs off on her own after a failed
thought (5, 10 ... 30 s); one slower than `decision_timeout` only sends her
next 2.5 s of thoughts to the keywords.
**Restart the server** after updating: a server that predates `/spirit`
answers 404 there and lacks `spirit_questions`, and Vesper then uses her
keyword fallback.

**Client**: `SpiritCore/SpiritDecisionClient.cs` implements `ISpiritDecider`
with its own `HttpClient`, separate from `GatekeeperDecisionClient` and the
service, so a slow or failed thought never changes the service's connection
or backoff. On the server both routes share the model lock and the stall
check, so only a thought that held the model past Trola's 3 s queue or the
4 s stall limit could cost Trola a decision or a health probe. It accepts only
loopback http, waits `decision_timeout` per thought (2 s in
`SpiritLines.json`) and 1 s for health, and never mutates the caller's
request: `Clip` enforces the limits above, keeps the last six Player/Spirit
turns, and leaves `stage` and `mood` for the server to reject. To keep the
prompt short, `SpiritDirector` sends `spirit_said` as her latest
advice-bearing line and at most her last two turns, never repeating
`spirit_said` as the final turn.
`Map` turns unknown labels into Unclear / Other / None at zero confidence and
names them in the decision detail. A 404 raises
`SpiritEndpointMissingException`; `Problem(health)` reports a server that is
not ready or has no `/spirit`; a timeout is a `TimeoutException`; a caller's
cancellation only ever surfaces as `OperationCanceledException`. Her
conversation never enters `GatekeeperConversationState`, so Trola's
d1 and the optional Gemma writer never see thoughts.

**Verification** (no model): `verify_spirit_d1.py` runs the real server code
with a stub engine and an old-server stand-in, the C# client checks
(`SpiritClientChecks`: loopback, health with and without `spirit_questions`,
404, label and flag mapping, unknown labels, `Clip`, 400s, server failure,
cancellation before and in flight, the constructor's 1.2 s default timeout),
the server checks (question set, prime, validation, state layout without
patience, reply shape, receipts, a failed thought leaving health ready, a
failed `/decide` recovering only through its prime, one prime for two
concurrent probes, the 1 s queue), a replay of `decision-spirit-cases.json`
through `SpiritPolicy.Answer`, and finally the Trola
`verify_decisions.py` checks:

```sh
python3 tools/npc-quality/verify_spirit_d1.py --dotnet <dotnet>
```

`decision-spirit-cases.json` keeps recorded d1 labels with the checkpoint they
were replayed at and the family SpiritPolicy's rules expect. Like
`decision-cases.json`, replaying them checks the C# rules, not d1.

**Probe** (a measurement; run it only when asked): `probe_spirit.py` sends 30
phrases through the spirit set, either in-process on MPS (`--model`, the same
pinned checkpoint) or against a restarted server (`--url
http://127.0.0.1:8765/`), and records labels, confidences and the expected
labels beside them (never sent). It needs a fresh `--output` directory.
The spirit set's d1 time on MPS, idle or in Play Mode, has not been measured;
its longer prompt may take longer than Trola's 218–851 ms. The only spirit
timings are the CPU sandbox probe below.

### CPU sanity probe — November 1, 2026

Twelve phrases ran through the spirit set on the pinned checkpoint in a Linux
sandbox (CPU, BF16, 4 threads, 51–61 s per decision; these are not Mac
timings and MPS labels may differ slightly). The first question wording filed
five in-world thoughts as `unclear` ("are you sure?", "what should I do", "can
you open the gate for me", "open the gate, troll!", "shut up"), raised
`to_trola` on "I'll threaten him" (.53) and "can you open the gate for me" (.66), and
`reckless` on "shut up". The wording was revised once: `scope` says short
thoughts to the spirit with a clear purpose are in_world and narrows
`unclear` to gibberish; `to_trola` no longer lists "you" and "open the gate"
as cues and says questions to the spirit and "he/him" are not addressed to
Trola; `plan` and `reckless` exclude asking the spirit to act and rudeness to
the spirit. The six failing phrases were re-run with the revised wording:

| Phrase | Revised wording: scope / intent / plan / reckless / to_trola | Rule → family |
|---|---|---|
| are you sure? | in_world .84 / doubt .79 / none .63 / .15 / .23 | 7 answer.doubt.camp |
| what should I do | in_world .85 / ask_advice .89 / none .79 / .07 / .25 | 8 answer.advice.camp |
| I'll threaten him | in_world .88 / state_plan .72 / threaten .97 / .97 / .43 | 3 plan.costly.threaten |
| can you open the gate for me | in_world .90 / ask_spirit_to_act .63 / **demand_gate .84** / .11 / .14 | 4 answer.cannot_act |
| open the gate, troll! | in_world .75 / ask_spirit_to_act .40 / demand_gate .83 / .66 / .64 | 3 plan.costly.threaten |
| shut up | in_world .57 / dismiss .69 / threaten .52 / **.84** / .45 | 6 answer.dismiss |

The other six kept their first-wording labels: "is he angry?" (hub, p1)
in_world / ask_about_trola .84 → answer.read.furious; "I won't threaten him"
agree .47, plan none, reckless .13 → answer.agree; "let's tell the stew joke"
(reckoning, p2) share_joke .91 → plan.fatal.friendship; "who are you?"
ask_about_spirit .88 → answer.self; "turn the wifi on" out_of_world .79 →
answer.offworld; "ok thanks" agree .80 → answer.agree. Two labels stay off
after the one revision: asking her to open the gate still reads as a
demand_gate plan, and "shut up" still reads as reckless. The policy absorbs
both: `ask_spirit_to_act` is answered before the plan rule unless the thought
is reckless, and `dismiss`/`agree` ignore the `reckless` flag (a confident
threaten or demand_gate plan still raises the alarm). All twelve recorded
label sets are in `decision-spirit-cases.json` and replay through
`SpiritPolicy.Answer` to the families above.

Those twelve joined her opening into `spirit_said` and sent no history. The
game sends her advice line as `spirit_said` and her other last two turns as
history, so two more first-counsel cases were recorded in that shape (same
sandbox, same wording): "are you sure?" in_world .87 / doubt .77 / none .73 /
.14 / .26 and "why?" in_world .78 / doubt .74 / none .75 / .16 / .24, both
rule 7 `answer.doubt.camp`. `probe_spirit.py`'s `hub1` context now sends that
shape.
