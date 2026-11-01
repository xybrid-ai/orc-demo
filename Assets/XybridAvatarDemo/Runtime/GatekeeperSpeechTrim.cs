using System;
using System.Collections.Generic;

// Makes one synthesized utterance (PCM16 mono) speakable without dead air. Phonon pads every call with ~150 ms of
// silence in front and 0.6-1 s behind, and a bare "Hmph." runs to its frame cap; played clause by clause, each comma
// and full stop became a ~1 s gap. Silence is measured against the loudest 10 ms window: Phonon's floor sits near
// -66 dB and word endings decay through -40 dB, so -50 dB plus a tail margin and a fade never clips a word. Long
// internal pauses keep MaxPauseMs, split around the cut. Pure; tools/npc-quality/SpeechTrimChecks.cs covers it.
public static class GatekeeperSpeechTrim
{
    public const float SilenceDb = -50, LeadMs = 25, TailMs = 100, MaxPauseMs = 400, FadeInMs = 4, FadeOutMs = 30, JoinMs = 8;

    public sealed class Result
    {
        public byte[] pcm;
        public double leadRemovedMs, tailRemovedMs, pausesRemovedMs;
        public int pausesShortened;
        // Source sample ranges kept, in order; empty when the input is returned untouched.
        public readonly List<(int from, int to)> kept = new List<(int from, int to)>();
    }

    public static byte[] Tighten(byte[] pcm, int sampleRate) => Measure(pcm, sampleRate).pcm;

    // Returns the input untouched when it is not mono PCM16, is too short, or has nothing to anchor a threshold on.
    public static Result Measure(byte[] pcm, int sampleRate)
    {
        var result = new Result { pcm = pcm };
        int win = sampleRate / 100;
        if (pcm == null || pcm.Length % 2 != 0 || sampleRate < 8000 || pcm.Length / 2 < win * 3) return result;
        int count = pcm.Length / 2, windows = count / win;
        var rms = new double[windows];
        double peak = 0;
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            for (int i = w * win, end = i + win; i < end; i++) { double s = Sample(pcm, i); sum += s * s; }
            rms[w] = Math.Sqrt(sum / win);
            peak = Math.Max(peak, rms[w]);
        }
        if (peak < 64) return result;
        double threshold = Math.Max(8, peak * Math.Pow(10, SilenceDb / 20));
        int first = -1, last = -1;
        for (int w = 0; w < windows; w++) if (rms[w] > threshold) { if (first < 0) first = w; last = w; }
        if (first < 0) return result;

        int start = Math.Max(0, first * win - Samples(LeadMs, sampleRate));
        int stop = Math.Min(count, (last + 1) * win + Samples(TailMs, sampleRate));
        int maxPause = Math.Max(2, Samples(MaxPauseMs, sampleRate) / win);
        var keep = new List<(int from, int to)>();
        int cursor = start;
        for (int w = first; w <= last;)
        {
            int runStart = w;
            while (runStart <= last && rms[runStart] > threshold) runStart++;
            if (runStart > last) break;
            int runEnd = runStart;
            while (runEnd <= last && rms[runEnd] <= threshold) runEnd++;
            if (runEnd - runStart > maxPause)
            {
                int before = maxPause / 2;
                keep.Add((cursor, (runStart + before) * win));
                cursor = (runEnd - (maxPause - before)) * win;
                result.pausesShortened++;
                result.pausesRemovedMs += (runEnd - runStart - maxPause) * 10.0;
            }
            w = runEnd;
        }
        keep.Add((cursor, stop));
        result.leadRemovedMs = start * 1000.0 / sampleRate;
        result.tailRemovedMs = (count - stop) * 1000.0 / sampleRate;
        if (start == 0 && stop == count && keep.Count == 1) return result;
        result.kept.AddRange(keep);

        int length = 0;
        foreach (var (from, to) in keep) length += to - from;
        var output = new float[length];
        int at = 0, join = Samples(JoinMs, sampleRate);
        for (int k = 0; k < keep.Count; k++)
        {
            var (from, to) = keep[k];
            int span = to - from;
            for (int i = 0; i < span; i++)
            {
                float gain = 1;
                // Cuts fall inside silence; short ramps on both sides keep the residual floor from clicking.
                if (k > 0 && i < join) gain = Math.Min(gain, i / (float)join);
                if (k < keep.Count - 1 && span - i <= join) gain = Math.Min(gain, (span - i - 1) / (float)join);
                output[at + i] = (float)Sample(pcm, from + i) * gain;
            }
            at += span;
        }
        Ramp(output, Samples(FadeInMs, sampleRate), Samples(FadeOutMs, sampleRate));
        var bytes = new byte[length * 2];
        for (int i = 0; i < length; i++)
        {
            short value = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(output[i])));
            bytes[2 * i] = (byte)value; bytes[2 * i + 1] = (byte)(value >> 8);
        }
        result.pcm = bytes;
        return result;
    }

    private static void Ramp(float[] samples, int fadeIn, int fadeOut)
    {
        fadeIn = Math.Min(fadeIn, samples.Length / 2); fadeOut = Math.Min(fadeOut, samples.Length / 2);
        for (int i = 0; i < fadeIn; i++) samples[i] *= i / (float)fadeIn;
        for (int i = 0; i < fadeOut; i++) samples[samples.Length - 1 - i] *= i / (float)fadeOut;
    }

    private static short Sample(byte[] pcm, int index) => (short)(pcm[2 * index] | pcm[2 * index + 1] << 8);
    private static int Samples(float ms, int sampleRate) => (int)(sampleRate * ms / 1000);
}
