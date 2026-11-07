using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

// Checks over ProceduralAudio (no Unity): the same seed gives identical samples; every sample is finite and
// |s| <= 1; each loop's wrap continues its stream (< 1e-3) with no click; crackle density rises with the fire scale;
// one-shots have no DC offset and start and end at 0. CampMix picks the designed drum pattern for each patience after
// CampSoundscape's smoothing from any earlier patience, and the drum stays above the camp bed through a 150 Hz high-pass
// (laptop speakers). camp_render_ms times one render of every camp loop and sound.
// With {"previews": DIR} it also writes 16-bit WAVs of every camp loop, every SpiritSound and three rough mono mixes
// (CampMix gains, distance rolloff, no reverb or panning).
public static class SpiritAudioChecks
{
    private static readonly float[] FireScales = { .65f, 1, 1.9f };
    private static readonly int[] RangeRates = { 16000, 48000 };
    private static readonly int[] SoundRates = { 16000, 24000, 48000 };

    public static object Run(JsonNode request, GatekeeperScenario scenario, GatekeeperStory.Document story)
    {
        var failures = new List<string>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        string section = "";
        void Check(bool ok, string what)
        {
            counts[section] = counts.TryGetValue(section, out int c) ? c + 1 : 1;
            if (!ok && failures.Count < 200) failures.Add(section + ": " + what);
        }
        void Section(string name, Action body) { section = name; try { body(); } catch (Exception e) { Check(false, "crashed: " + e); } }
        var loops = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var sounds = new Dictionary<SpiritSound, float[]>();
        var density = new List<object>();
        var stats = new List<object>();
        var written = new List<string>();
        var clock = new System.Diagnostics.Stopwatch();

        Section("H6 determinism", () =>
        {
            foreach (var spec in ProceduralAudio.CampLoops)
            {
                clock.Start(); var a = Loop(spec); clock.Stop();
                var b = Loop(spec);
                var other = ProceduralAudio.RenderLoop(spec.Layer, spec.Seed + 1, spec.Rate, spec.Seconds, spec.Fire);
                Check(Same(a, b), spec.Name + ": same seed, different samples");
                Check(!Same(a, other), spec.Name + ": seed " + (spec.Seed + 1) + " gives the same samples");
                Check(a.Length == ProceduralAudio.LoopLength(spec.Rate, spec.Seconds), spec.Name + ": length " + a.Length);
                loops[spec.Name] = a;
            }
            foreach (var sound in ProceduralAudio.Sounds)
            {
                clock.Start(); var a = ProceduralAudio.Render(sound, 1, ProceduralAudio.CampOneShotRate); clock.Stop();
                Check(Same(a, ProceduralAudio.Render(sound, 1, ProceduralAudio.CampOneShotRate)), sound + ": same seed, different samples");
                Check(!Same(a, ProceduralAudio.Render(sound, 2, ProceduralAudio.CampOneShotRate)), sound + ": seed 2 gives the same samples");
                sounds[sound] = a;
            }
        });

        Section("H6 range", () =>
        {
            foreach (var pair in loops) Check(InRange(pair.Value, out string why), pair.Key + ": " + why);
            foreach (string layer in ProceduralAudio.Layers)
                foreach (int rate in RangeRates)
                    foreach (float fire in ProceduralAudio.UsesFire(layer) ? FireScales : new[] { 1f })
                        Check(InRange(ProceduralAudio.RenderLoop(layer, 9, rate, 3, fire), out string why), layer + " @" + rate + " fire " + fire + ": " + why);
            foreach (var sound in ProceduralAudio.Sounds)
                foreach (int rate in SoundRates)
                    Check(InRange(ProceduralAudio.Render(sound, 3, rate), out string why), sound + " @" + rate + ": " + why);
            bool threw = false;
            try { ProceduralAudio.RenderLoop("no such layer", 1, 24000, 2); } catch (ArgumentException) { threw = true; }
            Check(threw, "an unknown layer must throw");
        });

        Section("H6 seams", () =>
        {
            foreach (var spec in ProceduralAudio.CampLoops)
            {
                var loop = loops[spec.Name];
                int n = loop.Length, extra = 200;
                var stream = ProceduralAudio.RenderStream(spec.Layer, spec.Seed, spec.Rate, spec.Seconds, spec.Fire, extra);
                // The wrap loop[n-1] -> loop[0] must be the stream's own step stream[n-1] -> stream[n].
                Check(Math.Abs(loop[0] - stream[n]) < 1e-3, spec.Name + ": loop head " + loop[0] + " vs continuation " + stream[n]);
                Check(Math.Abs(loop[n - 1] - stream[n - 1]) < 1e-3, spec.Name + ": loop tail " + loop[n - 1] + " vs stream " + stream[n - 1]);
                // ...and no larger than the loop's own ordinary steps.
                var steps = new float[n - 1];
                for (int i = 1; i < n; i++) steps[i - 1] = Math.Abs(loop[i] - loop[i - 1]);
                Array.Sort(steps);
                float seam = Math.Abs(loop[0] - loop[n - 1]), p999 = steps[(int)(.999 * (steps.Length - 1))];
                Check(seam <= Math.Max(1e-3f, p999), spec.Name + ": seam step " + seam + " above the 99.9th percentile step " + p999);
                if (ProceduralAudio.IsPeriodic(spec.Layer))
                {
                    float drift = 0;
                    for (int i = 0; i < extra; i++) drift = Math.Max(drift, Math.Abs(stream[n + i] - stream[i]));
                    Check(drift < 1e-3, spec.Name + ": not periodic (" + drift + ")");
                }
                else
                {
                    // Equal-power crossfade: the level around the seam matches the loop's typical level.
                    int window = spec.Rate / 20;
                    double seamRms = Rms(loop, n - window / 2, window), median = MedianWindowRms(loop, window);
                    Check(seamRms > median / 4 && seamRms < median * 4, spec.Name + ": seam level " + seamRms.ToString("0.0000") + " vs median " + median.ToString("0.0000"));
                }
            }
        });

        Section("H6 crackle density", () =>
        {
            foreach (string layer in new[] { "hearth", "torch", "campfire" })
            {
                var found = FireScales.Select(f => Onsets(ProceduralAudio.RenderLoop(layer, 5, 24000, 12, f), 24000)).ToArray();
                density.Add(new { layer, fire = FireScales, onsets_per_second = found.Select(c => Math.Round(c / 12.0, 2)).ToArray() });
                Check(found[0] < found[1] && found[1] < found[2], layer + ": onsets " + string.Join("/", found) + " do not rise with fire " + string.Join("/", FireScales));
                Check(found[2] >= 2 * found[0], layer + ": fire 1.9 has fewer than twice the onsets of fire .65 (" + found[2] + " vs " + found[0] + ")");
            }
        });

        Section("H6 one-shots", () =>
        {
            foreach (var sound in ProceduralAudio.Sounds)
                foreach (int rate in SoundRates)
                {
                    var s = rate == ProceduralAudio.CampOneShotRate ? sounds[sound] : ProceduralAudio.Render(sound, 1, rate);
                    double mean = s.Average(x => (double)x);
                    float peak = s.Max(x => Math.Abs(x));
                    Check(Math.Abs(mean) < 1e-4, sound + " @" + rate + ": DC offset " + mean);
                    Check(s[0] == 0 && Math.Abs(s[s.Length - 1]) < 1e-6, sound + " @" + rate + ": does not start and end at 0");
                    Check(peak > .5f && peak <= .9001f, sound + " @" + rate + ": peak " + peak);
                }
            // The road approach's footfalls (ExperienceDirector): the same one-shot rules, deterministic per seed, 300 ms.
            foreach (int rate in SoundRates)
            {
                var s = ProceduralAudio.RenderFootstep(4271, rate);
                double mean = s.Average(x => (double)x);
                float peak = s.Max(x => Math.Abs(x));
                Check(InRange(s, out string why), "footstep @" + rate + ": " + why);
                Check(Math.Abs(mean) < 1e-4, "footstep @" + rate + ": DC offset " + mean);
                Check(s[0] == 0 && Math.Abs(s[s.Length - 1]) < 1e-6, "footstep @" + rate + ": does not start and end at 0");
                Check(peak > .5f && peak <= .9001f, "footstep @" + rate + ": peak " + peak);
                Check(Math.Abs(s.Length / (double)rate - .3) < .002, "footstep @" + rate + ": not 300 ms");
                Check(Same(s, ProceduralAudio.RenderFootstep(4271, rate)) && !Same(s, ProceduralAudio.RenderFootstep(4272, rate)), "footstep @" + rate + ": not deterministic per seed");
            }
            Check(Math.Abs(sounds[SpiritSound.Gasp].Length / (double)ProceduralAudio.CampOneShotRate - .18) < .002, "Gasp is not 180 ms");
            Check(Math.Abs(sounds[SpiritSound.GateCreak].Length / (double)ProceduralAudio.CampOneShotRate - 1.6) < .002, "GateCreak is not 1.6 s");
            foreach (var pair in loops)
            {
                double mean = pair.Value.Average(x => (double)x);
                Check(Math.Abs(mean) < .01, pair.Key + ": loop DC offset " + mean.ToString("0.0000"));
            }
        });

        Section("drum pattern", () =>
        {
            // CampSoundscape eases T in float every frame and stalls a few float steps short of the target, from either
            // side; the pattern at each patience must not depend on where T came from (p>=4 silent, heartbeat pair at
            // p3-p2, war pattern at p<=1).
            static string Expected(int p) => p >= 4 ? "silent" : p >= 2 ? "heartbeat" : "war";
            static string Hits(float t) => string.Join(",", Enumerable.Range(0, 16).Where(k => CampMix.DrumStep(t, k) > 0));
            var hits = new Dictionary<string, string> { ["silent"] = "", ["heartbeat"] = "0,1,8,9", ["war"] = "0,4,6,8,12" };
            for (int to = 0; to <= 5; to++)
            {
                Check(CampMix.DrumPattern(CampMix.Tension(to)) == Expected(to) && Hits(CampMix.Tension(to)) == hits[Expected(to)], "p" + to + " at its exact tension");
                foreach (int fps in new[] { 30, 60, 120, 144 })
                    for (int from = 0; from <= 5; from++)
                    {
                        float t = CampMix.Tension(from), dt = 1f / fps;
                        for (int frame = 0; frame < 60 * fps; frame++) t = CampMix.SmoothTension(t, to, dt);
                        string got = CampMix.DrumPattern(t);
                        Check(got == Expected(to) && Hits(t) == hits[got], "p" + from + " -> p" + to + " at " + fps + " fps: T " + t.ToString("R") + " plays " + got + " [" + Hits(t) + "]");
                    }
            }
        });

        Section("drum on small speakers", () =>
        {
            // Laptop and projector speakers roll off below about 150 Hz. Through a 4th-order 150 Hz high-pass, the 100 ms
            // after each hit must still stand 3 dB over the camp bed it plays against (p3 heartbeat, p1 war).
            const int rate = 24000;
            foreach (int patience in new[] { 3, 1 })
            {
                Mix(loops, sounds, patience, false, rate, 12, out var drums, out var bed);
                HighPass(drums, 150, rate); HighPass(bed, 150, rate);
                float tension = CampMix.Tension(patience);
                double step = 15.0 / CampMix.Bpm(tension), sum = 0;
                int count = 0, window = rate / 10;
                for (int k = 0; (k * step + .1) * rate < drums.Length; k++)
                    if (CampMix.DrumStep(tension, k) > 0)
                    {
                        int at = (int)(k * step * rate);
                        sum += 20 * Math.Log10(Math.Max(Rms(drums, at, window), 1e-9) / Math.Max(Rms(bed, at, window), 1e-9)); count++;
                    }
                double margin = count > 0 ? sum / count : double.NegativeInfinity;
                stats.Add(new { name = "drum over bed above 150 Hz, p" + patience, db = Math.Round(margin, 1) });
                Check(margin >= 3, "p" + patience + ": the drum is " + margin.ToString("0.0") + " dB over the bed above 150 Hz (want >= 3)");
            }
        });

        section = "stats";
        foreach (var spec in ProceduralAudio.CampLoops) if (loops.TryGetValue(spec.Name, out var l)) stats.Add(Stats("loop " + spec.Name, l, spec.Rate));
        foreach (var pair in sounds) stats.Add(Stats("sound " + pair.Key, pair.Value, ProceduralAudio.CampOneShotRate));

        string previews = request?["previews"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(previews))
            Section("previews", () =>
            {
                Directory.CreateDirectory(previews);
                void Save(string name, float[] samples, int rate)
                {
                    string path = Path.Combine(previews, name + ".wav");
                    WriteWav(path, samples, rate); written.Add(Path.GetFullPath(path));
                }
                foreach (var spec in ProceduralAudio.CampLoops) Save("loop-" + spec.Name.Replace(' ', '-'), Tile(loops[spec.Name], 2), spec.Rate);
                foreach (var pair in sounds) Save("sound-" + pair.Key, pair.Value, ProceduralAudio.CampOneShotRate);
                foreach (float fire in FireScales) Save("fire-hearth-x" + fire.ToString("0.00"), ProceduralAudio.RenderLoop("hearth", 11, 24000, 8, fire), 24000);
                foreach (var (name, patience, moment) in new[] { ("mix-p3-start", 3, false), ("mix-p1-after-threaten", 1, false), ("mix-p3-mind-moment", 3, true) })
                {
                    var mix = Mix(loops, sounds, patience, moment, 24000, 24);
                    Save(name, mix, 24000); stats.Add(Stats(name, mix, 24000));
                }
            });

        return new
        {
            passed = failures.Count == 0,
            checks = string.Join(", ", counts.Select(c => c.Key + " " + c.Value)) + "; " + failures.Count + " failed",
            failures, camp_render_ms = clock.ElapsedMilliseconds, crackle_density = density, stats, previews = written
        };
    }

    private static float[] Loop(ProceduralAudio.LoopSpec spec) => ProceduralAudio.RenderLoop(spec.Layer, spec.Seed, spec.Rate, spec.Seconds, spec.Fire);
    private static bool Same(float[] a, float[] b) => a.Length == b.Length && MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static bool InRange(float[] s, out string why)
    {
        for (int i = 0; i < s.Length; i++)
            if (!float.IsFinite(s[i]) || Math.Abs(s[i]) > 1) { why = "sample " + i + " = " + s[i]; return false; }
        why = s.Length == 0 ? "empty" : "ok";
        return s.Length > 0;
    }
    // Crackle onsets: first difference above .06 (beds and hiss stay below about .035), at least 5 ms apart.
    private static int Onsets(float[] s, int rate)
    {
        int count = 0, quiet = rate / 200, last = -quiet;
        for (int i = 1; i < s.Length; i++)
            if (Math.Abs(s[i] - s[i - 1]) > .06f && i - last >= quiet) { count++; last = i; }
        return count;
    }
    private static double Rms(float[] s, int start, int length)
    {
        double sum = 0;
        for (int i = 0; i < length; i++) { float x = s[((start + i) % s.Length + s.Length) % s.Length]; sum += x * x; }
        return Math.Sqrt(sum / length);
    }
    private static double MedianWindowRms(float[] s, int window)
    {
        var all = new List<double>();
        for (int at = 0; at + window <= s.Length; at += window) all.Add(Rms(s, at, window));
        all.Sort();
        return all[all.Count / 2];
    }
    private static float[] Tile(float[] loop, int times)
    {
        var output = new float[loop.Length * times];
        for (int i = 0; i < output.Length; i++) output[i] = loop[i % loop.Length];
        return output;
    }

    private static object Stats(string name, float[] s, int rate)
    {
        double sum = 0, squares = 0; float peak = 0;
        foreach (float x in s) { sum += x; squares += x * x; peak = Math.Max(peak, Math.Abs(x)); }
        double rms = Math.Sqrt(squares / s.Length);
        return new
        {
            name, rate, seconds = Math.Round(s.Length / (double)rate, 3), rms_dbfs = Math.Round(20 * Math.Log10(Math.Max(rms, 1e-9)), 1),
            peak = Math.Round(peak, 3), centroid_hz = Math.Round(Centroid(s, rate)), mean = Math.Round(sum / s.Length, 6)
        };
    }
    // Magnitude-weighted mean frequency over Hann-windowed 2048-sample frames (at most 256 frames, spread over the signal).
    private static double Centroid(float[] s, int rate)
    {
        const int size = 2048;
        if (s.Length < size) return 0;
        int frames = Math.Min(256, (s.Length - size) / (size / 2) + 1);
        double stride = frames > 1 ? (s.Length - size) / (double)(frames - 1) : 0, weighted = 0, total = 0;
        var re = new double[size]; var im = new double[size];
        for (int f = 0; f < frames; f++)
        {
            int at = (int)(f * stride);
            for (int i = 0; i < size; i++) { re[i] = s[at + i] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (size - 1))); im[i] = 0; }
            Fft(re, im);
            for (int k = 1; k < size / 2; k++)
            {
                double magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                weighted += magnitude * k * rate / (double)size; total += magnitude;
            }
        }
        return total > 0 ? weighted / total : 0;
    }
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length, wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += length)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < length / 2; k++)
                {
                    int a = i + k, b = a + length / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double next = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = next;
                }
            }
        }
    }

    // A rough mono stand-in for CampSoundscape at a fixed patience: same clips and CampMix gains, Unity's logarithmic
    // rolloff from the portrait camera, one-pole low-passes for the moment muffle and drone filter; no panning or reverb.
    private static float[] Mix(Dictionary<string, float[]> loops, Dictionary<SpiritSound, float[]> sounds, int patience, bool moment, int rate, double seconds)
        => Mix(loops, sounds, patience, moment, rate, seconds, out _, out _);
    // drums: the war drum alone; bed: everything else. The mix is their sum, clipped to [-1, 1].
    private static float[] Mix(Dictionary<string, float[]> loops, Dictionary<SpiritSound, float[]> sounds, int patience, bool moment, int rate, double seconds,
        out float[] drums, out float[] bed)
    {
        int length = (int)(rate * seconds);
        var world = new float[length]; var music = new float[length]; var shimmer = new float[length];
        float fire = CampMix.FireScale(patience), tension = CampMix.Tension(patience), roar = CampMix.RoarMix(fire), w = moment ? 1 : 0;
        var listener = (x: .3, y: 1.29, z: 2.75);
        float Rolloff((double x, double y, double z) p, double min)
        {
            double d = Math.Sqrt((p.x - listener.x) * (p.x - listener.x) + (p.y - listener.y) * (p.y - listener.y) + (p.z - listener.z) * (p.z - listener.z));
            return (float)(min / Math.Max(min, d));
        }
        void Add(float[] into, string name, float gain)
        {
            if (gain <= 0) return;
            var spec = ProceduralAudio.CampLoops.First(l => l.Name == name);
            var loop = loops[name];
            for (int i = 0; i < length; i++)
            {
                double at = i * (double)spec.Rate / rate;
                int k = (int)at; float frac = (float)(at - k);
                into[i] += gain * (loop[k % loop.Length] + (loop[(k + 1) % loop.Length] - loop[k % loop.Length]) * frac);
            }
        }
        void Shot(float[] into, SpiritSound sound, double at, float gain)
        {
            var s = sounds[sound];
            for (int i = 0, o = (int)(at * rate); i < s.Length && o + i < length; i++) into[o + i] += gain * s[i];
        }
        Add(world, "hearth calm", CampMix.Hearth * fire * (float)Math.Cos(roar * Math.PI / 2) * Rolloff((-1, .4, 2.2), 1.5));
        Add(world, "hearth roar", CampMix.Hearth * fire * (float)Math.Sin(roar * Math.PI / 2) * Rolloff((-1, .4, 2.2), 1.5));
        Add(world, "torch west", CampMix.Torch * fire * Rolloff((-2.22, 2.38, -3.95), 3));
        Add(world, "torch east", CampMix.Torch * fire * Rolloff((2.22, 2.38, -3.95), 3));
        Add(world, "campfire", CampMix.Campfire * fire * Rolloff((1, .25, -10), 5));
        Add(world, "wind", CampMix.Wind);
        Add(world, "crickets left", CampMix.CricketLevel(patience, false));
        Add(world, "crickets right", CampMix.CricketLevel(patience, false));
        if (!moment)
        {
            float tents = Rolloff((1.5, .6, -10), 4);
            foreach (var (sound, at) in new[] { (SpiritSound.Hammer, 2.0), (SpiritSound.Clank, 9.0), (SpiritSound.Thud, 14.0), (SpiritSound.Clatter, 19.0) })
                Shot(world, sound, at, CampMix.Events.First(e => e.sound == sound).gain * tents);
            double step = 15.0 / CampMix.Bpm(tension);
            for (int k = 0; k * step < seconds; k++)
            {
                float hit = CampMix.DrumStep(tension, k);
                if (hit > 0) Shot(music, SpiritSound.DrumHit, k * step, CampMix.Drum * hit);
            }
        }
        var drone = new float[length];
        Add(drone, "drone", CampMix.DroneGain(tension));
        Lowpass(drone, CampMix.DroneCutoff(tension, w), rate);
        Lowpass(world, CampMix.WorldCutoff(w), rate);
        Lowpass(music, CampMix.MusicCutoff(w), rate);
        Add(shimmer, "shimmer", CampMix.Shimmer * w);
        var output = new float[length];
        drums = new float[length]; bed = new float[length];
        for (int i = 0; i < length; i++)
        {
            drums[i] = music[i] * CampMix.MusicGain(w);
            bed[i] = world[i] * CampMix.WorldGain(w) + drone[i] * CampMix.MusicGain(w) + shimmer[i];
            output[i] = Math.Max(-1, Math.Min(1, drums[i] + bed[i]));
        }
        return output;
    }
    // Butterworth (two RBJ biquads, Q .5412 and 1.3066), in place.
    private static void HighPass(float[] s, double hz, int rate)
    {
        foreach (double q in new[] { .5412, 1.3066 })
        {
            double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            double b0 = (1 + cos) / 2 / a0, b1 = -(1 + cos) / a0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0, x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double x = s[i], y = b0 * x + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y; s[i] = (float)y;
            }
        }
    }
    private static void Lowpass(float[] s, float hz, int rate)
    {
        if (hz >= .45f * rate) return;
        double a = 1 - Math.Exp(-2 * Math.PI * hz / rate);
        double y1 = 0, y2 = 0;
        for (int i = 0; i < s.Length; i++) { y1 += a * (s[i] - y1); y2 += a * (y1 - y2); s[i] = (float)y2; }
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        int bytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (float x in samples) w.Write((short)Math.Round(Math.Max(-1, Math.Min(1, x)) * 32767));
    }
}
