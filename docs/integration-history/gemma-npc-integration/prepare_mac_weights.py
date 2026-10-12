"""Prepare the Q4_K_M GGUF for Metal libraries without a BF16 matrix kernel.

Only BF16 auxiliary tensors become F16. Quantized weights retain identical
bytes. Keep the verified upstream file outside Assets and pass a new destination.
Requires the gguf and numpy Python packages; does not run inference or build.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

import gguf
import numpy as np

UPSTREAM_SHA256 = "d808fcac6160f69139cca6583e3d66470f2b21fb3b6a1895ee01bf47a29f721e"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def quantized_digest(reader):
    result = hashlib.sha256()
    for tensor in reader.tensors:
        if tensor.tensor_type not in (
            gguf.GGMLQuantizationType.F32,
            gguf.GGMLQuantizationType.F16,
            gguf.GGMLQuantizationType.BF16,
        ):
            result.update(tensor.name.encode())
            result.update(memoryview(tensor.data).cast("B"))
    return result.hexdigest()


def prepare(source, destination, report):
    if source.resolve() == destination.resolve():
        raise ValueError("Keep the verified upstream file separate from the converted file.")
    if digest(source) != UPSTREAM_SHA256:
        raise ValueError("The source does not match the pinned upstream Q4_K_M SHA-256.")
    original = gguf.GGUFReader(str(source))
    original_quantized = quantized_digest(original)
    temporary = destination.with_name(destination.name + ".fp16.part")
    shutil.copyfile(source, temporary)
    reader = gguf.GGUFReader(str(temporary), mode="r+")
    converted = []
    for tensor in reader.tensors:
        if tensor.tensor_type != gguf.GGMLQuantizationType.BF16:
            continue
        words = tensor.data.reshape(-1).view("<u2")
        # Work in bounded chunks; BF16 and F16 both occupy two bytes, so tensor
        # sizes and GGUF offsets stay unchanged.
        for first in range(0, words.size, 1024 * 1024):
            block = words[first:first + 1024 * 1024]
            values = (block.astype(np.uint32) << 16).view(np.float32)
            if not np.isfinite(values).all() or (np.abs(values) > 65504).any():
                raise ValueError("Auxiliary tensor values exceed finite FP16 range.")
            block[:] = values.astype(np.float16).view(np.uint16)
        tensor.field.parts[-2][0] = int(gguf.GGMLQuantizationType.F16)
        converted.append(tensor.name)
    reader.data.flush()
    validated = gguf.GGUFReader(str(temporary))
    if any(t.tensor_type == gguf.GGMLQuantizationType.BF16 for t in validated.tensors):
        raise ValueError("BF16 tensors remain in the prepared GGUF.")
    if quantized_digest(validated) != original_quantized:
        raise ValueError("Quantized tensor bytes changed unexpectedly.")
    result = {
        "upstream_sha256": UPSTREAM_SHA256,
        "prepared_sha256": digest(temporary),
        "converted_tensors": converted,
        "quantized_weights_unchanged": True,
        "bytes": temporary.stat().st_size,
    }
    temporary.replace(destination)
    report.write_text(json.dumps(result, indent=2) + "\n")
    return result


if __name__ == "__main__":
    args = argparse.ArgumentParser(description=__doc__)
    args.add_argument("source", type=Path)
    args.add_argument("destination", type=Path)
    args.add_argument("report", type=Path)
    options = args.parse_args()
    print(json.dumps(prepare(options.source, options.destination, options.report)))
