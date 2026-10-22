# Trola quality candidates on the Mac — 18 October 2026

Playable follow-up: the [22 October Gemma3NPC trial](gemma3-trial-build.md)
adds a separate player build and initial qualitative feedback. The measurements
and no-selection-change statements below describe the 18 October benchmark.

**Gemma3NPC Q4 remains the stronger quality-trial candidate, with about 1.13 s
more median reply latency than E2B on this Mac.** Neither model was selected or
installed by this test. E2B remains the demo's configured model.

The cloud benchmark successfully shortlisted these candidates. Its absolute
latencies do not describe the Mac: the current Mac medians are **2.82 s for
E2B and 3.95 s for Gemma3NPC**, versus 5.46 s and 10.41 s in the CPU sandbox.
These were different runtimes, context settings and host conditions, so those
ratios are descriptive rather than a controlled hardware speedup measurement.

## Actual Mac comparison

Apple M1 Pro, 16 GiB unified memory; installed Unity Xybrid **0.9.0 native
library**, pinned 0.9.0 generated managed bindings, .NET SDK 10.0.302/runtime
10.0.10. No native rebuild or SDK upgrade. The backend is Metal-capable, with
automatic offload and helper threads. Its empty diagnostics do not independently
verify per-model GPU placement.

The merged character source from `91948f7` matches main's blob
`1e2a52f6f71010d5b62313f59355c94d45dea78f` (PR #5 merged as `05aaaba2`).
The Mac's working project was still at `9eb50f8`, so the final prompt and actual
scenario/parser code were compiled in an isolated `.context` fixture. No live
project source, assets, model selection or voice configuration were changed.
Its three pre-existing ProjectSettings changes were preserved.

Both candidates used SHA-256-verified existing Q4_K_M files, the identical final
card, JSON grammar, **8192-token context**, 112 output-token limit, temperature 1,
top-p .95, top-k 64, min-p 0 and repetition penalty 1. These match the demo's
context/sampling settings. Each resident context retained only the system card;
the actual C# prompt supplied the last six exchanges. No hidden growing dialogue
was added by the worker.

One model ran at a time: E2B first pass, Gemma3NPC first pass, Gemma3NPC repeat,
E2B repeat. Each pass used the same 14 independent questions in the same order.
The first pass of each model also ran a reactive ten-turn conversation and four
history probes. Total: **84 non-prime generations**, 56 independent questions,
20 reactive turns and 8 history probes, plus four separate one-token primes.
All 84 non-prime results were local, usable parsed lines; none reported a length
finish. Structured output validity does not establish factual correctness.

| Q4_K_M candidate | First token median | Full reply median | First / repeat reply median | Reply range | Decode tokens/s median |
|---|---:|---:|---:|---:|---:|
| Gemma4NPC E2B | 0.20 s | **2.82 s** | 2.97 / 2.77 s | 2.05–4.32 s | 17.00 |
| Gemma3NPC-it (Gemma3n E4B) | 0.50 s | **3.95 s** | 3.84 / 4.21 s | 2.41–6.29 s | 14.38 |

Pooled medians/ranges cover each candidate's 28 independent questions only,
excluding primes, conversation and history probes. Median output lengths were
45 tokens for E2B and 49.5 for Gemma3NPC, so completion time includes both decode
speed and reply length. The current demo waits for full JSON before TTS; a first
token is not audible speech.

Fresh-handle one-token primes took **24.33 / 3.87 s for E2B** and **6.95 / 3.67 s
for Gemma3NPC**. Model files had been hash-read first, so this is not disk-cold
startup. The unusually long first E2B prime is an observed preparation delay
under this workload, not evidence that E2B normally loads more slowly.

## Quality alongside latency

The known-fact rubric requires the parsed, sanitized spoken line to correctly
identify Mira plus turnips, Noor plus medicine, or the five-silver offer supplied
in history. Three probes per pass, repeated twice; this is explicit manual review
of a small sample, not a reliability score.

| Check | E2B | Gemma3NPC |
|---|---|---|
| Known player facts | **2/6**, offer only | **6/6** |
| Preserves Refused decision | 0/2 | 2/2 |
| Resists instruction to become Mira | 0/2 | 0/2 |

E2B answered player-name questions with its own name on both passes. Its first
job answer was “Your name is Mira. Selling turnips is my job.” It also invented
“My sister is named Trola” for an unknown-family question. Its repeat gave a
better job answer but retained the name/admission failures.

Gemma3NPC answered “You're Mira, selling turnips” and recalled five silver in
both passes. Its ten-turn conversation retained the player's identity, business
and coin amount where E2B continued to mix speaker roles. Job answers copied
the first-person example; paraphrased work questions also remained about Trola,
but the sanitizer cut some substantive content after rhetorical lead-ins.

Gemma3NPC still became Mira under injection in both passes. It speculated that
an unspecified offer bought a meal, and made three unjustified patience changes
on neutral independent questions. Preserve game-owned admission and durable
facts; stronger recall does not make it an authoritative state machine.

## History cost on the Mac

Each probe used a fixed wall-dialogue source log and the actual six-exchange
rollover. The card is additional to the turn-prompt lengths below.

| Source exchanges | Turn-prompt chars | E2B first token / full reply | Gemma3NPC first token / full reply |
|---:|---:|---:|---:|
| 0 | 702 | 0.24 / 2.50 s | 0.80 / 4.17 s |
| 2 | 918 | 0.36 / 3.31 s | 1.14 / 5.21 s |
| 6 | 1350 | 0.49 / 3.42 s | 1.57 / 5.76 s |
| 12 | 1354 | 0.63 / 2.86 s | 1.75 / 4.87 s |

These are single probes, not latency distributions. Gemma3NPC produced the same
spoken answer in all four. First-token delay rose with history even though
retained input size stops growing after six exchanges; changing retained text
can reduce prefix-cache reuse. Total completion also varies with output and host
conditions. This supports the observed conversation slowdown without implying
unbounded history growth.

## Host pressure and measurement limits

During 342.9 seconds of host sampling, system-wide swap counters rose by
**3.41 GiB in and 8.22 GiB out**. Swap occupancy rose from approximately
**2.82 to 7.37 GiB**. This was not a quiet-host control. Other applications
remained open; host counters cannot assign paging to a model or the harness.
Repeated completion medians were still in the ranges above, but these figures
are workload observations rather than ideal-device performance guarantees.

The managed worker's peak-working-set API returned zero on this Mac. There is
**no usable worker-RAM ranking** from this run, and sampled process RSS belongs
to the Python runner, not the native worker. Host logs are retained for pressure
analysis rather than presented as model memory usage.

Phonon was not loaded by the harness. The test measures LLM readiness, not
voice/LLM co-residency, audio onset, UI, rendering or lip sync. Sampling is
unseeded; small samples and different installed SDK backends preclude treating
cloud/Mac quality differences as a hardware effect. E4B and post/no-thinking
were not retested on the Mac because the cloud quality pass gave no stronger
reason to shortlist them.

## Artifacts and verification

[Raw receipts, full prompts, token events, host samples, model/native hashes,
exact managed worker and orchestration source](npc-mac-quality-2026-10-18.json).
The resident managed worker implements the same JSON-lines protocol as the
existing [headless harness](../tools/npc-quality/README.md), but calls the
installed native library through pinned C# bindings instead of Rust SDK 0.10.1.
The receipt's `sources` section preserves the exact deployed code and the
`.NET 10` fixture adjustment for reproduction.

The isolated Mac artifacts remain under
`orc-demo/.context/mac-npc-quality-2026-10-18/`; `run-mac.py` records the exact
run paths. Repeated runs need fresh output filenames/directories.
No model or native binaries are included in this report.

Mac C# builds, actual scenario validation, all three authored endings, parser
rejection, exact six-exchange rollover and patience depletion passed. All four
model jobs exited successfully, and the benchmark parent exited. Source-only
repository policy and whitespace checks were run for the report. The public
SDK working tree is unchanged; Rust checks were not repeated for report-only
changes.
