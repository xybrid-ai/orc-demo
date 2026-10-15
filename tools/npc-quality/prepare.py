#!/usr/bin/env python3
"""Download a pinned NPC model, verify its hash, and optionally requantize Q8."""
import argparse,hashlib,json,pathlib,subprocess,urllib.request
MODELS={
 "no": ("Gemma3NPC-4B-no-thinking-GGUF","8101a981ad54f263cc321e8c939ca08be3159865","Gemma3-4B-no-thinking-Q8_0.gguf","76fffc23fa1b0cc64977bd68045abb4fede4f0308ee8b903c6ee4d4013846164"),
 "post": ("Gemma3NPC-4B-post-thinking-GGUF","03314336a0892984182b6bf2b8df29a10488809b","Gemma3-4B-post-thinking-Q8_0.gguf","4fa1689f9661e4183f072cc9694ce0c8cf5e0f91e787e9842640395ddba69c7f"),
 "e4b": ("Gemma4NPC-E4B-GGUF","5b1e65fc3ac2e10f9d3a846ed6fbf0ee25308499","Gemma4NPC-E4B-Q4_K_M.gguf","226e466bfa56f833da117dab73722aebaab88f121005e76458fdb477d5d3e4a0")
}
def digest(path):
 h=hashlib.sha256()
 with path.open("rb") as f:
  for block in iter(lambda:f.read(8*1024*1024),b""):h.update(block)
 return h.hexdigest()
def main():
 p=argparse.ArgumentParser();p.add_argument("variant",choices=MODELS);p.add_argument("directory",type=pathlib.Path);p.add_argument("--quantizer");p.add_argument("--source-directory",type=pathlib.Path);args=p.parse_args()
 repo,revision,filename,sha=MODELS[args.variant];dest=args.directory.resolve();dest.mkdir(parents=True,exist_ok=True)
 source_dir=(args.source_directory or dest).resolve();source_dir.mkdir(parents=True,exist_ok=True);source=source_dir/filename
 if args.variant!="e4b" and not args.quantizer:p.error("--quantizer is required for a Q8-to-Q4 conversion")
 url=f"https://huggingface.co/chimbiwide/{repo}/resolve/{revision}/{filename}"
 if not source.exists():
  partial=source.with_suffix(".partial")
  urllib.request.urlretrieve(url,partial);partial.replace(source)
 if digest(source)!=sha:raise RuntimeError("Source SHA-256 mismatch: "+str(source))
 output=dest/(filename.replace("Q8_0","Q4_K_M") if args.variant!="e4b" else filename)
 if args.variant!="e4b":subprocess.run([args.quantizer,"--allow-requantize",str(source),str(output),"Q4_K_M","4"],check=True)
 elif source!=output:
  import shutil
  shutil.copyfile(source,output)
 metadata=dict(model_id=args.variant+"-q4",version=revision,execution_template=dict(type="Gguf",model_file=output.name,context_length=4096),files=[output.name],metadata=dict(backend="llamacpp",task="text-generation",instruction_tuned=True,quantization="Q4_K_M"),preprocessing=[],postprocessing=[])
 (dest/"model_metadata.json").write_text(json.dumps(metadata,indent=2)+"\n")
 (dest/"provenance.json").write_text(json.dumps(dict(repo="chimbiwide/"+repo,revision=revision,source_sha256=sha,output_sha256=digest(output),bytes=output.stat().st_size,requantized=args.variant!="e4b",context_length=4096),indent=2)+"\n")
 print(output)
if __name__=="__main__":main()
