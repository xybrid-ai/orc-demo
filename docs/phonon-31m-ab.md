# Phonon 31m A/B (2026-10-31)

Two questions, on an M5 Max (macOS 27), CPU only, six threads:

1. **Engine:** is the xybrid zzz port as good as the upstream reference,
   [gradium-ai/xn-ptts](https://github.com/gradium-ai/xn-ptts), on the same model?
2. **Model:** should the game move from the private `7e71a02d@200` drop to the
   public Phonon 31m (`gradium/phonon-31m-en`, `5f0a2aab@200`)?

Phonon 40m was not tested: zzz has no 40m profile, and its CC BY-NC 4.0 gated
licence needs a separate decision.

## Conditions

| Arm | Engine | Model |
| --- | --- | --- |
| `xn-31m` | xn-ptts 0.6.0 (PyPI wheel, `XN_KAI=0`, q8) | 31m `model.q8.gguf` (`a7d9aad2…`) |
| `zzz-31m` | zzz v0.1.0-rc.9 (`5de2a037`) `zzz speak` | the same file, converted |
| `zzz-7e71` | zzz v0.1.0-rc.9 | `7e71a02d@200` q8 (`781f6793…`) |

All arms used the Trola clone (`ba7a5a45@300`, `0c86716d…`), temperature 0.3,
English text normalization, seeds 1–3 (zzz maps seed 0 to 1) and six threads.
The corpus has 174 items: the 159 unique clauses the game synthesizes from
Trola's authored lines, real Gemma replies and hard cases (numbers, currency,
times, names, questions), split with the game's clause regex, plus 15 whole
multi-clause lines. 1,566 clips were transcribed with whisper.cpp
large-v3-turbo (q5_0). Text was normalized with the Whisper English normalizer,
with interjections removed and name spellings aliased.

## Results

| | xn-ptts 0.6.0 · 31m | zzz rc.9 · 31m | zzz rc.9 · 7e71a02d |
| --- | ---: | ---: | ---: |
| Corpus WER | 4.53% | 4.08% | 5.68% |
| Mean CER | 3.78% | 3.36% | 5.08% |
| Last word not heard | 29 / 501 | 28 / 501 | 33 / 501 |
| Hard cut-off endings | 0 | 0 | 0 |
| Mean clip duration | 2.34 s | 2.34 s | 2.28 s |

Paired by item (mean over seeds), with 95% bootstrap confidence intervals over items:

| Difference | CER | WER | Last word missed | Duration ratio |
| --- | --- | --- | --- | ---: |
| zzz − xn-ptts (31m) | −0.43 pp [−1.05, +0.17] | −0.53 pp [−1.56, +0.64] | −0.20 pp [−1.60, +1.20] | 1.000 |
| 31m − 7e71a02d (zzz) | **−1.72 pp [−2.71, −0.78]** | **−2.98 pp [−4.70, −1.34]** | −1.00 pp [−2.40, +0.40] | 1.025 |

Leading and trailing silence matched between engines within 1 ms on average.
The worst items were the same for all arms: the shouted "OUT!" and the
name "Mira" heard as "Mirror". These are ASR artifacts.

## Blind listening

One listener rated 32 randomized A/B pairs of 16 full lines (seed 1) on a −3…+3 scale:

- **Engine:** 12 same, 3 slight preferences for zzz, none for xn-ptts (one pair unrated).
- **Model:** 31m preferred 9 times, 7e71a02d twice, 5 same; mean +0.69 toward 31m.
  7e71a02d was flagged as mispronouncing "Pay me $5 or 12 gold, by 10:30 tonight."
  and "Go on. Before I remember a second toll."
- The all-caps line "OUT! BEFORE I USE YOU TO BAR THE GATE!" was flagged in every
  condition. The text frontend does not change case. A sentence-case rendering
  was transcribed correctly, but it was not rated.

## Findings

- zzz rc.9 is equivalent to the upstream reference on this corpus. No metric
  differs beyond its interval, and durations match.
- 31m is more intelligible than 7e71a02d and was preferred by ear, so it is now
  the startup model on both engines.
- A bare "Hmph." clause reaches the frame cap on 31m with both engines (3.36 s).
  zzz reports `natural_stop=false`. xn-ptts renders the same length. Within the
  cap, xn-ptts falls silent after the interjection, while zzz keeps low-level
  sound to the end.

## Limits

This is a quick profile: one voice, three seeds and a single listener. ASR error
rates include Whisper's own mistakes. Speaker-similarity and MOS predictors,
zzz's own xn-ptts parity gate (`zig build test-phonon-public`) and a full
eight-seed run were not performed. The harness is in
[`tools/phonon-ab`](../tools/phonon-ab/README.md).
