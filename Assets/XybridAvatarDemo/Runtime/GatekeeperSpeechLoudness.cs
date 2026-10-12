using System.Collections.Generic;
using UnityEngine;

// Loudness of one speech clip, analysed once from its PCM and indexed by the audio clock.
// "Slow" follows syllables for head accents; "fast" follows laugh pulses for the bounce.
public sealed class GatekeeperSpeechLoudness
{
    public readonly float hop;
    public readonly float[] slow, fast;
    public readonly List<float> peakTimes = new List<float>();
    public readonly List<float> peakStrengths = new List<float>();
    public float Duration => slow.Length * hop;

    private GatekeeperSpeechLoudness(float hop, int frames) { this.hop = hop; slow = new float[frames]; fast = new float[frames]; }

    // Returns null when the clip's samples cannot be read (streamed audio); callers fall back to live output.
    public static GatekeeperSpeechLoudness Analyze(AudioClip clip, GatekeeperPerformanceSettings s, float fastAttack, float fastRelease)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return null;
        var pcm = new float[clip.samples * clip.channels];
        if (!clip.GetData(pcm, 0)) return null;
        int size = Mathf.Max(1, Mathf.RoundToInt(s.loudnessHopSeconds * clip.frequency));
        int frames = Mathf.CeilToInt(clip.samples / (float)size);
        var result = new GatekeeperSpeechLoudness(size / (float)clip.frequency, frames);
        var rms = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0; int count = 0;
            for (int i = f * size; i < Mathf.Min(clip.samples, (f + 1) * size); i++)
            {
                float mono = 0;
                for (int c = 0; c < clip.channels; c++) mono += pcm[i * clip.channels + c];
                mono /= clip.channels; sum += mono * mono; count++;
            }
            rms[f] = count > 0 ? Mathf.Sqrt((float)(sum / count)) : 0;
        }
        Follow(rms, result.slow, result.hop, s.envelopeAttackSeconds, s.envelopeReleaseSeconds);
        Follow(rms, result.fast, result.hop, fastAttack, fastRelease);
        Normalize(result.slow); Normalize(result.fast);
        int lookback = Mathf.Max(1, Mathf.RoundToInt(s.peakLookbackSeconds / result.hop));
        float last = -999;
        for (int f = 1; f < frames - 1; f++)
        {
            float v = result.slow[f];
            if (v < s.peakFloor || v < result.slow[f - 1] || v <= result.slow[f + 1]) continue;
            float trough = v;
            for (int k = Mathf.Max(0, f - lookback); k < f; k++) trough = Mathf.Min(trough, result.slow[k]);
            float time = f * result.hop;
            if (v < s.peakRise * Mathf.Max(trough, .02f) || time - last < s.peakCooldownSeconds) continue;
            result.peakTimes.Add(time); result.peakStrengths.Add(v); last = time;
        }
        return result;
    }

    public float Slow(float time) => Sample(slow, time);
    public float Fast(float time) => Sample(fast, time);
    private float Sample(float[] data, float time)
    {
        if (data.Length == 0 || time < 0) return 0;
        float x = time / hop; int i = Mathf.FloorToInt(x);
        if (i >= data.Length - 1) return i == data.Length - 1 ? data[i] : 0;
        return Mathf.Lerp(data[i], data[i + 1], x - i);
    }

    private static void Follow(float[] input, float[] output, float hop, float attack, float release)
    {
        float a = 1 - Mathf.Exp(-hop / Mathf.Max(attack, 1e-4f)), r = 1 - Mathf.Exp(-hop / Mathf.Max(release, 1e-4f)), env = 0;
        for (int i = 0; i < input.Length; i++) { env += (input[i] - env) * (input[i] > env ? a : r); output[i] = env; }
    }

    // Normalised to the clip's 95th percentile so quiet and loud lines both produce accents.
    private static void Normalize(float[] data)
    {
        if (data.Length == 0) return;
        var sorted = (float[])data.Clone(); System.Array.Sort(sorted);
        float reference = Mathf.Max(sorted[Mathf.Clamp(Mathf.RoundToInt(sorted.Length * .95f), 0, sorted.Length - 1)], 1e-4f);
        for (int i = 0; i < data.Length; i++) data[i] = Mathf.Clamp01(data[i] / reference);
    }
}
