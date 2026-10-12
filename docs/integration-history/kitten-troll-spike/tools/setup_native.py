#!/usr/bin/env python3
"""Relink a checksum-pinned rc.4 archive and stage the four prepared assets."""
import argparse
import hashlib
import json
import pathlib
import platform
import shutil
import subprocess
import tarfile
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
PINS = json.loads((ROOT / "assets.json").read_text())


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for part in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(part)
    return digest.hexdigest()


def verified(path, expected):
    if not path.is_file() or sha(path) != expected:
        raise SystemExit("Missing or changed pinned asset: " + str(path))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=pathlib.Path, help="Stage plugin/models into a Unity project")
    parser.add_argument("--models", type=pathlib.Path, default=pathlib.Path.home() / ".zzz/models")
    parser.add_argument("--zzz-checkout", type=pathlib.Path, required=True)
    parser.add_argument("--archive", type=pathlib.Path, help="Use an already downloaded release archive")
    args = parser.parse_args()
    system, machine = platform.system(), platform.machine()
    if system == "Darwin" and machine == "arm64":
        slice_name, extension = "macos-arm64", "dylib"
    elif system == "Linux" and machine == "x86_64":
        flags = pathlib.Path("/proc/cpuinfo").read_text().split("flags", 1)[-1].splitlines()[0].split()
        required = {"avx2", "fma", "bmi1", "bmi2", "f16c", "movbe", "popcnt", "sse4_2", "xsave"}
        if not required.issubset(flags):
            raise SystemExit("The rc.4 Linux archive requires x86-64-v3.")
        slice_name, extension = "linux-x86_64", "so"
    else:
        raise SystemExit("This desktop spike supports native macOS ARM64 and Linux x86-64-v3 only.")
    sources = {
        "kitten-tts-2-q2_0.gguf": args.models / "kitten-tts-2-q2_0.gguf",
        "kitten-s3-meanflow-f32.gguf": args.models / "kitten-s3-meanflow-f32.gguf",
        "bruno.lm.json": args.zzz_checkout / "fixtures/reference/kitten-tts-2/bruno-short.json",
        "bruno.s3.json": args.zzz_checkout / "fixtures/reference/kitten-s3/bruno-voice.json",
    }
    for name, source in sources.items():
        verified(source, PINS["assets"][name])
    build = ROOT / "build" / slice_name
    build.mkdir(parents=True, exist_ok=True)
    basename = "zzz-kitten-tts2-" + slice_name + "-" + PINS["engine_tag"]
    archive = args.archive or build / (basename + ".tar.gz")
    if not archive.exists():
        subprocess.run(["gh", "release", "download", PINS["engine_tag"], "-R", "xybrid-ai/zzz-research",
                        "-p", archive.name, "-D", str(build)], check=True)
    verified(archive, PINS["archives"][slice_name])
    with tarfile.open(archive) as bundle:
        for item in bundle.getmembers():
            parts = pathlib.PurePosixPath(item.name).parts
            if not parts or parts[0] != basename or ".." in parts or item.name.startswith("/") or not (item.isfile() or item.isdir()):
                raise SystemExit("Unsafe release archive entry: " + item.name)
        bundle.extractall(build)
    payload = build / basename
    receipt = json.loads((payload / "RECEIPT.json").read_text())
    if receipt["source_commit"] != PINS["source_commit"] or receipt["abi_version"] != 1 or receipt["slice"] != slice_name:
        raise SystemExit("Release receipt differs from pinned rc.4.")
    library = build / ("libzzz_embed." + extension)
    static = payload / "lib/libzzz_embed.a"
    if system == "Darwin":
        subprocess.run(["cc", "-dynamiclib", "-mmacosx-version-min=13.0", "-Wl,-force_load," + str(static),
                        "-Wl,-install_name,@rpath/libzzz_embed.dylib", "-o", str(library)], check=True)
        subprocess.run(["codesign", "--force", "--sign", "-", str(library)], check=True)
    else:
        subprocess.run(["cc", "-shared", "-Wl,--no-undefined", "-Wl,--whole-archive", str(static),
                        "-Wl,--no-whole-archive", "-Wl,-soname,libzzz_embed.so", "-pthread", "-lm", "-ldl", "-o", str(library)], check=True)
    (build / "provenance.json").write_text(json.dumps({"release": receipt, "plugin_sha256": sha(library), "assets": PINS["assets"]}, indent=2) + "\n")
    if args.project:
        assets = args.project / "Assets"
        if not (args.project / "ProjectSettings/ProjectVersion.txt").is_file():
            raise SystemExit("Not a Unity project: " + str(args.project))
        models = assets / "StreamingAssets/KittenTTS2"
        plugins = assets / "XybridAvatarDemo/Plugins" / slice_name
        models.mkdir(parents=True, exist_ok=True)
        plugins.mkdir(parents=True, exist_ok=True)
        for name, source in sources.items():
            destination = models / name
            if destination.exists():
                verified(destination, PINS["assets"][name])
            else:
                temporary = destination.with_suffix(destination.suffix + ".partial")
                shutil.copy2(source, temporary)
                verified(temporary, PINS["assets"][name])
                temporary.replace(destination)
        destination = plugins / library.name
        if destination.exists() and sha(destination) != sha(library):
            backup = args.project / "Library/XybridAvatarDemo/Backups" / ("kitten-plugin-" + uuid.uuid4().hex)
            backup.mkdir(parents=True)
            shutil.copy2(destination, backup / destination.name)
        shutil.copy2(library, destination)
        metadata = pathlib.Path(str(destination) + ".meta")
        guid = uuid.uuid4().hex
        if metadata.exists():
            import re
            match = re.search(r"^guid: (\w+)$", metadata.read_text(), re.M)
            if match:
                guid = match.group(1)
        editor_os = "OSX" if system == "Darwin" else "Linux"
        target = "OSXUniversal" if system == "Darwin" else "Linux64"
        cpu = "ARM64" if system == "Darwin" else "x86_64"
        metadata.write_text("""fileFormatVersion: 2
guid: %s
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: %s
        OS: %s
  - first:
      Standalone: %s
    second:
      enabled: 1
      settings:
        CPU: %s
  userData:
  assetBundleName:
  assetBundleVariant:
""" % (guid, cpu, editor_os, target, cpu))
        shutil.copy2(build / "provenance.json", models / "provenance.json")
    print("Built", library)
    print("Staged assets" if args.project else "Verified assets", PINS["assets"])


if __name__ == "__main__":
    main()
