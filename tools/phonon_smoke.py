#!/usr/bin/env python3
"""Compile the actual C# provider and generate one fresh sentence through its native ABI."""
from pathlib import Path
import subprocess
import sys
import xml.sax.saxutils as xml

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / '.context/phonon-smoke'
WORK.mkdir(parents=True, exist_ok=True)

cs = '''using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
NativeLibrary.SetDllImportResolver(typeof(PhononNativeSpeechProvider).Assembly,
    (name,assembly,path)=>(name=="orc_phonon" || name=="orc_phonon_zzz")?NativeLibrary.Load(args[0]):IntPtr.Zero);
using var provider = new PhononNativeSpeechProvider(args[1], args[2], 6, args.Length>3 ? PhononEngine.Zzz407 : PhononEngine.XnPtts);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var result = provider.Synthesize("The gate is open, Mira.", deadline.Token);
if (result.completion != SpeechCompletion.Completed || result.pcm == null || result.pcm.Length < 2
    || result.sampleRate != 24000 || result.channels != 1 || result.chunks == 0)
    throw new Exception("Invalid Phonon outcome");
bool nonzero=false;
for(int i=0;i<result.pcm.Length;i+=2) if(BitConverter.ToInt16(result.pcm,i)!=0) {nonzero=true;break;}
if(!nonzero)throw new Exception("Silent Phonon output");
File.WriteAllBytes(args.Length>3 ? "zzz407.pcm16" : "xn-ptts.pcm16", result.pcm);
Console.WriteLine($"PASS: fresh cloned voice, {result.pcm.Length/48000.0:F2}s audio, {result.synthesisSeconds:F3}s synthesis; first packet {result.firstPacketSeconds:F3}s; open {provider.OpenSeconds:F3}s.");
namespace AOT { [AttributeUsage(AttributeTargets.Method)] public sealed class MonoPInvokeCallbackAttribute : Attribute { public MonoPInvokeCallbackAttribute(Type type) {} } }
'''
(WORK / 'Program.cs').write_text(cs)
runtime = ROOT / 'Assets/XybridAvatarDemo/Runtime'
paths = [runtime / 'IGatekeeperSpeechProvider.cs', runtime / 'PhononNativeSpeechProvider.cs']
refs = ROOT / 'Library/ScriptAssemblies/Xybrid.SDK.Runtime.dll'
includes = ''.join('<Compile Include="' + xml.escape(str(p), {'"': '&quot;'}) + '" />' for p in paths)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>' + includes
project += '<Reference Include="Xybrid.SDK.Runtime"><HintPath>' + xml.escape(str(refs)) + '</HintPath></Reference></ItemGroup></Project>'
(WORK / 'Smoke.csproj').write_text(project)
subprocess.run(['/opt/homebrew/bin/dotnet', 'build', str(WORK / 'Smoke.csproj'), '-c', 'Release'], check=True)
zzz = "--zzz" in sys.argv
subprocess.run(['/opt/homebrew/bin/dotnet', str(WORK / 'bin/Release/net10.0/Smoke.dll'),
                str(ROOT / 'Assets/XybridAvatarDemo/Plugins/macos-arm64/liborc_phonon_zzz.dylib' if zzz else ROOT / 'Assets/XybridAvatarDemo/Plugins/macos-arm64/liborc_phonon.dylib'),
                str(ROOT / 'Assets/StreamingAssets/PhononZzz/model.gguf') if zzz else str(ROOT / 'Assets/StreamingAssets/Phonon'),
                'trola_clone' if zzz else str(ROOT / 'Assets/StreamingAssets/Phonon/voice.safetensors')] + (['zzz'] if zzz else []), check=True, cwd=WORK)
