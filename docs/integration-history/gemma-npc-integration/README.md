> Archived integration snapshot. Use the project root README and current
> `Assets/XybridAvatarDemo` implementation. Do not run these historical installers
> against the current demo. See [integration history](../README.md).

# Gemma4NPC free-form gatekeeper

This patch installs into `/Users/glennsonna/Dev/games/Xybrid Tavern`. Free-form
replies use local Xybrid/llama.cpp with
[chimbiwide/Gemma4NPC-E2B-GGUF](https://huggingface.co/chimbiwide/Gemma4NPC-E2B-GGUF),
revision `b8e57bca1c8e5455178c16cd9554b2f75bb0c9fc`, Q4_K_M.

Place `Gemma4NPC-E2B-Q4_K_M.gguf` and `model_metadata.json` in
`Assets/StreamingAssets/XybridModels/gemma4npc-e2b-q4/`.
The GGUF is 3,427,863,872 bytes; SHA-256:
`d808fcac6160f69139cca6583e3d66470f2b21fb3b6a1895ee01bf47a29f721e`.
Weights are downloaded separately and never committed.

On the M1, the installed 0.9.0 Metal library lacks the BF16 matrix kernel
required by the upstream `per_layer_model_proj.weight` tensor. Preserve the
verified original outside Assets, then run `prepare_mac_weights.py ORIGINAL
DESTINATION REPORT` using Python with `gguf` and `numpy` installed. This converts
that one auxiliary tensor to FP16 and verifies that every quantized tensor
retains identical bytes. The resulting GGUF remains Q4_K_M, with SHA-256
`2a6f2d10f8ca60f357048a8ec9dd52c7d2f9f81d777ccd8c1a86373a89295846`.
Use the original filename for the prepared file in the model directory.
This is asset preparation; it requires no native build or SDK replacement.

`install_mac.py` checks the inspected source baseline, backs up existing scripts
under `Library/XybridAvatarDemo/Backups`, installs these sources, and adds the
scenario debug switch plus the F4 reply selector. Invoke `install(project,
source, baselines)` with SHA-256 hashes of the four original line-source files
(after trimming trailing whitespace). Use it only with Play Mode stopped.
It does not build, run measurements, or save scenes.

The character card follows the model card's Background, Location, Quest and
Roleplaying Instructions format. Camp facts, ten style examples and the allowed
emotion tags come from the actual scenario. A resident scene-owned model primes
`Greetings` during the intro; the GGUF's embedded Gemma 4 chat template formats
the system card, whose stable prefix is reused by Xybrid's KV cache. Each turn
adds the name, business, authored ending, recorded approaches, current patience,
the last six player exchanges and the new message. History is supplied once;
the native conversation context holds only the persistent persona.

The installed SDK 0.9.0 wrapper lacks a grammar setter, so this adapter uses its
existing generated Bolt API directly. GBNF constrains all five output fields and
the scenario's emotion enum. Generation uses the model card's temperature 1,
top-p .95 and top-k 64, with cloud fallback disabled. The 112-token envelope
budget includes JSON; spoken text is capped at two sentences and approximately
40 tokens (28 words / 180 characters). Stage directions and asterisks are stripped
before constructing the line sent to speech. Invalid or empty replies use three
generic authored lines. The performer applies the clamped -1/0/1 patience delta;
at zero the source plays every line in the authored `expelled` ending.

F4's **Debug placeholder** button restores the original placeholder source and
restarts the encounter; **Use model** switches back. `debugStartInFreeForm` still
skips the authored tree for development. Loading and token pulls stay off Unity's
main thread; stream closure drains native work before handles are released.

For the one Editor confirmation, enter Play Mode and call
`XybridNpcEditorCheck.Begin("/tmp/xybrid-gemma-editor.json")` via the existing Unity
MCP relay. It sends one player message through the real controller, requires a
parsed model line to reach live speech, and checks the authored expulsion
sequence without another model/speech run. It unregisters its own callback and
restarts the normal encounter on success. No build or benchmark is involved.
