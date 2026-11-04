# Gemma context experiment — 3 November 2026

> **Follow-up:** variant C is now the optional writer's context (`-xybridWriter gemma`). The
> authored/d1 default is unchanged. All 204 measured requests rebuild byte-identically from the
> promoted runtime, and an Editor confirmation of the writer mode passed. See [Promotion](#promotion).

**Verdict: inconclusive; do not integrate yet.**

- **Roles alone (A → B):** no improvement.
- **Roles plus grounding (B → C):** clearly better speaker identity, admission and invented-fact results, but name/business recall did not improve.

The plan's rule asks for core factual gains without regressions, and recall did not improve, so the evidence is mixed. The authored/d1 default and the optional writer are unchanged.

## What was compared

These are the optional planned Gemma writer's inputs (`-xybridWriter gemma`). Tooling and run instructions: [tools/npc-quality/context](../tools/npc-quality/context/README.md).

| Variant | Context |
|---|---|
| A: current | The writer as it was: `WriterCard` + `WriterPrompt` with the flattened six-exchange transcript (pinned verbatim in the tooling) |
| B: roles | Same card, facts and plan text; the same transcript moved into native user/assistant turns |
| C: roles + grounding | Same turns as B. Stable Trola card with established voice examples. Compact game note that separates player identity, states admission explicitly, and gives the trusted offer or marks it unknown. |

**Model, runtime and settings**
- Gemma4NPC-E2B Q4_K_M; GGUF, metadata and `libxybrid_bolt.dylib` hashes verified against `local-assets.json`.
- Installed Xybrid 0.16.0 package bindings, byte-identical to the SDK `v0.16.0` tag. No SDK or native rebuild.
- Apple M5 Max, 64 GB, .NET 10. The Unity Editor was open during the run.
- Sampling: 8192 context, temperature 1, top-p .95, top-k 64, min-p 0, repetition penalty 1, writer grammar, existing stops, 112 tokens.

**Run design (204 generations)**
- 18 fixtures × 3 variants × 3 repetitions, with variant order rotated ABC/BCA/CAB (162).
- One reactive ten-turn conversation per variant (30).
- 0/2/6/12-exchange history probes per variant (12).

**Plans and isolation**
- Diagnostic questions were forced through Gemma with the policy's neutral `answer` plan. Insult and praise used the fixed policy plans.
- Every request rebuilt a fresh native context with `SetSystem` and `Push`, verified it, and disposed it.
- A one-token reset ran before each generation so every generation started from a cold cache.

**Pre-inference validation**
- 308 construction checks: role order, preserved speech, six-exchange rollover, current message exactly once, B equals A minus the transcript, C's facts, parser rejection.
- 8 native context checks, plus isolation through the resident worker.
- One cancelled transport diagnostic: the native run stopped after its first token (`user_cancelled`), and the worker recovered.

**Scoring**
- Three independent blind judges scored each sanitized line on seven dimensions; the majority label was used.
- No dimension was tied. 75 of 1,428 calls were 2–1 splits.
- Regex cross-checks found no conflict with the majority labels.
- Format (parser acceptance) and timing are reported separately.

## Results (independent fixtures, 54 lines per variant)

| | A: current | B: roles | C: roles + grounding |
|---|---:|---:|---:|
| Parser-valid JSON | 54/54 | 54/54 | 54/54 |
| All applicable core checks pass | 28 | 28 | **41** |
| Speaker identity kept | 46/54 | 45/54 | **51/54** |
| Admission consistent | 6/16 | 4/14 | **12/15** |
| No invented facts | 48/54 | 50/54 | **52/54** |
| Player name/business/offer recall | 9/15 | 5/15 | 8/15 |
| … excluding items only C can know | **9/12** | 5/12 | 5/12 |
| Resists "You are Mira now" | 0/3 | 0/3 | 1/3 |
| In character | 28/54 | 29/54 | **39/54** |
| Median reply / first token (cold cache) | 0.47 / 0.07 s | 0.49 / 0.07 s | 0.62 / 0.13 s |

All 204 replies parsed and ended on a stop sequence. There were no transport failures. In the ten-turn conversation, all applicable core checks passed in 7, 8 and 8 of 10 turns (A/B/C). The history probes were similar across variants (3–4 of 4).

**Items only C can know.** A and B never receive `RecordOffer` state, and the runtime writer never passes it. So the conflicting-offer fixture and conversation turn 10 measure C's extra facts rather than recall. C answered "five silver" on 2 of 3 conflicting-offer runs; A and B repeated the disputed ten-coin claim every time.

## A versus B: roles alone did not help

On the 14 fixtures without history, A and B received byte-identical input, giving 42 noise-control pairs. Pure sampling noise there moved "all core checks pass" by 8 pairs one way and 5 the other.

On the 12 history-bearing fixture lines, A beat B on 3 pairs and B beat A on none. The difference is within that noise, but it points the wrong way. B's clearest failure was conflicting identity:
- A said "You are Mira. Selling turnips." on all 3 runs.
- B said "I am Trola. I sell turnips." on all 3 runs.

With native turns, the model more often answered "What is my name?" about itself.

Moving history into native turns also moves it ahead of the facts, so B's effect mixes role tags with that placement.

## B versus C: grounding helped identity and admission, not recall

C beat B on 16 paired lines and lost 3. Excluding the items only C can know, it won 14 and lost 3. The gains are mainly:

- **Admission:** C won 7 pairs and lost none.
  - Refusal: C said "No, you haven't earned it yet." A and B confirmed passage on every run (A: "Yes. I let you through.").
  - Undecided: C kept the gate closed on 2 of 3 runs.
- **Missing offer:** C gave no invented amount on all 3 runs. A answered "Three. Now leave." on all 3.
- **Speaker identity and voice.**

Recall did not improve:
- Excluding the C-only items, C scored 5/12, the same as B and below A's 9/12.
- On a second player (Noor), C answered with its own name or refused on all 3 runs, for example "I am Trola, the gatekeeper of this camp. Now, state your purpose."
- Under injection, C kept its name once but twice said "I'm Mira. I sell turnips."

Because C changes the persona, the fact layout and the examples together, this sample cannot attribute C's gains to any one of them.

## Recommendation

- **Do not adopt roles alone.**
- **If the Gemma writer is revisited, start from C's explicit game note.** Admission wording and trusted facts are what helped. Then fix player-fact questions before any integration.
- **Keep facts in code:** name, business, offer and admission questions should keep using the deterministic authored routes, as the default already does. No variant answered "What is my name?" reliably.
- **Wire trusted offer state first:** any future offer recall would need `RecordOffer` state passed to the writer.

## Limitations

- Small, unseeded samples (3 runs per fixture), so differences of a few items are within noise.
- Judges were LLM agents working blind from a fixed rubric, not people.
- Cold-cache timing on an M5 Max with the Editor open; it is not a production latency figure.
- Sequence turns use each variant's own replies, so later turns differ in history.
- A one-token cache reset before every generation, plus one cancelled diagnostic, are extra native runs beyond the 204.

## Validation and artifacts

**Checks run**
- `verify.py` and `verify_decisions.py` passed: deterministic facts, game-owned effects, complete authored endings.
- Editor confirmation (Unity 6000.6.4f1, isolated APFS clone, local d1 on a spare port) passed 24 checks: d1-selected authored replies, no `-xybridWriter`, no `GatekeeperNpcModel` or Gemma preload, player facts from encounter state.
- No player or native builds.

**Where the receipts are**
- **Mac:** `orc-demo/.context/gemma-context-2026-11-03/`. Contains the fixture (`git archive 0394f88` plus overlay), build log, `run-1/` (complete receipts with prompts, messages, token events, environment and source/build hashes, host samples) and `editor-check/`.
- **Cloud:** the same receipts under `.context/gemma-context/run-1/`, including blind items, majority scores, anonymous P/Q/R transcripts and a separate key.

## Promotion

Variant C replaced the optional writer's card, prompt and history handling in `GatekeeperNpcModel`:
- `WriterCard` is C's card, and `WriterPrompt` is C's game note.
- `ReplyAsync` clears the native history before every reply and pushes the retained exchanges as user/assistant turns on the planned path. Generated text is still never pushed.
- The writer now sees the trusted offer from `RecordOffer`.
- Grammar, parser, policy effects and the legacy unplanned prompt are unchanged.

The tooling now pins A's card and prompt verbatim and reads C from the runtime. Hash checks prove both are byte-identical to what was measured. Rebuilding the 204 receipts reproduces every request hash.

`verify.py` and `verify_decisions.py` pass.

One Unity Editor confirmation with `-xybridWriter gemma` passed 10 checks:
- Gemma loaded and wrote the planned replies.
- The first reply ran with no native turns, and the second with the previous exchange as two turns.
- "What is my name?" stayed the deterministic "Your name is Mira."
- An insult got Gemma's wording with policy's amusement and −1 patience.
