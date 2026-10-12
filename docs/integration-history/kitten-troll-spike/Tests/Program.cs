using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

static class Program
{
    private static int checks;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        checks++; Console.WriteLine("PASS: " + message);
    }
    private static KittenNativeSpeechProvider Open(string root, bool sentences = false, uint cap = 0) =>
        new KittenNativeSpeechProvider(Path.Combine(root, "kitten-tts-2-q2_0.gguf"),
            Path.Combine(root, "kitten-s3-meanflow-f32.gguf"), Path.Combine(root, "bruno.lm.json"),
            Path.Combine(root, "bruno.s3.json"), 6, sentences, true, cap);
    static async Task Main(string[] args)
    {
        KittenNativeSpeechProvider.AssertLayout(); Check(true, "C ABI layouts match rc.4");
        var buffer = new GatekeeperPcmBuffer();
        var packet = new SpeechPacket { pcm = new byte[] { 0, 64, 0, 192 }, channels = 1, sampleRate = 24000 };
        buffer.Append(packet); var output = new float[4]; buffer.Read(output);
        Check(output.SequenceEqual(new float[] { .5f, -.5f, 0, 0 }), "PCM16 endian conversion and nonblocking underrun");
        bool refused = false;
        try { buffer.Append(packet); } catch (ArgumentException) { refused = true; }
        Check(refused, "non-contiguous packets rejected");
        buffer.Cancel(); buffer.Append(new SpeechPacket { pcm = new byte[] { 0, 64 }, channels = 1, sampleRate = 24000, firstSample = 2 });
        buffer.Read(output); Check(output.All(v => v == 0) && buffer.Finished(1), "stale packets after interruption stay silent");
        try { Open("/missing-kittten-assets"); throw new Exception("Missing assets accepted"); }
        catch (FileNotFoundException) { Check(true, "missing assets produce an actionable file error"); }
        if (args.Length == 0) { Console.WriteLine("Real engine tests require MODEL_DIR [REPORT.json]."); return; }
        if (args[0] == "--active-shutdown")
        {
            var provider = Open(args[1]);
            var task = Task.Run(() => provider.Synthesize("The village gate stays closed until you tell me your true business.", CancellationToken.None));
            await Task.Delay(100);
            var close = Stopwatch.StartNew();
            await Task.Run(provider.Dispose);
            var stopped = await task;
            Check(stopped.completion == SpeechCompletion.Cancelled, "active close cancels and drains computation");
            Check(close.Elapsed.TotalSeconds < 10, "active close completes within ten seconds");
            provider.Dispose();
            if (args.Length > 2) File.WriteAllText(args[2], JsonSerializer.Serialize(new { checks, shutdownMs = close.Elapsed.TotalMilliseconds }));
            return;
        }
        var records = new List<object>();
        foreach (bool sentences in new[] { false, true })
        {
            using (var provider = Open(args[0], sentences))
            {
                var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { provider.Synthesize("Queued cancellation.", cancelled.Token); throw new Exception("Started cancelled work"); }
                catch (OperationCanceledException) { Check(true, "queued cancellation does not enter synthesis"); }
                using (var stop = new CancellationTokenSource())
                {
                    var timer = Stopwatch.StartNew();
                    var task = Task.Run(() => provider.Synthesize("The village gate stays closed until you tell me your true business.", stop.Token));
                    await Task.Delay(100); double at = timer.Elapsed.TotalSeconds; stop.Cancel();
                    var result = await task;
                    Check(result.completion == SpeechCompletion.Cancelled, "native cancellation reaches computation");
                    records.Add(new { kind = "cancel", sentences, cancellationMs = (timer.Elapsed.TotalSeconds - at) * 1000 });
                }
                var texts = new[] { "Your name sounds like trouble.", "The village gate stays closed until you tell me your true business.",
                    "Tell me your name. Then explain your business. The village gate stays closed until I know who you are and what you want here today." };
                for (int i = 0; i < texts.Length; i++)
                {
                    for (int run = 0; run < 2; run++)
                    {
                        var retained = new List<SpeechPacket>();
                        var timer = Stopwatch.StartNew(); double first = 0; ulong next = 0;
                        var result = provider.Synthesize(texts[i], CancellationToken.None, p =>
                        {
                            if (retained.Count == 0) first = timer.Elapsed.TotalSeconds;
                            if (p.firstSample != next) throw new Exception("Bad sample offsets");
                            next += (ulong)p.pcm.Length / 2; retained.Add(p);
                        });
                        Check(result.completion == SpeechCompletion.Completed && retained.Count > 0, "completed speech after cancellation and resident-session reuse");
                        double audioSeconds = next / 24000.0;
                        Check(i != 2 || audioSeconds > 5.1, "long speech exceeds the old 5.1-second cap");
                        // Retained packets must own their data after native return and GC.
                        int bytes = retained.Sum(p => p.pcm.Length); GC.Collect();
                        Check(bytes > 0 && retained.SelectMany(p => p.pcm).Any(v => v != 0), "owned packets survive callback return and GC");
                        records.Add(new { kind = i == 0 ? "short" : i == 1 ? "long" : "sentences", sentences, run,
                            coldOpenMs = provider.OpenSeconds * 1000, firstPacketMs = first * 1000,
                            synthesisMs = result.synthesisSeconds * 1000, audioSeconds, result.chunks, result.limitedChunks,
                            processMemoryBytes = Process.GetCurrentProcess().WorkingSet64 });
                    }
                }
            }
        }
        using (var limited = Open(args[0], cap: 32))
        {
            var result = limited.Synthesize("The village gate stays closed until you tell me your true business.", CancellationToken.None);
            Check(result.completion == SpeechCompletion.Limited && result.limitedChunks > 0 && result.pcm.Length > 0,
                "explicit token limit reports partial speech");
        }
        var disposed = Open(args[0]); disposed.Dispose(); disposed.Dispose();
        try { disposed.Synthesize("After shutdown.", CancellationToken.None); throw new Exception("Used closed handle"); }
        catch (ObjectDisposedException) { Check(true, "shutdown closes once and refuses subsequent synthesis"); }
        if (args.Length > 1) File.WriteAllText(args[1], JsonSerializer.Serialize(new { checks, runtime = RuntimeInformation(), records },
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        Console.WriteLine(checks + " checks passed. Native host timings are not Unity gameplay measurements.");
    }
    static string RuntimeInformation() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
}
