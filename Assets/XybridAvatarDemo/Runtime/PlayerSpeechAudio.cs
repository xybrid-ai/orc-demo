using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// What the microphone hands the recognizer: mono, 16 kHz, trimmed to the speech, as a PCM16 WAV (Xybrid's whisper.cpp
// runtime takes a complete WAV file). Pure, so tools/npc-quality/PlayerSpeechChecks.cs can test it without Unity.
public static class PlayerSpeechAudio
{
    public const int Rate = 16000;
    private const int Frame = Rate / 50; // 20 ms

    public static float[] ToMono(float[] interleaved, int channels)
    {
        if (interleaved == null) return Array.Empty<float>();
        if (channels <= 1) return (float[])interleaved.Clone();
        var mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    // Windowed-sinc low-pass below the new Nyquist, then linear interpolation onto the 16 kHz grid. A plain linear
    // resampler would fold the microphone's 8-24 kHz content into the speech band.
    public static float[] Resample(float[] mono, int fromRate)
    {
        if (mono == null || mono.Length == 0 || fromRate <= 0) return Array.Empty<float>();
        if (fromRate == Rate) return (float[])mono.Clone();
        float[] source = mono;
        if (fromRate > Rate)
        {
            double cutoff = .45 * Rate / fromRate; // cycles per input sample
            const int half = 24;
            var taps = new double[2 * half + 1];
            double total = 0;
            for (int k = -half; k <= half; k++)
            {
                double sinc = k == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * k) / (Math.PI * k);
                double window = .54 + .46 * Math.Cos(Math.PI * k / half);
                taps[k + half] = sinc * window; total += taps[k + half];
            }
            source = new float[mono.Length];
            for (int i = 0; i < mono.Length; i++)
            {
                double acc = 0;
                for (int k = -half; k <= half; k++)
                {
                    int j = i + k;
                    if (j >= 0 && j < mono.Length) acc += mono[j] * taps[k + half];
                }
                source[i] = (float)(acc / total);
            }
        }
        int length = (int)((long)source.Length * Rate / fromRate);
        var output = new float[length];
        double step = (double)fromRate / Rate;
        for (int i = 0; i < length; i++)
        {
            double at = i * step; int a = (int)at; double t = at - a;
            float x0 = source[Math.Min(a, source.Length - 1)], x1 = source[Math.Min(a + 1, source.Length - 1)];
            output[i] = (float)(x0 + (x1 - x0) * t);
        }
        return output;
    }

    public readonly struct Gate
    {
        public readonly float VoicedSeconds, PeakDb, FloorDb;
        public readonly int Start, End; // speech span in samples, with a margin
        public Gate(float voiced, float peak, float floor, int start, int end) { VoicedSeconds = voiced; PeakDb = peak; FloorDb = floor; Start = start; End = end; }
        public bool HasSpeech => VoicedSeconds >= .3f && PeakDb > -45 && End > Start;
    }

    // 20 ms frames above max(floor + 10 dB, -50 dBFS) count as voiced; the floor is the quietest fifth of the clip,
    // which includes the pre-roll before the key went down.
    public static Gate Analyze(float[] pcm)
    {
        if (pcm == null || pcm.Length < Frame) return new Gate(0, -120, -120, 0, 0);
        int frames = pcm.Length / Frame;
        var db = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * Frame; i < (f + 1) * Frame; i++) sum += pcm[i] * pcm[i];
            db[f] = (float)(10 * Math.Log10(sum / Frame + 1e-12));
        }
        var sorted = (float[])db.Clone(); Array.Sort(sorted);
        float floor = sorted[Math.Max(0, frames / 5 - 1)], peak = sorted[frames - 1];
        float threshold = Math.Max(floor + 10, -50);
        int voiced = 0, first = -1, last = -1;
        for (int f = 0; f < frames; f++)
            if (db[f] > threshold) { voiced++; if (first < 0) first = f; last = f; }
        if (first < 0) return new Gate(0, peak, floor, 0, 0);
        int margin = 8; // 160 ms either side keeps soft onsets and endings
        int start = Math.Max(0, first - margin) * Frame, end = Math.Min(pcm.Length, (last + 1 + margin) * Frame);
        return new Gate(voiced * Frame / (float)Rate, peak, floor, start, end);
    }

    public static float[] Slice(float[] pcm, int start, int end)
    {
        start = Math.Max(0, start); end = Math.Min(pcm.Length, end);
        var slice = new float[Math.Max(0, end - start)];
        Array.Copy(pcm, start, slice, 0, slice.Length);
        return slice;
    }

    public static byte[] Wav(float[] pcm, int rate = Rate)
    {
        using (var stream = new MemoryStream(44 + pcm.Length * 2))
        using (var w = new BinaryWriter(stream))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length * 2);
            foreach (float s in pcm) w.Write((short)Math.Round(Math.Max(-1f, Math.Min(1f, s)) * 32767));
            w.Flush();
            return stream.ToArray();
        }
    }

    private static readonly Regex Tags = new Regex(@"\[[^\]]*\]|\([^)]*\)|[♪♫]+|\*[^*]*\*", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);
    // What Whisper says over silence or breath. Only dropped when the clip was quiet, so a spoken "Bye." still counts.
    private static readonly HashSet<string> Hallucinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "you", "thank you", "thanks", "thank you for watching", "thanks for watching", "bye", "i", "so", "okay", "uh", "um", "hmm", "." };

    // The words to use, or null when nothing was said.
    public static string Clean(string transcript, bool quiet)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        string text = Spaces.Replace(Tags.Replace(transcript, " "), " ").Trim().Trim('"', '\'', '-', ' ');
        if (text.Length == 0 || !Regex.IsMatch(text, @"\p{L}")) return null;
        if (quiet && Hallucinations.Contains(text.TrimEnd('.', '!', '?', ','))) return null;
        return text;
    }
}
