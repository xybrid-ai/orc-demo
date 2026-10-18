#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
sdk_root="$(cd "${1:?usage: build.sh SDK_ROOT [DOTNET]}" && pwd)"
dotnet_cli="${2:-dotnet}"
mkdir -p ../../.context/npc-quality/sdk-0.9
# Source compatibility stays on the demo's SDK version; no native SDK upgrade.
git -C "$sdk_root" archive v0.9.0 bindings/unity/Runtime/Bolt | tar -x -C ../../.context/npc-quality/sdk-0.9
"$dotnet_cli" build ScenarioChecks.csproj -p:SdkRoot="$(cd ../../.context/npc-quality/sdk-0.9 && pwd)" --nologo -v:q
python3 - "$sdk_root" <<'PYBUILD'
from pathlib import Path
import sys,json
sdk=Path(sys.argv[1]); dest=Path('../../.context/npc-quality/runtime').resolve();dest.mkdir(parents=True,exist_ok=True)
(dest/'Cargo.toml').write_text('[package]\nname = "orc-npc-worker"\nversion = "0.1.0"\nedition = "2021"\n[workspace]\n[dependencies]\nanyhow = "1"\nserde_json = "1"\nxybrid-sdk = { path = '+json.dumps(str(sdk/'crates/xybrid-sdk'))+', features = ["llm-llamacpp"] }\n[[bin]]\nname = "npc-worker"\npath = '+json.dumps(str(Path('worker.rs').resolve()))+'\n')
PYBUILD
cargo build --release --manifest-path ../../.context/npc-quality/runtime/Cargo.toml --target-dir "$sdk_root/target"
printf 'Worker: %s/target/release/npc-worker\n' "$sdk_root"
