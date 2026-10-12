using System;
using System.Diagnostics;

// Single producer / audio reader. Bounded storage, no engine or Unity calls.
// Audio reads never wait for synthesis: underruns produce silence and are counted.
public sealed class GatekeeperPcmBuffer
{
    public const int Rate = 24000, Capacity = Rate * 180;
    private readonly object gate = new object();
    private readonly float[] ring = new float[Capacity];
    private int read, write, queued;
    private bool completed, cancelled;
    private long lastAudioRead;
    private ulong delivered;
    private long gapSamples;
    private bool heard;
    private long firstAudible = -1;
    public float FirstAudibleSeconds { get { lock (gate) return firstAudible < 0 ? 0 : firstAudible / (float)Rate; } }
    public SpeechOutcome Outcome { get; private set; }
    public Exception Failure { get; private set; }
    public float DurationSeconds { get { lock (gate) return delivered / (float)Rate; } }
    public double GapSeconds { get { lock (gate) return gapSamples / (double)Rate; } }
    public bool Finished(double drainSeconds)
    {
        lock (gate) return cancelled || (completed && queued == 0 &&
            (lastAudioRead == 0 || (Stopwatch.GetTimestamp() - lastAudioRead) / (double)Stopwatch.Frequency >= drainSeconds));
    }
    public void Append(SpeechPacket packet)
    {
        if (packet.sampleRate != Rate || packet.channels != 1 || packet.pcm == null || packet.pcm.Length % 2 != 0)
            throw new ArgumentException("Expected 24 kHz mono PCM16 LE.");
        lock (gate)
        {
            if (cancelled) return;
            if (packet.firstSample != delivered) throw new ArgumentException("Non-contiguous speech packet.");
            int n = packet.pcm.Length / 2;
            if (n > Capacity - queued) throw new InvalidOperationException("Speech playback buffer exceeded 180 seconds.");
            for (int i = 0; i < n; i++)
            {
                ring[write] = (short)(packet.pcm[i * 2] | packet.pcm[i * 2 + 1] << 8) / 32768f;
                if (firstAudible < 0 && Math.Abs(ring[write]) >= .01f) firstAudible = (long)delivered + i;
                write = (write + 1) % Capacity;
            }
            queued += n; delivered += (ulong)n;
        }
    }
    public void Read(float[] output)
    {
        lock (gate)
        {
            int n = Math.Min(queued, output.Length);
            for (int i = 0; i < n; i++) { output[i] = ring[read]; read = (read + 1) % Capacity; }
            Array.Clear(output, n, output.Length - n);
            queued -= n;
            if (n > 0) { heard = true; lastAudioRead = Stopwatch.GetTimestamp(); }
            if (heard && !completed && !cancelled) gapSamples += output.Length - n;
        }
    }
    public void Complete(SpeechOutcome outcome, Exception failure = null)
    {
        lock (gate) { Outcome = outcome; Failure = failure; completed = true; }
    }
    public void Cancel()
    {
        lock (gate) { cancelled = true; queued = 0; }
    }
}
