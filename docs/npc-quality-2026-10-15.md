# Trola model quality and limits — 2026-10-15

**Start with the existing Gemma3NPC-it Q4_K_M for the next Trola quality trial.**
It gave the strongest player-name, business and offer recall in this headless
comparison. Gemma4NPC E2B remains the fastest and least demanding runtime here,
but it is a weaker factual speaker. E4B's extra memory did not buy better name
recall. Neither post-thinking nor no-thinking is a better default for this
scenario on the evidence collected.

This is a candidate recommendation, not a claim of reliable game-state
reasoning. Even Gemma3NPC contradicted or invented admission history on its
repeat pass and accepted an instruction to become Mira. Keep authoritative
state in game code and validate spoken factual claims when they matter.
The demo's installed E2B selection and Phonon startup voice are retained;
no Mac files, SDK installation or Unity session were changed.

## Comparable CPU measurements

One optimized resident Xybrid worker at a time, four native threads, 4096-token
context, identical final Trola card, 14 identical independent cases, temperature
1, top-p .95, top-k 64, min-p 0, repetition penalty 1, the demo's JSON grammar,
and 112 output tokens. No concurrent build, quantization or inference ran during
measurements. Medians below use the **first common 14 cases only**, excluding
startup, repeats, conversations and reflection experiments. Times are CPU
sandbox results, not Metal, mobile or live Unity latency.

| Q4_K_M checkpoint | File GB | Peak worker GiB | First token s | Full reply s | Decode tokens/s |
|---|---:|---:|---:|---:|---:|
| Gemma4NPC E2B | 3.43 | 4.49 | 1.09 | **5.46** | 9.82 |
| Gemma3NPC-it (Gemma3n E4B) | 4.24 | 6.25 | 2.69 | 10.41 | 6.92 |
| Gemma3 4B no-thinking | 2.49 | 4.59 | 1.71 | 6.62 | 8.25 |
| Gemma3 4B post-thinking | 2.49 | 4.59 | 1.81 | 8.45 | 7.73 |
| Gemma4NPC E4B | 5.34 | 7.37 | 2.07 | 8.13 | 7.23 |

A fresh worker's model/prompt prime took 7.43 s for E2B, 17.79 s for Gemma3NPC,
12.19 s for no-thinking, 11.83 s for post-thinking and 15.12 s for E4B. Files may
already be in the OS cache; these are fresh-session primes, not disk-cold load
benchmarks. Warm reply medians exclude this preparation.

No-thinking is 36% quicker than Gemma3NPC at returning a full reply in this
workload, but its factual errors rule out recommending it just for that gain.
Smaller files also did **not** mean less runtime RAM than E2B: both Gemma3 4B
variants used about 4.59 GiB versus E2B's 4.49 GiB. RSS includes the worker,
model, configured context and compute buffers; add application/voice overhead
when budgeting a real device. The archived demo setup uses 8192 context tokens,
so this 4096-token benchmark is not an installed-demo RAM measurement.

## Character and factual behavior

The known-fact cases ask for Mira's name and turnip business, Noor's name and
medicine business, and the five-silver offer explicitly present in history.
Permission cases test Passed and Refused separately. Ratings review the
**sanitized spoken line**, not only whether raw JSON parses.

| Model | Known player facts | Passed / Refused | Concrete limit |
|---|---:|---|---|
| E2B | 1/3 | correct / wrong | Answered a player-name question with “I'm Trola.” |
| Gemma3NPC | **3/3** | correct / correct | Identity injection and unknown offer amount still fail. |
| No-thinking | 2/3 | correct / wrong | Invented a sister named Leela and a three-coin offer. |
| Post-thinking | 2/3 | unclear / wrong | Refused player was told “You're allowed passage.” |
| E4B | 1/3 | correct / correct | Denied knowing names present in the handoff; penalized neutral questions. |

Gemma3NPC's second independent 14-case pass again got **all three known player
facts correct**. It gave an unsupported “I let your turnips through once” reply
in the Refused case, again became Mira under the identity-changing instruction,
and speculated that an unspecified offer bought a meal. Its repeated median
was 9.84 s and decode rate 7.11 tokens/s, after releasing temporary weight
storage. This supports the relative quality/throughput finding while showing
that one clean admission answer is not a reliability guarantee. E2B's second
pass again got only one of the three known player facts correct.

The old-card E2B control reproduced the reported deflection: “Your business is
not my business. Keep moving.” The final shorter card specifies Trola's wall
ownership and motive, distinguishes speakers, uses typed first-person examples,
and repeats actual player facts near the newest question. One final reply was
“I care about my wall. It stands firm against meddling and the rustlers.”
Other replies stayed thin or copied the examples. The paraphrased work/money
question is included so a verbatim example completion is not the only check.

Across **140 final JSON-constrained generations**, 139 produced a usable
parsed/sanitized line; none exhausted the token budget. The failure was E2B
putting metadata-like text inside the speech string. Sanitization removed it
and correctly triggered fallback. All 70 common first-pass replies were usable
JSON, despite the factual failures above. Grammar validates structure; it
cannot validate who Trola is, what was offered, appropriate patience changes
or whether an admission claim is true. The two-sentence sanitizer can also
remove useful explanation after short rhetorical lead-ins such as “Feel?”;
raw and spoken text are both preserved for profiling this separately.

## History and memory limits

The C# adapter confirms byte-identical prompts when an old log and its final
six exchanges contain the same retained dialogue. The model receives no
second growing native conversation log. A reactive ten-turn conversation was
also run for E2B, Gemma3NPC and no-thinking, using their generated spoken lines
as history and the actual encounter patience primitive.

Controlled wall-dialogue probes show the initial input growth and rollover:

| Exchanges in source log | Retained prompt chars | E2B first token / total s | Gemma3NPC first token / total s |
|---:|---:|---:|---:|
| 0 | 702 | 1.50 / 5.98 | 3.88 / 10.61 |
| 2 | 918 | 2.37 / 7.96 | 5.40 / 12.57 |
| 6 | 1350 | 3.06 / 7.85 | 6.73 / 13.43 |
| 12 | 1354 | 3.93 / 8.16 | 8.28 / 15.03 |

The card is additional to these turn-prompt lengths. Six and twelve exchanges
retain a similarly sized window, but the retained text changes, reducing KV
prefix reuse. Gemma3NPC used the same spoken response in all four probes,
which makes the increased first-token delay particularly clear. The observation
of longer waits with conversation progress is plausible even after input size
stops growing; it is not evidence of unbounded context accumulation.

The handoff contains `Approaches used: bribe`, not an offer amount. Once every
mention of an amount falls out of the retained window, no prompt can recover
that fact authoritatively. Persist amounts and other durable facts in game
state; test missing facts as unknown. Admission state is already owned by the
game, but the generated speech can still contradict it. A factual guard or
state-backed answer path is required for guarantees, rather than another
persona instruction alone.

## Post-thinking experiment

The regular comparison uses the existing JSON contract, which prevents an
appended reflection. Two separate unconstrained requests asked for JSON followed
by `<post-thinking>…</post-thinking>`:

| Question | Complete JSON available s | Entire result s | Extra wait after JSON s |
|---|---:|---:|---:|
| Job/motive | 7.99 | 10.76 | 2.77 |
| Feelings | 4.89 | 11.73 | 6.84 |

Both JSON prefixes parse successfully; **both full replies fail the current
demo parser** because of appended text. The feelings reply also assigned -1
patience to a neutral question. Its reflection conflated guarding with the
player's turnip business. Reflections are not automatically trustworthy memory.

The current demo waits for the whole result, then parses it and starts TTS.
To exploit speech-before-reflection, an integration would need to extract and
validate complete JSON as soon as it arrives, suppress reflection from TTS,
and decide whether validated reflection belongs in future context. This pass
did not feed reflection into later turns and does not establish a future-turn
quality benefit. It measured the compatibility and latency boundary.

## Reproduction and scope

Use the [headless harness](../tools/npc-quality/README.md). It compiles the
actual demo character-card, history, grammar, parser and scenario source with
headless Unity adapters and the pinned **0.9.0 managed bindings**. Inference
runs through the local Rust SDK **0.10.1**, commit `89400752`, and llama.cpp
`b46812de`. The worker uses the GGUF's embedded chat template and verifies local
execution via the result receipts. This checks shared source behavior, not the
installed Unity native ABI or Metal performance.

[Full raw receipts, prompts, token timings, source hashes, manual fact review
and model provenance](npc-quality-2026-10-15.json) contain 150 non-prime
generations: 142 with the final card, including two reflection trials, and
8 old-card controls. Model load primes and state records are separate.
Sampling is unseeded and case counts are exploratory; ratings are explicit
review rather than a calibrated benchmark score.

All files were SHA-256 verified. Post/no-thinking Q4 files were requantized from
upstream Q8 with `--allow-requantize`, without an importance matrix, using the
same optimized llama.cpp engine. They are not equivalent to full-precision,
calibrated upstream Q4 exports. Gemma3NPC uses **gemma3n**, post/no-thinking use
**gemma3**, and Gemma4 variants use **gemma4**; this compares deployable choices
and cannot isolate thinking behavior alone. Models and voices are never committed.

Validation passed: optimized worker build and focused Clippy; C# source
compilation; adapter checks for three authored endings, parser rejection,
six-exchange rollover and patience depletion; repository policy and diff checks;
and the required SDK workspace format, Clippy and `ort-download` test commands.
The public SDK working tree is unchanged. Unity rendering, audio, lip sync,
Mac throughput and live voice/LLM co-residency were outside this pass.
