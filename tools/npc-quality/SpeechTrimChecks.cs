// Headless checks for GatekeeperSpeechTrim on synthetic Phonon-like utterances: voiced bursts with quiet word endings,
// near-digital silence (Phonon's floor is about -66 dB), padded edges, long pauses and a frame-capped interjection.
// Bounds are two-sided, and kept audio is compared sample by sample through Result.kept, so trimming too much fails too.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

public static class SpeechTrimChecks
{
    private const int Rate = 24000;

    public static object Run(JsonNode request, GatekeeperScenario scenario, GatekeeperStory.Document story)
    {
        var failures = new List<string>();
        int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) failures.Add(what); }
        double Ms(byte[] pcm) => pcm.Length / 2 * 1000.0 / Rate;

        // 150 ms pad; a word whose 160 ms ending is held 45 dB under it (inside the threshold); a 900 ms pause whose
        // floor is a tone 53 dB under the word (just below the threshold, so a cut without ramps would click); a second
        // word ten dB quieter whose ending decays 60 dB per 100 ms through the threshold; then 800 ms of floor.
        var synth = new Synth(1).Silence(150).Word(600, 160).Hum(900, -53);
        int decayStart = synth.Count + Rate * 500 / 1000;
        var line = synth.Word(500, 160, levelDb: -10, decayDbPer100Ms: 60).Silence(800).Bytes();
        var trimmed = GatekeeperSpeechTrim.Measure(line, Rate);
        var plan = Regions(trimmed.pcm);
        Check(Lead(trimmed.pcm) >= 15 && Lead(trimmed.pcm) <= 35, "lead silence kept at 25 ms: " + Lead(trimmed.pcm));
        Check(Tail(trimmed.pcm) >= 85 && Tail(trimmed.pcm) <= 115, "tail kept at 100 ms after the last sound above -50 dB: " + Tail(trimmed.pcm));
        Check(plan.Count == 2, "two voiced regions survive: " + plan.Count);
        Check(plan.Count == 2 && Math.Abs(plan[1].start - plan[0].end - 400) <= 20, "the 900 ms pause becomes 400 ms");
        Check(trimmed.pausesShortened == 1 && Math.Abs(trimmed.pausesRemovedMs - 500) <= 20, "one pause shortened by 500 ms: " + trimmed.pausesRemovedMs);
        Check(Math.Abs(Ms(line) - Ms(trimmed.pcm) - (trimmed.leadRemovedMs + trimmed.tailRemovedMs + trimmed.pausesRemovedMs)) <= 10,
            "reported removals add up to the real length change");
        Check(Kept(trimmed) == trimmed.pcm.Length / 2, "kept segments cover the output exactly");
        Check(Unchanged(line, trimmed, Rate * 150 / 1000, Rate * (150 + 760) / 1000, out string held), "the first word and its -45 dB held ending survive unchanged" + held);
        // Every decaying sample still above -56 dB of the loudest word (Phonon's floor is -66 dB) plays at full gain.
        int audible = decayStart + (int)(Rate * (56 - 10) / 600.0);
        Check(Unchanged(line, trimmed, decayStart, audible, out string decayed), "the decaying ending survives to -56 dB at full gain" + decayed);
        Check(Map(trimmed, audible) >= 0 && Map(trimmed, audible) < trimmed.pcm.Length / 2 - Rate * GatekeeperSpeechTrim.FadeOutMs / 1000,
            "the fade-out starts after the last audible sample");
        Check(Joins(trimmed, out string joins), "each cut lands in silence and ramps to zero on both sides" + joins);

        // A short pause stays exactly as rendered; only the edges move.
        var natural = new Synth(2).Silence(150).Word(500, 60).Silence(250).Word(400, 60).Silence(700).Bytes();
        var kept = GatekeeperSpeechTrim.Measure(natural, Rate);
        var naturalRegions = Regions(kept.pcm);
        Check(kept.pausesShortened == 0 && naturalRegions.Count == 2 && Math.Abs(naturalRegions[1].start - naturalRegions[0].end - 250) <= 20, "a 250 ms pause is untouched");
        Check(Math.Abs(Ms(kept.pcm) - (25 + 560 + 250 + 460 + 100)) <= 20, "short-pause line keeps 25 ms + speech + pause + 100 ms: " + Ms(kept.pcm));

        // "Hmph." at the frame cap: 300 ms of sound, then 3 s of near-silence.
        var capped = new Synth(3).Silence(120).Word(300, 40).Silence(3000).Bytes();
        double cappedMs = Ms(GatekeeperSpeechTrim.Tighten(capped, Rate));
        Check(Math.Abs(cappedMs - (25 + 340 + 100)) <= 15, "a frame-capped interjection keeps 25 ms + itself + 100 ms: " + cappedMs + " ms");

        // Untouched inputs: silence, too short, odd byte count; and a second pass is (nearly) a no-op.
        var silence = new Synth(4).Silence(500).Bytes();
        Check(ReferenceEquals(GatekeeperSpeechTrim.Tighten(silence, Rate), silence), "silence is returned as-is");
        var tiny = new byte[20];
        Check(ReferenceEquals(GatekeeperSpeechTrim.Tighten(tiny, Rate), tiny), "a tiny buffer is returned as-is");
        var odd = new byte[4801];
        Check(ReferenceEquals(GatekeeperSpeechTrim.Tighten(odd, Rate), odd), "an odd byte count is returned as-is");
        var tight = GatekeeperSpeechTrim.Tighten(trimmed.pcm, Rate);
        Check(Math.Abs(tight.Length - trimmed.pcm.Length) <= 2 * 2 * Rate / 100, "a second pass changes at most two windows: " + (trimmed.pcm.Length - tight.Length));
        Check(Abs(trimmed.pcm, 0) == 0 && Abs(trimmed.pcm, trimmed.pcm.Length / 2 - 1) <= 1, "faded to zero at both ends");

        return new { passed = failures.Count == 0, checks = checks + " checks", failed = failures };
    }

    // Phonon-like voiced audio: a 120 Hz harmonic tone with a 10 ms attack; the ending is either held 45 dB under the
    // word then dropped to the floor, or decays exponentially. The floor is +-3 LSB of noise.
    private sealed class Synth
    {
        private readonly List<short> samples = new List<short>();
        private uint state;
        public int Count => samples.Count;
        public Synth(uint seed) { state = 2463534242u ^ seed; }
        private double Noise() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state / (double)uint.MaxValue - .5) * 6; }
        private double Tone(int index) { double t = index / (double)Rate; return .5 * Math.Sin(2 * Math.PI * 120 * t) + .3 * Math.Sin(2 * Math.PI * 240 * t) + .2 * Math.Sin(2 * Math.PI * 360 * t); }
        public Synth Silence(int ms) { for (int i = 0; i < Rate * ms / 1000; i++) samples.Add((short)Math.Round(Noise())); return this; }
        // A floor tone `db` under a full-level word: below the -50 dB threshold, but not digital silence.
        public Synth Hum(int ms, double db) { for (int i = 0; i < Rate * ms / 1000; i++) samples.Add((short)Math.Round(Tone(samples.Count) * 6500 * Math.Pow(10, db / 20) + Noise())); return this; }
        public Synth Word(int ms, int endingMs, double levelDb = 0, double decayDbPer100Ms = 0)
        {
            int body = Rate * ms / 1000, ending = Rate * endingMs / 1000, attack = Rate / 100;
            double gain = Math.Pow(10, levelDb / 20);
            for (int i = 0; i < body + ending; i++)
            {
                double level = i < attack ? i / (double)attack : i < body ? 1
                    : decayDbPer100Ms > 0 ? Math.Pow(10, -decayDbPer100Ms * (i - body) / (Rate / 10.0) / 20) : Math.Pow(10, -45 / 20.0);
                samples.Add((short)Math.Round(Tone(samples.Count) * 6500 * gain * level + Noise()));
            }
            return this;
        }
        public byte[] Bytes()
        {
            var bytes = new byte[samples.Count * 2];
            for (int i = 0; i < samples.Count; i++) { bytes[2 * i] = (byte)samples[i]; bytes[2 * i + 1] = (byte)(samples[i] >> 8); }
            return bytes;
        }
    }

    private static short At(byte[] pcm, int i) => (short)(pcm[2 * i] | pcm[2 * i + 1] << 8);
    private static int Abs(byte[] pcm, int i) => Math.Abs((int)At(pcm, i));
    private static int Kept(GatekeeperSpeechTrim.Result r) { int n = 0; foreach (var (from, to) in r.kept) n += to - from; return n; }

    // Output index of source sample `source`, or -1 when it was cut.
    private static int Map(GatekeeperSpeechTrim.Result r, int source)
    {
        int at = 0;
        foreach (var (from, to) in r.kept) { if (source >= from && source < to) return at + source - from; at += to - from; }
        return -1;
    }

    // Source samples [from, to) appear in the output untouched (gain exactly 1).
    private static bool Unchanged(byte[] source, GatekeeperSpeechTrim.Result r, int from, int to, out string detail)
    {
        detail = "";
        for (int s = from; s < to; s++)
        {
            int o = Map(r, s);
            if (o < 0 || At(r.pcm, o) != At(source, s)) { detail = ": sample " + s + (o < 0 ? " was cut" : " changed"); return false; }
        }
        return true;
    }

    // Each join between kept segments is zero on both sides, and nothing within JoinMs of it jumps more than the floor.
    private static bool Joins(GatekeeperSpeechTrim.Result r, out string detail)
    {
        detail = "";
        int at = 0, ramp = (int)(Rate * GatekeeperSpeechTrim.JoinMs / 1000);
        for (int k = 0; k < r.kept.Count; k++)
        {
            if (k > 0)
            {
                if (At(r.pcm, at - 1) != 0 || At(r.pcm, at) != 0) { detail = ": join " + k + " is not ramped to zero"; return false; }
                for (int i = Math.Max(1, at - ramp); i < Math.Min(r.pcm.Length / 2, at + ramp); i++)
                    if (Math.Abs(At(r.pcm, i) - At(r.pcm, i - 1)) > 40) { detail = ": a step of " + Math.Abs(At(r.pcm, i) - At(r.pcm, i - 1)) + " at join " + k; return false; }
            }
            at += r.kept[k].to - r.kept[k].from;
        }
        if (r.kept.Count < 2) detail = ": no join to check";
        return r.kept.Count >= 2;
    }

    // Voiced regions as 10 ms windows above the floor (|sample| > 20), in milliseconds.
    private static List<(int start, int end)> Regions(byte[] pcm)
    {
        int win = Rate / 100, windows = pcm.Length / 2 / win;
        var regions = new List<(int start, int end)>();
        int open = -1;
        for (int w = 0; w <= windows; w++)
        {
            bool voiced = false;
            if (w < windows) for (int i = w * win; i < (w + 1) * win && !voiced; i++) voiced = Math.Abs((int)At(pcm, i)) > 20;
            if (voiced && open < 0) open = w;
            if (!voiced && open >= 0) { regions.Add((open * 10, w * 10)); open = -1; }
        }
        return regions;
    }
    private static int Lead(byte[] pcm) { var r = Regions(pcm); return r.Count == 0 ? -1 : r[0].start; }
    // Tail after the last sound above the trimmer's own -50 dB mark, measured on the output.
    private static int Tail(byte[] pcm)
    {
        int win = Rate / 100, windows = pcm.Length / 2 / win;
        var rms = new double[windows]; double peak = 0;
        for (int w = 0; w < windows; w++) { double sum = 0; for (int i = w * win; i < (w + 1) * win; i++) sum += (double)At(pcm, i) * At(pcm, i); rms[w] = Math.Sqrt(sum / win); peak = Math.Max(peak, rms[w]); }
        double threshold = peak * Math.Pow(10, GatekeeperSpeechTrim.SilenceDb / 20);
        int last = -1;
        for (int w = 0; w < windows; w++) if (rms[w] > threshold) last = w;
        return last < 0 ? -1 : (int)Math.Round(pcm.Length / 2 * 1000.0 / Rate) - (last + 1) * 10;
    }
}
