using System;
using System.Diagnostics;
using System.Threading;

// Enable XYBRID_KITTEN_RC4_SDK after installing the matching local SDK package/plugin.
// Model metadata owns threads, max_tokens, sentence_chunks and accelerate settings.
public sealed class KittenSdkSpeechProvider : IGatekeeperSpeechProvider
{
#if XYBRID_KITTEN_RC4_SDK
    public const bool Available = true;
    private readonly Xybrid.Model model;
    public KittenSdkSpeechProvider(string directory)
    {
        Xybrid.XybridClient.Initialize();
        using (var loader = Xybrid.ModelLoader.FromDirectory(directory)) model = loader.Load();
    }
    public SpeechOutcome Synthesize(string text, CancellationToken cancellation, Action<SpeechPacket> onAudio = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        using var input = Xybrid.Envelope.Text(text);
        if (onAudio == null)
        {
            var result = model.Run(input, cancellationToken: cancellation);
            var status = Completion(result.SpeechStatus.ToString());
            if (!result.Success && status != SpeechCompletion.Limited && status != SpeechCompletion.Cancelled)
                throw new InvalidOperationException("Kitten SDK batch inference failed.");
            return new SpeechOutcome { completion = status, pcm = result.AudioBytes,
                limitedChunks = (uint)result.LimitedChunks, synthesisSeconds = timer.Elapsed.TotalSeconds,
                firstPacketSeconds = timer.Elapsed.TotalSeconds };
        }
        double first = 0;
        bool delivered = false;
        var streaming = model.RunTtsStreaming(input, chunk =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (!delivered) { first = timer.Elapsed.TotalSeconds; delivered = true; }
            onAudio(new SpeechPacket { pcm = chunk.Pcm, sampleRate = chunk.SampleRate,
                channels = chunk.Channels, firstSample = chunk.FirstSample });
        }, cancellation);
        if (streaming.Status.ToString() == "Failed") throw new InvalidOperationException(streaming.Error);
        return new SpeechOutcome { completion = Completion(streaming.Status.ToString()),
            sampleRate = streaming.SampleRate, channels = streaming.Channels, chunks = streaming.Chunks,
            limitedChunks = streaming.LimitedChunks, synthesisSeconds = timer.Elapsed.TotalSeconds, firstPacketSeconds = first };
    }
    private static SpeechCompletion Completion(string status)
    {
        switch (status)
        {
            case "Completed": return SpeechCompletion.Completed;
            case "Cancelled": return SpeechCompletion.Cancelled;
            case "Limited": return SpeechCompletion.Limited;
            default: throw new InvalidOperationException("Kitten SDK returned speech status " + status + ".");
        }
    }
    public void Dispose() => model.Dispose();
#else
    public const bool Available = false;
    public KittenSdkSpeechProvider(string directory) => throw new NotSupportedException("Install the local rc.4 SDK and enable XYBRID_KITTEN_RC4_SDK first.");
    public SpeechOutcome Synthesize(string text, CancellationToken cancellation, Action<SpeechPacket> onAudio = null) => throw new NotSupportedException();
    public void Dispose() { }
#endif
}
