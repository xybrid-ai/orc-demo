#!/usr/bin/env bash
# Build the Gemma context experiment: the headless C# harness (A/B/C prompts, runtime parser) and the managed
# Xybrid worker, both compiled against the given 0.16.0 generated bindings. No SDK or native library is built.
#   BOLT: installed package bindings, e.g. Library/PackageCache/ai.xybrid.sdk@<hash>/Runtime/Bolt
#   TFM:  net8.0 by default; pass net10.0 where only .NET 10 is installed (the Mac in this study).
# Writes .context/gemma-context/build/manifest.json: hashes of every compiled source and both built assemblies,
# which run_context.py re-verifies before inference.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
bolt="$(cd "${1:?usage: build_context.sh BOLT_BINDINGS_DIR [DOTNET] [TFM]}" && pwd)"
dotnet_cli="${2:-dotnet}"
tfm="${3:-net8.0}"
grep -q 'XybridConversationContext' "$bolt/XybridConversationContext.cs"
# Installed package bindings: confirm the package version beside them (an SDK checkout has no package.json here).
if [ -f "$bolt/../../package.json" ]; then grep -q '"version": "0.16.0"' "$bolt/../../package.json"; fi
out="$root/.context/gemma-context/build"
mkdir -p "$out/sdk/bindings/unity/Runtime" "$out/worker"
ln -sfn "$bolt" "$out/sdk/bindings/unity/Runtime/Bolt"
# HarnessTfm (not TargetFramework) so the implicit restore targets the same framework.
"$dotnet_cli" build "$here/../ScenarioChecks.csproj" -p:SdkRoot="$out/sdk" -p:HarnessTfm="$tfm" --nologo -v:q
cat > "$out/worker/NativeContextWorker.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>$tfm</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><AllowUnsafeBlocks>true</AllowUnsafeBlocks><LangVersion>9</LangVersion><Nullable>disable</Nullable></PropertyGroup>
<ItemGroup><Compile Include="$here/NativeContextWorker.cs"/><Compile Include="$bolt/*.cs"/></ItemGroup>
</Project>
XML
"$dotnet_cli" build "$out/worker/NativeContextWorker.csproj" -c Release --nologo -v:q
exporter="$here/../bin/Debug/$tfm/ScenarioChecks.dll"
worker="$out/worker/bin/Release/$tfm/NativeContextWorker.dll"
items="$("$dotnet_cli" msbuild "$here/../ScenarioChecks.csproj" -p:SdkRoot="$out/sdk" -p:HarnessTfm="$tfm" -getItem:Compile)"
python3 - "$root" "$out/manifest.json" "$tfm" "$("$dotnet_cli" --version)" "$exporter" "$worker" "$bolt" "$here" "$items" <<'PY'
import hashlib, json, pathlib, sys
root, manifest, tfm, dotnet, exporter, worker, bolt, here, items = sys.argv[1:10]
root, here, bolt = pathlib.Path(root), pathlib.Path(here), pathlib.Path(bolt)
def digest(p): return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()
def name(p):
    p = pathlib.Path(p).resolve()
    for base, label in ((bolt.resolve(), "bolt:"), (root.resolve(), "")):
        try: return label + str(p.relative_to(base))
        except ValueError: pass
    return str(p)
compiled = [pathlib.Path(i["FullPath"]) for i in json.loads(items)["Items"]["Compile"]]
sources = sorted(set(compiled) | {here / "NativeContextWorker.cs", here.parent / "ScenarioChecks.csproj", here / "build_context.sh",
                                  root / "Assets/XybridAvatarDemo/Scenarios/TrolaAuthoredConversation.json"} | set(bolt.glob("*.cs")))
json.dump(dict(tfm=tfm, dotnet=dotnet, sources={name(p): digest(p) for p in sources},
               built={"ScenarioChecks.dll": digest(exporter), "NativeContextWorker.dll": digest(worker)},
               exporter=exporter, worker=worker), open(manifest, "w"), indent=1)
print("Manifest: %d sources -> %s" % (len(sources), manifest))
PY
printf 'Exporter: %s\nWorker: %s\n' "$exporter" "$worker"
