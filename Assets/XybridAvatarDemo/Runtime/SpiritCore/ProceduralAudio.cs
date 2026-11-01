// Deterministic procedural audio for the camp soundscape and the spirit's sound effects: pure C# 9.
// Every buffer is mono float within [-1, 1]; the same seed always gives the same samples. Loops are seamless:
// noise layers crossfade (equal power) the stream's continuation into their head, so the wrap is an ordinary step
// of one continuous signal; tonal layers are exactly periodic (each frequency is a whole number of cycles per loop).
// Their slow modulations are periodic in the loop as well, so the crossfade never blends two different swells.
using System;

public sealed class XorShift32
{
    private uint state;
    public XorShift32(uint seed)
    {
        // Murmur3 finalizer: neighbouring seeds start far apart, and the xorshift state must never be 0.
        unchecked { uint x = seed + 0x9E3779B9u; x ^= x >> 16; x *= 0x85EBCA6Bu; x ^= x >> 13; x *= 0xC2B2AE35u; x ^= x >> 16; state = x != 0 ? x : 0x6D2B79F5u; }
    }
    public uint Next() { uint x = state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; return state = x; }
    public float NextFloat() => (Next() >> 8) * (1f / 16777216f);                       // [0, 1)
    public float Range(float min, float max) => min + (max - min) * NextFloat();
    public int Range(int min, int maxExclusive) => min + (int)(NextFloat() * (maxExclusive - min));
    public float Signed() => NextFloat() * 2 - 1;
    public double Exponential(double rate) => -Math.Log(1 - NextFloat()) / rate;          // Poisson gaps; 1 - [0,1) is never 0
}

// Mix rules (music and mixing) shared by CampSoundscape and the headless previews in SpiritAudioChecks.
public static class CampMix
{
    public const float Hearth = .35f, Torch = .22f, Campfire = .30f, Wind = .10f, Crickets = .04f, Shimmer = .10f, Resolve = .06f;
    // Drum: .35 re-levelled for the DrumHit "skin" so each hit's full-band level is unchanged. Laughter is low-passed and
    // at the tents: Laugh sits a few dB under camp life, the Liked BigLaugh (3 voices) about level with a hammer.
    public const float Drum = .32f, Laugh = .18f, BigLaugh = .4f, Gate = .8f;
    public const float SpeakingAmbience = .55f, SpeakingMusic = .45f, SpeakingFire = .8f;        // while Trola (or the spirit) speaks
    public const float FireCalm = .65f, FireRoar = 1.9f;                                           // TrolaAtmosphere.PatienceFireScale at p5 and p0
    public static float Tension(int patience) => 1 - Clamp01(patience / 5f);
    public static float FireScale(int patience) => FireRoar + (FireCalm - FireRoar) * Clamp01(patience / 5f);
    public static float RoarMix(float fire) => Clamp01((fire - FireCalm) / (FireRoar - FireCalm));  // 0 = calm hearth loop, 1 = roaring one
    public static float CricketLevel(int patience, bool success) => success || patience >= 3 ? Crickets : patience == 2 ? Crickets / 2 : 0;
    public static float DroneGain(float tension) => .07f + .05f * tension;
    public static float MusicCutoff(float moment) => Lerp(22000, 500, Curve(moment));                // music sits behind LP 500 Hz in a moment
    public static float DroneCutoff(float tension, float moment) => Math.Min(400 + 900 * tension, MusicCutoff(moment));
    public static float WorldCutoff(float moment) => Lerp(22000, 900, Curve(moment));
    public static float WorldGain(float moment) => Lerp(1, .6f, Clamp01(moment));
    public static float MusicGain(float moment) => Lerp(1, .5f, Clamp01(moment));
    public static float Bpm(float tension) => 54 + 36 * tension;
    // T eases toward Tension(patience) over about 2 s (CampSoundscape calls this every frame; dt is capped at .1 s).
    public static float SmoothTension(float tension, int patience, float dt) => Lerp(tension, Tension(patience), 1 - (float)Math.Exp(-dt / .7f));
    // Pattern boundaries sit midway between patience steps (p4 .2 | p3 .4, p2 .6 | p1 .8): the smoothed T stalls a few
    // float steps short of its target, from either side, so a boundary on a target would make the pattern depend on history.
    public const float DrumSilentBelow = .3f, DrumWarFrom = .7f;
    public static string DrumPattern(float tension) => tension < DrumSilentBelow ? "silent" : tension < DrumWarFrom ? "heartbeat" : "war";
    // Sixteen steps per bar of four beats. Silent at p>=4; a heartbeat pair every two beats at p3-p2;
    // at p<=1 the war pattern, eighths [0,2,3,4,6]. Returns the hit velocity, 0 for no hit.
    public static float DrumStep(float tension, int step)
    {
        step &= 15;
        float hit;
        if (tension < DrumSilentBelow) hit = 0;
        else if (tension < DrumWarFrom) hit = step == 0 || step == 8 ? 1 : step == 1 || step == 9 ? .6f : 0;
        else hit = step == 0 ? 1 : step == 4 ? .7f : step == 6 ? .8f : step == 8 ? .9f : step == 12 ? .75f : 0;
        return hit * (.5f + .5f * tension);
    }
    // Camp life every 3.5-9 s: hammer .3, clank .2, thud .2, clatter .15, laugh .15 (twice as likely at p>=4, never at p<=1).
    public static readonly (SpiritSound sound, float weight, float gain)[] Events =
        { (SpiritSound.Hammer, .3f, .5f), (SpiritSound.Clank, .2f, .4f), (SpiritSound.Thud, .2f, .55f), (SpiritSound.Clatter, .15f, .4f) };
    public static float LaughWeight(int patience) => patience >= 4 ? .3f : patience <= 1 ? 0 : .15f;
    private static float Curve(float moment) => (float)Math.Pow(Clamp01(moment), .6);
    private static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

public static class ProceduralAudio
{
    // "crickets.left"/".right" are the two crickets on their own (panned apart in the camp); "resolve" is the F#3 that
    // turns the drone major on a good ending. The other names are the camp loops CampSoundscape plays.
    public static readonly string[] Layers = { "hearth", "torch", "campfire", "wind", "crickets", "crickets.left", "crickets.right", "drone", "resolve", "shimmer" };
    public static readonly SpiritSound[] Sounds = (SpiritSound[])Enum.GetValues(typeof(SpiritSound));
    public const float CrossfadeSeconds = .5f;
    // The camp's loops: CampSoundscape renders exactly these, and SpiritAudioChecks checks and previews the same set.
    // 24 kHz (16 kHz for the dark wind and drone) halves the memory of 32 kHz; nothing here needs more. About 3.3 M samples.
    public sealed class LoopSpec { public string Name, Layer; public int Seed, Rate; public float Seconds, Fire = 1; }
    public static readonly LoopSpec[] CampLoops =
    {
        new LoopSpec { Name = "wind", Layer = "wind", Seed = 41, Rate = 16000, Seconds = 20 },
        new LoopSpec { Name = "hearth calm", Layer = "hearth", Seed = 11, Rate = 24000, Seconds = 14, Fire = .65f },
        new LoopSpec { Name = "hearth roar", Layer = "hearth", Seed = 12, Rate = 24000, Seconds = 14, Fire = 1.9f },
        new LoopSpec { Name = "torch west", Layer = "torch", Seed = 21, Rate = 24000, Seconds = 12 },
        new LoopSpec { Name = "torch east", Layer = "torch", Seed = 22, Rate = 24000, Seconds = 12.5f },
        new LoopSpec { Name = "campfire", Layer = "campfire", Seed = 31, Rate = 24000, Seconds = 16 },
        new LoopSpec { Name = "crickets left", Layer = "crickets.left", Seed = 51, Rate = 24000, Seconds = 12 },
        new LoopSpec { Name = "crickets right", Layer = "crickets.right", Seed = 52, Rate = 24000, Seconds = 13 },
        new LoopSpec { Name = "drone", Layer = "drone", Seed = 61, Rate = 16000, Seconds = 16 },
        new LoopSpec { Name = "resolve", Layer = "resolve", Seed = 62, Rate = 16000, Seconds = 16 },
        new LoopSpec { Name = "shimmer", Layer = "shimmer", Seed = 71, Rate = 24000, Seconds = 8 },
    };
    public const int CampOneShotRate = 24000;
    public static bool IsPeriodic(string layer) => layer is "crickets" or "crickets.left" or "crickets.right" or "drone" or "resolve" or "shimmer";
    public static bool UsesFire(string layer) => layer is "hearth" or "torch" or "campfire";

    public static float[] RenderLoop(string layer, int seed, int sampleRate, float seconds) => RenderLoop(layer, seed, sampleRate, seconds, 1);
    // fire: TrolaAtmosphere.PatienceFireScale (.65 calm .. 1.9 furious); more fire, more crackles and pops. Other layers ignore it.
    public static float[] RenderLoop(string layer, int seed, int sampleRate, float seconds, float fire)
    {
        int n = LoopLength(sampleRate, seconds), fade = IsPeriodic(layer) ? 0 : Math.Min(n / 4, (int)(CrossfadeSeconds * sampleRate));
        float[] stream = RenderStream(layer, seed, sampleRate, seconds, fire, fade), loop = new float[n];
        Array.Copy(stream, loop, n);
        // loop[0] == stream[n] and loop[n-1] == stream[n-1]. Each stream sample is soft-clipped to .7, so sin + cos <= sqrt 2 stays below 1.
        for (int i = 0; i < fade; i++)
        {
            double a = Math.PI / 2 * i / fade;
            loop[i] = (float)(stream[i] * Math.Sin(a) + stream[n + i] * Math.Cos(a));
        }
        return loop;
    }

    // The continuous signal a loop is cut from: the loop's n samples plus extraSamples of continuation. A longer request
    // never changes the earlier samples, so checks can compare a loop's head with the stream's sample n.
    public static float[] RenderStream(string layer, int seed, int sampleRate, float loopSeconds, float fire, int extraSamples)
    {
        int n = LoopLength(sampleRate, loopSeconds);
        if (extraSamples < 0 || extraSamples > n) throw new ArgumentOutOfRangeException(nameof(extraSamples));
        if (!(fire > 0)) fire = 1;
        fire = Math.Min(4, Math.Max(.1f, fire));
        switch (layer)
        {
            case "hearth": return Fire(Hearth, seed, layer, sampleRate, n, extraSamples, fire);
            case "torch": return Fire(Torch, seed, layer, sampleRate, n, extraSamples, fire);
            case "campfire": return Fire(Campfire, seed, layer, sampleRate, n, extraSamples, fire);
            case "wind": return Wind(seed, sampleRate, n, extraSamples);
            case "crickets": return Repeat(Crickets(seed, layer, sampleRate, n, true, true), n + extraSamples);
            case "crickets.left": return Repeat(Crickets(seed, layer, sampleRate, n, true, false), n + extraSamples);
            case "crickets.right": return Repeat(Crickets(seed, layer, sampleRate, n, false, true), n + extraSamples);
            case "drone": return Drone(seed, layer, sampleRate, n, extraSamples, new[] { (73.416, 1f), (110.0, .75f) }, .25, 1800);
            case "resolve": return Drone(seed, layer, sampleRate, n, extraSamples, new[] { (184.997, 1f), (369.994, .3f) }, .18, 1400);
            case "shimmer": return ShimmerPad(seed, layer, sampleRate, n, extraSamples);
            default: throw new ArgumentException("Unknown procedural layer '" + layer + "'.", nameof(layer));
        }
    }

    // One-shots start and end at exactly 0, have no DC offset and peak at .9; the caller sets the level.
    public static float[] Render(SpiritSound sound, int seed, int sampleRate)
    {
        CheckRate(sampleRate);
        var rng = Rng(seed, sound.ToString(), 7);
        switch (sound)
        {
            case SpiritSound.BreathIn: return Finish(Breath(rng, sampleRate, true), sampleRate);
            case SpiritSound.Exhale: return Finish(Breath(rng, sampleRate, false), sampleRate);
            case SpiritSound.Gasp: return Finish(Gasp(rng, sampleRate), sampleRate);
            case SpiritSound.Shimmer: return Finish(ShimmerHit(rng, sampleRate), sampleRate);
            case SpiritSound.Clank: { var b = new float[Samples(1.0, sampleRate)]; Clank(b, 0, rng.Range(420f, 640f), 1, 1, sampleRate, rng); return Finish(b, sampleRate); }
            case SpiritSound.Hammer: return Finish(Hammer(rng, sampleRate), sampleRate);
            case SpiritSound.Thud: { var b = new float[Samples(.6, sampleRate)]; Thud(b, 0, 70, 45, .08, .14, 1, sampleRate, rng); return Finish(b, sampleRate); }
            case SpiritSound.Clatter: return Finish(Clatter(rng, sampleRate), sampleRate);
            case SpiritSound.GateCreak: return Finish(GateCreak(rng, sampleRate), sampleRate);
            case SpiritSound.DrumHit: return Finish(DrumHit(rng, sampleRate), sampleRate);
            default: throw new ArgumentException("Unknown sound " + sound + ".", nameof(sound));
        }
    }

    public static int LoopLength(int sampleRate, float seconds)
    {
        CheckRate(sampleRate);
        if (!(seconds >= .25f && seconds <= 120)) throw new ArgumentOutOfRangeException(nameof(seconds), "Loops are .25 to 120 s.");
        return (int)Math.Round(seconds * (double)sampleRate);
    }
    private static void CheckRate(int sampleRate) { if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate)); }
    private static int Samples(double seconds, int sampleRate) => Math.Max(1, (int)(seconds * sampleRate));

    // ---- Fire: brown-noise bed + Poisson crackles (1-6 ms bursts of differenced noise, often in small clusters) + pops.
    private sealed class FireSpec { public float BedLowpass, BedGain, Breath, Flutter, Crackles, CrackleGain, CrackleLowpass, Pops, PopGain, Hiss; public bool Dark; }
    private static readonly FireSpec Hearth = new FireSpec { BedLowpass = 600, BedGain = .30f, Breath = .25f, Crackles = 7, CrackleGain = .55f, CrackleLowpass = 9000, Pops = .3f, PopGain = .5f, Hiss = .012f };
    private static readonly FireSpec Torch = new FireSpec { BedLowpass = 900, BedGain = .26f, Breath = .15f, Flutter = .45f, Crackles = 4, CrackleGain = .45f, CrackleLowpass = 7000, Pops = .12f, PopGain = .35f, Hiss = .008f };
    private static readonly FireSpec Campfire = new FireSpec { BedLowpass = 400, BedGain = .28f, Breath = .3f, Crackles = 7, CrackleGain = .6f, CrackleLowpass = 2500, Pops = .3f, PopGain = .55f, Dark = true };

    private static float[] Fire(FireSpec spec, int seed, string layer, int sr, int n, int extra, float fire)
    {
        int pre = sr / 2, total = pre + n + extra;
        var crackles = new float[total];
        // Crackle timing and grain noise use their own generators, so a longer stream never shifts earlier events.
        var events = Rng(seed, layer, 2); var grain = Rng(seed, layer, 3);
        double rate = spec.Crackles * Math.Pow(fire, 1.3) / sr;
        for (double t = events.Exponential(rate); t < total; t += events.Exponential(rate))
        {
            float amp = spec.CrackleGain * (float)Math.Exp(events.Range(-2.5f, 0)), seconds = events.Range(.001f, .006f);
            int at = (int)t;
            Crackle(crackles, at, amp, seconds, sr, grain);
            if (events.NextFloat() < .35f)
                for (int count = events.Range(1, 4), k = 0; k < count; k++)
                {
                    at += (int)(events.Range(.003f, .025f) * sr);
                    Crackle(crackles, at, amp * events.Range(.4f, .9f), seconds * events.Range(.5f, 1), sr, grain);
                }
        }
        var pops = Rng(seed, layer, 4);
        double popRate = spec.Pops * fire / sr;
        for (double t = pops.Exponential(popRate); t < total; t += pops.Exponential(popRate))
            Pop(crackles, (int)t, spec.PopGain * pops.Range(.35f, .8f), pops.Range(180f, 420f), pops.Range(.012f, .035f), pops.NextFloat() < .5f ? -1 : 1, sr);

        var noise = Rng(seed, layer, 1); var hissNoise = Rng(seed, layer, 6);
        double leak = Math.Exp(-2 * Math.PI * 20 / sr);
        float keep = (float)leak, push = (float)Math.Sqrt(1 - leak * leak), brown = 0;
        var bedLow = OnePole.At(spec.BedLowpass, sr); var dc = OnePole.At(25, sr); var hissLow = OnePole.At(3000, sr);
        var crackleLow = OnePole.At(spec.CrackleLowpass, sr); var crackleLow2 = OnePole.At(spec.CrackleLowpass, sr);
        bool filterCrackles = spec.CrackleLowpass < .45f * sr;
        var breath = new Lfo(Rng(seed, layer, 5), n, sr, new[] { .23, .41, .67 }, new[] { 1.0, .6, .4 }, -pre);
        var flutter = spec.Flutter > 0 ? new Lfo(Rng(seed, layer, 8), n, sr, new[] { 6.3, 8.9, 11.7, 14.2 }, new[] { 1.0, .8, .6, .4 }, -pre) : null;
        var output = new float[n + extra];
        for (int i = 0; i < total; i++)
        {
            brown = keep * brown + push * noise.Signed();
            float bed = bedLow.Low(brown); bed -= dc.Low(bed);
            float env = 1 + spec.Breath * breath.Next();
            if (flutter != null) env *= Math.Max(0, 1 + spec.Flutter * flutter.Next());
            float c = crackles[i];
            if (filterCrackles) { c = crackleLow.Low(c); if (spec.Dark) c = crackleLow2.Low(c) * 1.6f; }
            float h = hissNoise.Signed(); h -= hissLow.Low(h);
            if (i >= pre) output[i - pre] = SoftClip(bed * env * spec.BedGain + c + h * spec.Hiss * env);
        }
        return output;
    }
    private static void Crackle(float[] into, int at, float amp, float seconds, int sr, XorShift32 grain)
    {
        double tau = seconds / 2.5 * sr;
        int length = (int)(tau * 5) + 1;
        float decay = (float)Math.Exp(-1 / tau), env = amp, previous = 0;
        // Draws every grain sample even past the end, so truncation never changes later events.
        for (int j = 0; j < length; j++, env *= decay)
        {
            float w = grain.Signed(), v = (w - previous) * .5f * env; previous = w;
            int i = at + j; if (i >= 0 && i < into.Length) into[i] += v;
        }
    }
    private static void Pop(float[] into, int at, float amp, float hz, float tau, int sign, int sr)
    {
        if (at >= 0 && at < into.Length) into[at] += .6f * amp * sign;
        double w = 2 * Math.PI * Math.Min(hz, .45 * sr) / sr, d = Math.Exp(-1 / (tau * sr)), c = Math.Cos(w) * d, s = Math.Sin(w) * d, re = sign * amp, im = 0;
        int length = (int)(tau * sr * 5);
        for (int j = 1; j < length && at + j < into.Length; j++)
        {
            double r = re * c - im * s; im = re * s + im * c; re = r;
            if (at + j >= 0) into[at + j] += (float)im;
        }
    }

    // ---- Night wind: pink noise, low-passed at up to 1.2 kHz, swelling in slow gusts with a faint whistle on top.
    private static float[] Wind(int seed, int sr, int n, int extra)
    {
        int pre = sr / 2, total = pre + n + extra;
        var noise = Rng(seed, "wind", 1);
        var gust = new Lfo(Rng(seed, "wind", 2), n, sr, new[] { .05, .1, .15, .25 }, new[] { .8, 1.0, .5, .3 }, -pre);
        float b0 = 0, b1 = 0, b2 = 0;
        var lowA = new OnePole(); var lowB = new OnePole(); var dc = OnePole.At(30, sr); var whistle = new Svf();
        var output = new float[n + extra];
        for (int i = 0; i < total; i++)
        {
            float g = .5f + .5f * gust.Next(); g = g * g * (3 - 2 * g);
            if ((i & 15) == 0) { float cut = 650 + 550 * g; lowA.Set(cut, sr); lowB.Set(cut, sr); whistle.Set(380 + 300 * g, 9, sr); }
            float w = noise.Signed();
            b0 = .99765f * b0 + w * .0990460f; b1 = .96300f * b1 + w * .2965164f; b2 = .57000f * b2 + w * 1.0526913f;
            float pink = b0 + b1 + b2 + w * .1848f;
            float v = lowB.Low(lowA.Low(pink)) * (.3f + .7f * g * (float)Math.Sqrt(g)) + whistle.Band(pink) * .12f * g * g;
            v -= dc.Low(v);
            if (i >= pre) output[i - pre] = SoftClip(v * .18f);
        }
        return output;
    }

    // ---- Crickets: chirps of 3-4 short 4.6/4.9 kHz pulses, written modulo the loop so the loop is periodic by construction.
    private static float[] Crickets(int seed, string layer, int sr, int n, bool left, bool right)
    {
        var loop = new float[n];
        if (left) Cricket(loop, Rng(seed, layer, 1), sr, 4600, .62f, 1);
        if (right) Cricket(loop, Rng(seed, layer, 2), sr, 4900, .71f, left ? .7f : 1);
        return Normalize(loop, n, .6f);
    }
    private static void Cricket(float[] loop, XorShift32 rng, int sr, double carrier, float period, float level)
    {
        carrier = Math.Min(carrier, .45 * sr);
        double seconds = loop.Length / (double)sr;
        int pulse = Samples(.017, sr);
        for (double t = rng.Range(0, period); t < seconds; t += period * rng.Range(.92f, 1.08f))
        {
            if (rng.NextFloat() < .1f) { t += period * rng.Range(.5f, 2f); continue; }   // a rest now and then
            int syllables = 3 + (rng.NextFloat() < .35f ? 1 : 0) - (rng.NextFloat() < .12f ? 1 : 0);
            float amp = level * rng.Range(.7f, 1);
            double gap = .042 * rng.Range(.95f, 1.05f);
            for (int s = 0; s < syllables; s++)
                Pulse(loop, (int)((t + s * gap) * sr), pulse, carrier * rng.Range(.996f, 1.004f), amp * (s == 0 ? .75f : 1), sr);
        }
    }
    private static void Pulse(float[] loop, int at, int length, double hz, float amp, int sr)
    {
        bool harmonic = 2 * hz < .45 * sr;
        for (int j = 0; j < length; j++)
        {
            double x = j / (double)length, env = Math.Sin(Math.PI * x), phase = 2 * Math.PI * hz * j / sr * (1 - .0075 * x);
            double v = Math.Sin(phase) + (harmonic ? .12 * Math.Sin(2 * phase) : 0);
            loop[(at + j) % loop.Length] += (float)(amp * env * env * v);
        }
    }

    // ---- Drone: detuned pairs (slow beating) of soft band-limited saws; the camp applies the tension low-pass at runtime.
    private static float[] Drone(int seed, string layer, int sr, int n, int extra, (double hz, float level)[] notes, double beat, double topHz)
    {
        double seconds = n / (double)sr;
        var rng = Rng(seed, layer, 1);
        var output = new float[n + extra];
        foreach (var (hz, level) in notes)
            for (int twin = -1; twin <= 1; twin += 2)
            {
                double f = Quantize(hz + twin * beat / 2, seconds), start = rng.NextFloat();
                int harmonics = Math.Max(1, (int)(Math.Min(topHz, .45 * sr) / f));
                var table = Wavetable(harmonics, 1.15, rng);
                for (int i = 0; i < output.Length; i++)
                {
                    double p = start + f * i / sr;
                    output[i] += level * Lookup(table, p - Math.Floor(p));
                }
            }
        var swell = new Lfo(rng, n, sr, new[] { 1 / seconds, 2 / seconds }, new[] { 1.0, .5 }, 0);
        for (int i = 0; i < output.Length; i++) output[i] *= 1 + .12f * swell.Next();
        return Normalize(output, n, .6f);
    }

    // ---- Shimmer pad: D5/A5 (and a faint D6) with a 5 Hz vibrato, for the mind moments.
    private static float[] ShimmerPad(int seed, string layer, int sr, int n, int extra)
    {
        double seconds = n / (double)sr, vibrato = Quantize(5, seconds);
        var rng = Rng(seed, layer, 1);
        var output = new float[n + extra];
        foreach (var (hz, level) in new[] { (587.33, 1.0), (880.0, .8), (1174.66, .3) })
        {
            double f = Quantize(Math.Min(hz, .4 * sr), seconds), beta = .004 * f / vibrato, phase = 2 * Math.PI * rng.NextFloat(), vphase = 2 * Math.PI * rng.NextFloat();
            var swell = new Lfo(rng, n, sr, new[] { 1 / seconds, 2 / seconds, 3 / seconds }, new[] { 1.0, .6, .3 }, 0);
            for (int i = 0; i < output.Length; i++)
            {
                double t = i / (double)sr, a = 2 * Math.PI * f * t + beta * Math.Sin(2 * Math.PI * vibrato * t + vphase) + phase;
                output[i] += (float)(level * (1 + .35 * swell.Next()) * (Math.Sin(a) + .15 * Math.Sin(2 * a)));
            }
        }
        return Normalize(output, n, .6f);
    }

    // ---- Spirit one-shots: breaths are formant-filtered noise; the shimmer is a D major bell swell with sparkles.
    private static float[] Breath(XorShift32 rng, int sr, bool inhale)
    {
        int length = Samples(inhale ? .85 : 1.05, sr);
        var buffer = new float[length];
        var first = new Svf(); var second = new Svf(); var air = OnePole.At(inhale ? 3500 : 1200, sr); var air2 = OnePole.At(1200, sr);
        var turbulence = new Lfo(rng, length, sr, new[] { 13.0, 21.0, 34.0 }, new[] { 1.0, .7, .5 }, 0);
        for (int i = 0; i < length; i++)
        {
            double x = i / (double)length;
            if ((i & 31) == 0)
            {
                first.Set(inhale ? 900 + 450 * x : 650 - 170 * x, inhale ? 3.5f : 3, sr);
                second.Set(inhale ? 2300 + 600 * x : 1500 - 350 * x, inhale ? 5 : 4, sr);
            }
            double env = inhale ? (x < .85 ? Math.Pow(x / .85, 1.7) : Math.Cos((x - .85) / .15 * Math.PI / 2))
                : (x < .07 ? Math.Sin(x / .07 * Math.PI / 2) : Math.Pow(1 - (x - .07) / .93, 1.6));
            // Inhale: a little high air over the formants; exhale: warmer, a soft two-pole low-passed breath instead.
            float w = rng.Signed(), low = air.Low(w), hiss = inhale ? (w - low) * .12f : air2.Low(low) * 1.4f;
            buffer[i] = (float)(env * (1 + .25 * turbulence.Next()) * (.55 * first.Band(w) + .35 * second.Band(w) + hiss));
        }
        return buffer;
    }
    private static float[] Gasp(XorShift32 rng, int sr)
    {
        int length = Samples(.18, sr);
        var buffer = new float[length];
        var first = new Svf(); var second = new Svf(); var voice = OnePole.At(1200, sr);
        double glottis = 0;
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)sr, x = i / (double)length;
            if ((i & 31) == 0) { first.Set(1700 + 250 * x, 4, sr); second.Set(3100, 6, sr); }
            double env = t < .012 ? Math.Sin(t / .012 * Math.PI / 2) : t > .145 ? Math.Cos((t - .145) / .035 * Math.PI / 2) : 1 - .3 * x;
            glottis += (260 + 40 * x) / sr; glottis -= Math.Floor(glottis);
            float w = rng.Signed(), voiced = voice.Low((float)(glottis < .12 ? 1 : -.136));
            buffer[i] = (float)(Math.Max(0, env) * (.6 * first.Band(w) + .4 * second.Band(w) + .35 * voiced));
        }
        return buffer;
    }
    private static float[] ShimmerHit(XorShift32 rng, int sr)
    {
        int length = Samples(1.8, sr);
        var buffer = new float[length];
        double[] hz = { 587.33, 880, 1174.66, 1760 }, enter = { 0, .07, .14, .21 }, tau = { 1.0, .85, .7, .55 };
        for (int p = 0; p < hz.Length; p++)
            for (int twin = 0; twin < 2; twin++)
            {
                double f = Math.Min(hz[p] + twin * 1.2, .45 * sr), level = twin == 0 ? 1 : .5, phase = 2 * Math.PI * rng.NextFloat();
                for (int i = Samples(enter[p], sr) - 1; i < length; i++)
                {
                    double t = i / (double)sr - enter[p];
                    if (t < 0) continue;
                    double env = Math.Min(1, t / .025) * Math.Exp(-t / tau[p]);
                    buffer[i] += (float)(level * env * Math.Sin(2 * Math.PI * f * t + .004 * f / 5.5 * Math.Sin(2 * Math.PI * 5.5 * t) + phase) / (1 + p * .4));
                }
            }
        for (int k = 0; k < 14; k++)
        {
            double at = rng.Range(0, .9f), f = Math.Min(rng.Range(2600f, 5200f) * (1 + .4 * at), .45 * sr), decay = rng.Range(.02f, .045f), amp = rng.Range(.15f, .3f);
            for (int i = (int)(at * sr), j = 0; i < length && j < decay * 6 * sr; i++, j++)
                buffer[i] += (float)(amp * Math.Exp(-j / (decay * sr)) * Math.Sin(2 * Math.PI * f * j / sr));
        }
        var air = OnePole.At(4000, sr);
        for (int i = 0; i < length; i++)
        {
            float w = rng.Signed(); w -= air.Low(w);
            double t = i / (double)sr;
            if (t < 1.2) buffer[i] += (float)(.05 * Math.Sin(Math.PI * t / 1.2) * w);
        }
        return buffer;
    }

    // ---- Camp one-shots: an anvil clank (partials x1, 2.76, 5.4, 8.93), hammering, thuds, clatter, the gate and the war drum.
    private static void Clank(float[] into, int at, double f0, float amp, double decayScale, int sr, XorShift32 rng)
    {
        double[] ratio = { 1, 2.76, 5.4, 8.93 }, level = { 1, .55, .32, .18 }, tau = { .45, .28, .16, .09 };
        for (int p = 0; p < ratio.Length; p++)
            for (int twin = 0; twin < 2; twin++)
            {
                double f = f0 * ratio[p] * (twin == 0 ? 1 : 1.0015);
                if (f >= .45 * sr) continue;
                Resonate(into, at, f, tau[p] * decayScale, (float)(amp * level[p] * (twin == 0 ? 1 : .3)), 2 * Math.PI * rng.NextFloat(), sr);
            }
        float previous = 0;
        for (int j = 0; j < Samples(.003, sr) && at + j < into.Length; j++)
        {
            float w = rng.Signed(); into[at + j] += .5f * amp * (w - previous) * (1 - j / (.003f * sr)); previous = w;
        }
    }
    private static float[] Hammer(XorShift32 rng, int sr)
    {
        int count = rng.Range(3, 6);
        double f0 = rng.Range(420f, 640f);
        var buffer = new float[Samples((count - 1) * .45 + .9, sr)];
        for (int k = 0; k < count; k++)
            Clank(buffer, Math.Max(0, (int)((k * .45 + rng.Range(-.02f, .02f)) * sr)), f0 * rng.Range(.995f, 1.005f), rng.Range(.75f, 1), .8, sr, rng);
        return buffer;
    }
    private static void Thud(float[] into, int at, double fromHz, double toHz, double glide, double decay, float amp, int sr, XorShift32 rng)
    {
        var low = OnePole.At(250, sr); var low2 = OnePole.At(250, sr); var knock = new Svf(); knock.Set(180, 3, sr);
        double phase = 0;
        for (int j = 0; at + j < into.Length && j < decay * 7 * sr; j++)
        {
            double t = j / (double)sr, f = toHz + (fromHz - toHz) * Math.Exp(-t / glide);
            phase += 2 * Math.PI * f / sr;
            double env = Math.Min(1, t / .003) * Math.Exp(-t / decay);
            float w = rng.Signed();
            into[at + j] += (float)(amp * (env * Math.Sin(phase) + Math.Exp(-t / .03) * (.5 * low2.Low(low.Low(w)) * 10 + .3 * knock.Band(w))));
        }
    }
    private static float[] Clatter(XorShift32 rng, int sr)
    {
        var buffer = new float[Samples(.9, sr)];
        double t = 0, gap = rng.Range(.05f, .11f);
        float amp = 1;
        for (int hits = rng.Range(6, 11), k = 0; k < hits && t < .8; k++)
        {
            double f = rng.Range(700f, 2600f);
            int at = (int)(t * sr);
            Resonate(buffer, at, f, rng.Range(.02f, .07f), amp, 0, sr);
            Resonate(buffer, at, Math.Min(f * rng.Range(1.5f, 2.4f), .45 * sr), rng.Range(.015f, .04f), amp * .6f, 0, sr);
            float previous = 0;
            for (int j = 0; j < Samples(.0015, sr) && at + j < buffer.Length; j++) { float w = rng.Signed(); buffer[at + j] += .6f * amp * (w - previous); previous = w; }
            t += gap * rng.Range(.7f, 1.3f); gap *= .82; amp *= rng.Range(.7f, .95f);
        }
        return buffer;
    }
    // GatekeeperBarrier swings 92 degrees at 150 deg/s, so the doors stop .61 s after IsOpen flips: the creak fills
    // that swing, both doors thud into their stops, and a short settling creak trails off (1.6 s in all).
    private static float[] GateCreak(XorShift32 rng, int sr)
    {
        var buffer = new float[Samples(1.6, sr)];
        Creak(buffer, 0, .62, 90, 140, 1, sr, rng);
        Thud(buffer, Samples(.63, sr), 85, 52, .05, .1, .9f, sr, rng);
        Thud(buffer, Samples(.74, sr), 95, 58, .05, .09, .6f, sr, rng);
        Creak(buffer, Samples(.82, sr), .22, 75, 62, .35f, sr, rng);
        return buffer;
    }
    // Stick-slip: a sawtooth whose every period and step are jittered, ringing a band-pass at 600 Hz and two wood modes.
    private static void Creak(float[] into, int at, double seconds, double fromHz, double toHz, float amp, int sr, XorShift32 rng)
    {
        var body = new Svf(); var upper = new Svf(); var lower = new Svf();
        body.Set(600, 4, sr); upper.Set(1150, 7, sr); lower.Set(320, 3, sr);
        double phase = 0, jitter = 1, step = 1;
        int length = Samples(seconds, sr);
        for (int j = 0; j < length && at + j < into.Length; j++)
        {
            double x = j / (double)length, f = fromHz + (toHz - fromHz) * x * x * (3 - 2 * x);
            phase += f / sr * jitter;
            if (phase >= 1) { phase -= 1; jitter = rng.Range(.85f, 1.15f); step = rng.Range(.6f, 1); }
            float saw = (float)((2 * phase - 1) * step);
            double env = Math.Min(1, x / .1) * Math.Min(1, (1 - x) / .15) * (1 + .25 * Math.Sin(2 * Math.PI * 6 * x * seconds));
            into[at + j] += (float)(amp * env * (.55 * body.Band(saw) + .3 * upper.Band(saw) + .35 * lower.Band(saw)));
        }
    }
    // The 55->38 Hz body carries the weight on a PA or headphones; laptop and projector speakers roll off below about
    // 150 Hz, so the hit also needs "skin": the membrane's 2.92 and 4.06 modes (about 110-225 Hz) and a longer 190 Hz slap.
    // Through a 150 Hz high-pass this keeps the drum about 6 dB over the p3 and p1 beds (SpiritAudioChecks), where the
    // bare kick fell 2-3 dB under them.
    private static float[] DrumHit(XorShift32 rng, int sr)
    {
        int length = Samples(1.2, sr);
        var buffer = new float[length];
        var slap = new Svf(); var slapHigh = new Svf(); var click = OnePole.At(2500, sr);
        slap.Set(190, 1.5f, sr); slapHigh.Set(420, 2, sr);
        double phase = 0, phase2 = 0, phase3 = 0, phase4 = 0;
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)sr, f = 38 + 17 * Math.Exp(-t / .09);
            phase += 2 * Math.PI * f / sr; phase2 += 2 * Math.PI * 1.59 * f / sr; phase3 += 2 * Math.PI * 2.92 * f / sr; phase4 += 2 * Math.PI * 4.06 * f / sr;
            double attack = Math.Min(1, t / .002);
            float w = rng.Signed();
            double v = attack * (Math.Exp(-t / .3) * Math.Sin(phase) + .35 * Math.Exp(-t / .18) * Math.Sin(phase2) + .5 * Math.Exp(-t / .1) * (Math.Sin(phase3) + Math.Sin(phase4)))
                + Math.Exp(-t / .07) * 1.35 * slap.Band(w) * 2 + Math.Exp(-t / .02) * .2 * slapHigh.Band(w) * 2 + Math.Exp(-t / .004) * .3 * click.Low(w) * 3;
            buffer[i] = (float)(Math.Tanh(2.5 * v) / Math.Tanh(2.5));   // saturation adds more harmonics in the same band
        }
        return buffer;
    }

    // ---- Shared helpers.
    private static XorShift32 Rng(int seed, string name, uint salt)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in name) { h ^= c; h *= 16777619u; }        // FNV-1a: string.GetHashCode is randomized per process
            return new XorShift32((uint)seed * 0x9E3779B1u ^ h ^ salt * 0x85EBCA77u);
        }
    }
    private static double Quantize(double hz, double seconds) => Math.Max(1, Math.Round(hz * seconds)) / seconds;
    private static float SoftClip(float x)
    {
        float a = Math.Abs(x);
        return a <= .55f ? x : Math.Sign(x) * (.55f + .15f * (float)Math.Tanh((a - .55f) / .15f));   // never reaches .7
    }
    private static float[] Repeat(float[] loop, int length)
    {
        var output = new float[length];
        for (int i = 0; i < length; i++) output[i] = loop[i % loop.Length];
        return output;
    }
    // Scales by the peak of the first period only, so a longer stream of a periodic layer never changes its level.
    private static float[] Normalize(float[] buffer, int period, float peak)
    {
        float max = 0;
        for (int i = 0; i < period; i++) max = Math.Max(max, Math.Abs(buffer[i]));
        if (max > 0) for (int i = 0; i < buffer.Length; i++) buffer[i] *= peak / max;
        return buffer;
    }
    // Fades the edges to exactly 0, removes the mean along the same fade shape (so the edges stay 0) and peaks at .9.
    private static float[] Finish(float[] buffer, int sr)
    {
        int n = buffer.Length, fadeIn = Math.Max(1, sr / 2000), fadeOut = Math.Max(1, Math.Min(n / 4, sr / 100));
        double sum = 0, shape = 0;
        var fade = new float[n];
        for (int i = 0; i < n; i++)
        {
            fade[i] = Math.Min(1, Math.Min(i / (float)fadeIn, (n - 1 - i) / (float)fadeOut));
            buffer[i] *= fade[i]; sum += buffer[i]; shape += fade[i];
        }
        float offset = shape > 0 ? (float)(sum / shape) : 0, max = 0;
        for (int i = 0; i < n; i++) { buffer[i] -= offset * fade[i]; max = Math.Max(max, Math.Abs(buffer[i])); }
        if (max > 0) for (int i = 0; i < n; i++) buffer[i] *= .9f / max;
        return buffer;
    }
    private static float[] Wavetable(int harmonics, double rolloff, XorShift32 rng)
    {
        var table = new float[4096];
        for (int h = 1; h <= harmonics; h++)
        {
            double amp = 1 / Math.Pow(h, rolloff), phase = 2 * Math.PI * rng.NextFloat();
            for (int i = 0; i < table.Length; i++) table[i] += (float)(amp * Math.Sin(2 * Math.PI * h * i / table.Length + phase));
        }
        return table;
    }
    private static float Lookup(float[] table, double phase)
    {
        double x = phase * table.Length;
        int i = (int)x;
        float frac = (float)(x - i);
        return table[i & 4095] + (table[(i + 1) & 4095] - table[i & 4095]) * frac;
    }
    private static void Resonate(float[] into, int at, double hz, double tau, float amp, double phase, int sr)
    {
        double w = 2 * Math.PI * hz / sr, d = Math.Exp(-1 / (tau * sr)), c = Math.Cos(w) * d, s = Math.Sin(w) * d, re = Math.Cos(phase) * amp, im = Math.Sin(phase) * amp;
        double attack = Math.Max(1, .0005 * sr);
        for (int j = 0; j < tau * 6 * sr && at + j < into.Length; j++)
        {
            if (at + j >= 0) into[at + j] += (float)(im * Math.Min(1, j / attack));
            double r = re * c - im * s; im = re * s + im * c; re = r;
        }
    }

    private struct OnePole
    {
        private float a, y;
        public static OnePole At(double hz, int sr) { var f = new OnePole(); f.Set(hz, sr); return f; }
        public void Set(double hz, int sr) { a = (float)(1 - Math.Exp(-2 * Math.PI * Math.Min(hz, .45 * sr) / sr)); }
        public float Low(float x) { y += a * (x - y); return y; }
    }
    // Topology-preserving state-variable filter; Band is normalized to unity gain at the centre.
    private struct Svf
    {
        private float k, a1, a2, a3, ic1, ic2;
        public void Set(double hz, float q, int sr)
        {
            double g = Math.Tan(Math.PI * Math.Min(hz, .45 * sr) / sr);
            k = 1 / q; a1 = (float)(1 / (1 + g * (g + k))); a2 = (float)(g * a1); a3 = (float)(g * a2);
        }
        public float Band(float x)
        {
            float v3 = x - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2 * v1 - ic1; ic2 = 2 * v2 - ic2;
            return k * v1;
        }
    }
    // A sum of sines, each a whole number of cycles per period n, read sample by sample from index start; range [-1, 1].
    private sealed class Lfo
    {
        private readonly double[] re, im, c, s, weight;
        private readonly double norm;
        public Lfo(XorShift32 rng, int n, int sr, double[] hz, double[] weights, int start)
        {
            int m = hz.Length;
            re = new double[m]; im = new double[m]; c = new double[m]; s = new double[m]; weight = weights;
            for (int j = 0; j < m; j++)
            {
                double w = 2 * Math.PI * Math.Max(1, Math.Round(hz[j] * n / sr)) / n, phase = 2 * Math.PI * rng.NextFloat() + w * start;
                re[j] = Math.Cos(phase); im[j] = Math.Sin(phase); c[j] = Math.Cos(w); s[j] = Math.Sin(w); norm += weights[j];
            }
        }
        public float Next()
        {
            double sum = 0;
            for (int j = 0; j < re.Length; j++)
            {
                sum += weight[j] * im[j];
                double r = re[j] * c[j] - im[j] * s[j]; im[j] = re[j] * s[j] + im[j] * c[j]; re[j] = r;
            }
            return (float)(sum / norm);
        }
    }
}
