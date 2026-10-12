using System;
using System.Threading;

// The SDK rc.4 adapter can implement this without changing dialogue or playback.
public interface IGatekeeperSpeechProvider : IDisposable
{
    SpeechOutcome Synthesize(string text, CancellationToken cancellation, Action<SpeechPacket> onAudio = null);
}

public enum SpeechCompletion { Completed, Cancelled, Limited }

public sealed class SpeechPacket
{
    public byte[] pcm;
    public uint sampleRate, channels;
    public ulong firstSample;
}

public sealed class SpeechOutcome
{
    public SpeechCompletion completion;
    public byte[] pcm;
    public uint sampleRate = 24000, channels = 1, chunks, limitedChunks;
    public double synthesisSeconds, firstPacketSeconds;
}

public sealed class KokoroSpeechProvider : IGatekeeperSpeechProvider
{
    private readonly Xybrid.Model model;
    private readonly string voice;
    public KokoroSpeechProvider(string bundle, string voice)
    {
        this.voice = voice;
        Xybrid.XybridClient.Initialize();
        using (var loader = Xybrid.ModelLoader.FromBundle(bundle)) model = loader.Load();
    }
    public SpeechOutcome Synthesize(string text, CancellationToken cancellation, Action<SpeechPacket> onAudio = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        byte[] pcm = model.RunTts(text, voice, 1.0);
        cancellation.ThrowIfCancellationRequested();
        onAudio?.Invoke(new SpeechPacket { pcm = pcm, sampleRate = 24000, channels = 1 });
        return new SpeechOutcome { pcm = pcm, completion = SpeechCompletion.Completed, chunks = 1,
            synthesisSeconds = timer.Elapsed.TotalSeconds, firstPacketSeconds = timer.Elapsed.TotalSeconds };
    }
    public void Dispose() => model.Dispose();
}
