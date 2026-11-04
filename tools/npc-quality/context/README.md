# Gemma context experiment

Compares three context layouts for the optional planned Gemma writer
(`-xybridWriter gemma`). Tooling only: the playable writer and the
authored/d1 default are unchanged. Results: [docs/gemma-context-experiment-2026-11-03.md](../../../docs/gemma-context-experiment-2026-11-03.md).

| Variant | System turn | Native history | Final user turn |
|---|---|---|---|
| A: Measured writer | pre-promotion `WriterCard`, pinned | none | pre-promotion `WriterPrompt`, pinned (runtime `TurnContext`, flattened transcript) |
| B: Roles | A's card | last six exchanges as user/assistant turns | A's prompt minus transcript lines; header notes the earlier turns |
| C: Roles + grounding | runtime `WriterCard` (grounded card with voice examples) | runtime `WriterTurns` | runtime `WriterPrompt` (game note: identity split, explicit admission, trusted offer or unknown) + plan |

C won and is now the runtime writer, read by reflection from `GatekeeperNpcModel`.
A's card and prompt are pinned verbatim from `main` 92f9abe in `ContextExperiment.cs`;
hash checks prove A and C match what was measured. B's final turn is A's prompt
built from the same snapshot without transcript lines. Without history, A and B
are byte-identical, so their differences on those fixtures measure sampling noise.

History mapping: `Player` → user, `Troll`/`Trola` → assistant, spoken text
preserved, adjacent same-role entries joined with a newline, the same
six-player-turn window as `TurnContext`. Unknown speakers are rejected.
The current message appears exactly once, at the end of the final user turn.

## Build

Requires .NET 8 or 10 and the installed Xybrid **0.16.0** package bindings.
Nothing in the SDK or native library is rebuilt.

```sh
tools/npc-quality/context/build_context.sh \
  Library/PackageCache/ai.xybrid.sdk@<hash>/Runtime/Bolt /opt/homebrew/bin/dotnet net10.0
```

Output goes to ignored `tools/npc-quality/bin|obj` and `.context/gemma-context/build`.

## Run

```sh
python3 tools/npc-quality/context/run_context.py \
  --model Assets/StreamingAssets/XybridModels/gemma4npc-e2b-q4 \
  --native Assets/Xybrid/Plugins/macOS/libxybrid_bolt.dylib \
  --bindings Library/PackageCache/ai.xybrid.sdk@<hash>/Runtime/Bolt \
  --exporter tools/npc-quality/bin/Debug/net10.0/ScenarioChecks.dll \
  --worker-dll .context/gemma-context/build/worker/bin/Release/net10.0/NativeContextWorker.dll \
  --dotnet /opt/homebrew/bin/dotnet --output .context/gemma-context/run-1
```

Before inference the runner verifies the GGUF, metadata and native library
against `local-assets.json`, records bindings/source/build hashes, runs the C#
construction checks (role order, preserved speech, six-exchange rollover,
single current message, B = A minus transcript, C facts, parser rejection) and
the native context checks (round trips, isolation, clear, cancellation token).
It then checks isolation through the resident worker, runs the runtime's
discarded one-token `Greetings` prime and one cancelled transport diagnostic
(neither counted).

Generations: 18 fixtures × 3 variants × 3 repetitions with variant order
ABC/BCA/CAB per fixture (162), one ten-turn conversation per variant (30) and
0/2/6/12-exchange history probes per variant (12): **204**. Each request
rebuilds a fresh native context (`SetSystem`, `Push`), verifies it holds exactly
the requested turns, streams once with a native cancellation token (90 s
watchdog), and disposes it. Sampling matches the game: 8192-token context,
temperature 1, top-p .95, top-k 64, min-p 0, repetition penalty 1, the writer
grammar, `<turn|>`/`<|turn>` stops, 112 tokens. A UTF-8 byte bound rejects any
request that might not fit instead of clipping it.

Diagnostic questions use the policy's neutral `answer` plan for every variant;
insult and praise use the fixed policy plans. Fixture `expect` blocks never
reach the C# builder or the model. In the conversation, each variant's own
accepted lines become history; a rejected reply records the game's authored
fallback. The game records the five-silver offer after turn 3.

## Score

```sh
python3 tools/npc-quality/context/score_context.py prepare RUN/receipts.jsonl RUN/score --salt SECRET
# Independent blind judges write one JSON list per judge: {item, speaker_identity, player_facts, ...}
python3 tools/npc-quality/context/score_context.py aggregate RUN/score --judgments j1.json j2.json j3.json \
  [--adjudications adjudications.json]
```

`prepare` hides variants behind salted IDs, shuffles items, writes rule-based
cross-check flags and anonymous side-by-side transcripts with a separate key.
`aggregate` takes per-dimension majorities, lists every non-unanimous call for
adjudication and reports by variant. Parser acceptance and timing are reported
separately. Samples are small and unseeded: treat results as exploratory.
