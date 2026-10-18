# Voice performance observation

Measured once per condition inside Unity 6000.6.4f1 on an Apple M1 Pro, 16 GiB unified memory, 10 CPU cores (8 performance, 2 efficiency). No player builds or repeated observations. Each native session used the paired WarChief conditioning, rc.4, Q2_0 speech LM, F32 decoder, unlimited tokens, seed 0, Accelerate and sentenceChunks=true. Session-open/prefix preparation time is excluded. Rhubarb is excluded from these synthesis-only timings.

RTF = synthesis seconds / generated audio seconds; below 1 is faster than playback.

| Gemma | Voice delivery | Short RTF | Medium RTF | Long RTF | Total synthesis / total audio |
|---|---|---:|---:|---:|---:|
| unloaded | Original, 6 threads | 22.00 | 16.52 | 12.37 | 14.53 |
| unloaded | Clauses, 6 threads | 15.85 | 26.19 | 24.14 | 24.24 |
| unloaded | Clauses, 4 threads | 20.93 | 35.74 | 13.28 | 21.24 |
| loaded | Original, 6 threads | 37.51 | 28.83 | 2.46 | 14.15 |
| loaded | Clauses, 6 threads | 6.39 | 2.80 | 2.63 | 2.95 |
| loaded | Clauses, 4 threads | 7.07 | 3.38 | 2.89 | 3.34 |

The short line is “Give me your name.”; the medium line is “The gate is open, Mira. Keep those turnips away from my boots.”; the long line is “You tried a bribe, and I let you through. Sell your turnips in the camp, then leave me to guard this gate in peace.”

Six threads remains selected. In the Gemma-loaded clause observations, four threads was slower on every line. Clause delivery guarantees that a fully generated waveform finishes before advancing, and starts after the first clause; it can still pause between clauses because synthesis is slower than playback.

These are single observations with substantial changing machine load. The helper import also exposed an existing animation-cache hot-reload error during the loaded measurement window; the scene was reset for the successful gameplay confirmation. That extra logging is another confound, so these numbers are observations, not a controlled contention benchmark. The original loaded long line was far faster than the loaded short and medium lines, and the later clause samples were much faster than the earlier samples. This prevents a causal conclusion about Gemma residency or an isolated clause speedup. All 18 returned Completed. The helper preserved completed rows across its import and skipped them on continuation; no measured condition was repeated.

Kitten runs on CPU with Accelerate and six explicit native threads. Accelerate’s internal worker count is system-managed and not exposed by the rc.4 ABI. Gemma uses the existing Xybrid GGUF path configured for Metal; its CPU helpers retain the automatic thread setting. The installed SDK does not expose their actual active thread count (the host has 10 logical CPUs). The two paths already target different compute devices; the observations do not establish concurrent compute contention.

The installed Xybrid loader probe failed with:

```
invalid model_metadata.json: unknown variant `ZzzEmbed`, expected one of `Onnx`, `SafeTensors`, `CoreMl`, `TfLite`, `LiteRtLm`, `ModelGraph`, `Gguf`, `GgmlWhisper`, `VisionLanguage` at line 4 column 22
```

Consequently, Kitten Metal through Xybrid, stable/expressive voice decoding presets and lighter Xybrid-supported voice quantization could not be compared without replacing the SDK/native backend. The rc.4 direct ABI offers greedy token decoding and only Accelerate/sentence flags; no preset/device selector. No SDK upgrade, native build, weight conversion or fabricated comparison was performed.

The original and revised WAVs are diagnostic output in .context/voice-pass-audio. They are never replayed by the demo. Inspection of the original waveforms found quiet endings, including 125 ms after the short line’s last sample above amplitude 0.01, with final sample zero. Complete-clip playback replaces the packet ring’s underrun/drain behavior. No additional punctuation/padding retry was justified by those waveforms.

| Gemma | Delivery | Threads | Line | Synthesis s | Audio s | RTF |
|---|---|---:|---|---:|---:|---:|
| unloaded | original | 6 | Give me your name. | 23.76 | 1.08 | 22.00 |
| unloaded | original | 6 | The gate is open, Mira. Keep those turnips away from my boots. | 77.96 | 4.72 | 16.52 |
| unloaded | original | 6 | You tried a bribe, and I let you through. Sell your turnips in the camp, then leave me to guard this gate in peace. | 99.93 | 8.08 | 12.37 |
| unloaded | clauses | 6 | Give me your name. | 17.12 | 1.08 | 15.85 |
| unloaded | clauses | 6 | The gate is open, Mira. Keep those turnips away from my boots. | 134.09 | 5.12 | 26.19 |
| unloaded | clauses | 6 | You tried a bribe, and I let you through. Sell your turnips in the camp, then leave me to guard this gate in peace. | 223.98 | 9.28 | 24.14 |
| unloaded | clauses | 4 | Give me your name. | 22.60 | 1.08 | 20.93 |
| unloaded | clauses | 4 | The gate is open, Mira. Keep those turnips away from my boots. | 182.98 | 5.12 | 35.74 |
| unloaded | clauses | 4 | You tried a bribe, and I let you through. Sell your turnips in the camp, then leave me to guard this gate in peace. | 123.21 | 9.28 | 13.28 |
| loaded | original | 6 | Give me your name. | 40.52 | 1.08 | 37.51 |
| loaded | original | 6 | The gate is open, Mira. Keep those turnips away from my boots. | 136.09 | 4.72 | 28.83 |
| loaded | original | 6 | You tried a bribe, and I let you through. Sell your turnips in the camp, then leave me to guard this gate in peace. | 19.85 | 8.08 | 2.46 |
| loaded | clauses | 6 | Give me your name. | 6.90 | 1.08 | 6.39 |
| loaded | clauses | 6 | The gate is open, Mira. Keep those turnips away from my boots. | 14.31 | 5.12 | 2.80 |
| loaded | clauses | 6 | You tried a bribe, and I let you through. Sell your turnips in the camp, then leave me to guard this gate in peace. | 24.38 | 9.28 | 2.63 |
| loaded | clauses | 4 | Give me your name. | 7.63 | 1.08 | 7.07 |
| loaded | clauses | 4 | The gate is open, Mira. Keep those turnips away from my boots. | 17.29 | 5.12 | 3.38 |
| loaded | clauses | 4 | You tried a bribe, and I let you through. Sell your turnips in the camp, then leave me to guard this gate in peace. | 26.78 | 9.28 | 2.89 |


The final clean-scene Editor confirmation passed the editable Mira handoff, parsed Gemma reply, live playback, Rhubarb cue attachment and saved original WAV. Rhubarb initially failed because its macOS executable is x86_64 and Rosetta was absent; Apple Rosetta 2 was installed, retaining Rhubarb 1.14.0. The repaired saved-audio analysis produced 28 cues, then the live Editor confirmation passed. Every measured performance condition remained skipped during those gameplay checks.
