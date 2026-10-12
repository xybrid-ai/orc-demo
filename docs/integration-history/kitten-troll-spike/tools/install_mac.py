#!/usr/bin/env python3
"""Baseline-guard and compile the demo before copying scripts; never save a scene."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=pathlib.Path, default=pathlib.Path.home() / "Dev/games/Xybrid Tavern")
    parser.add_argument("--unity", type=pathlib.Path, help="Unity.app path; defaults to the project's exact version")
    args = parser.parse_args()
    project = args.project.resolve()
    demo = project / "Assets/XybridAvatarDemo"
    expected = json.loads((ROOT / "baseline-sha256.json").read_text())
    for name, value in expected.items():
        destination = demo / "Runtime" / name
        proposed = ROOT / "Runtime" / name
        if not destination.exists() or digest(destination) not in [*(value if isinstance(value, list) else [value]), digest(proposed)]:
            raise SystemExit("Demo source changed; merge before installing: " + str(destination))
    for source in (ROOT / "Runtime").glob("*.cs"):
        destination = demo / "Runtime" / source.name
        if source.name not in expected and destination.exists() and digest(source) != digest(destination):
            raise SystemExit("A different provider/control script already exists: " + str(destination))
    version = re.search(r"^m_EditorVersion: (.+)$", (project / "ProjectSettings/ProjectVersion.txt").read_text(), re.M).group(1)
    unity = args.unity or pathlib.Path("/Applications/Unity/Hub/Editor") / version / "Unity.app"
    scripting = unity / "Contents/Resources/Scripting"
    responses = list((project / "Library/Bee/artifacts").glob("*/Assembly-CSharp.rsp"))
    if not responses:
        raise SystemExit("Open the Unity project and let it compile first (no compiler response file).")
    source = max(responses, key=lambda p: p.stat().st_mtime)
    output = ROOT / "build/compiler-output"
    output.mkdir(parents=True, exist_ok=True)
    lines, seen = [], set()
    for line in source.read_text().splitlines():
        match = re.match(r'^"Assets/XybridAvatarDemo/Runtime/(.+\.cs)"$', line)
        if match and (ROOT / "Runtime" / match.group(1)).exists():
            line = '"' + str(ROOT / "Runtime" / match.group(1)) + '"'
            seen.add(match.group(1))
        elif line.startswith("-out:"):
            line = '-out:"' + str(output / "Assembly-CSharp.dll") + '"'
        elif line.startswith("-refout:"):
            line = '-refout:"' + str(output / "Assembly-CSharp.ref.dll") + '"'
        lines.append(line)
    for script in sorted((ROOT / "Runtime").glob("*.cs")):
        if script.name not in seen:
            lines.append('"' + str(script) + '"')
    response = output / "Assembly-CSharp.rsp"
    response.write_text("\n".join(lines) + "\n")
    result = subprocess.run([str(scripting / "NetCoreRuntime/dotnet"), "exec", str(scripting / "DotNetSdkRoslyn/csc.dll"),
                             "/noconfig", "@" + str(response)], cwd=project, capture_output=True, text=True)
    (output / "compile.log").write_text(result.stdout + result.stderr)
    print(result.stdout, result.stderr)
    if result.returncode:
        raise SystemExit("Unity compiler failed; no scripts installed.")
    backup = project / "Library/XybridAvatarDemo/Backups" / ("kitten-spike-" + str(int(time.time())))
    for script in sorted((ROOT / "Runtime").glob("*.cs")):
        destination = demo / "Runtime" / script.name
        if destination.exists():
            backup.mkdir(parents=True, exist_ok=True)
            shutil.copy2(destination, backup / script.name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(script, destination)
    # The editor helper is independent of runtime compilation and scene content.
    for script in sorted((ROOT / "Editor").glob("*.cs")):
        destination = demo / "Editor" / script.name
        if destination.exists():
            backup.mkdir(parents=True, exist_ok=True)
            shutil.copy2(destination, backup / script.name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(script, destination)
    print("Installed runtime scripts and editor helper; backup:", backup)
    print("Scene was not edited. Refresh Assets; press Play, F4 selects live voice. F3 retains timing overlay.")


if __name__ == "__main__":
    main()
