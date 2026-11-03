// Headless checks for PlayerSpeechAudio, what the microphone hands Whisper: mono, 16 kHz without aliasing, trimmed to the
// speech, a valid PCM16 WAV, and transcripts cleaned of Whisper's silence fillers. No Unity, no model.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

public static class PlayerSpeechChecks
{
    private static float[] Tone(double hz, int rate, double seconds, double dbfs, double noiseDbfs = -120, int seed = 1)
    {
        var random = new Random(seed);
        double amplitude = Math.Pow(10, dbfs / 20), noise = Math.Pow(10, noiseDbfs / 20);
        var pcm = new float[(int)(rate * seconds)];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate) + noise * (random.NextDouble() * 2 - 1));
        return pcm;
    }
    private static double Rms(float[] pcm, int from = 0, int to = -1)
    {
        to = to < 0 ? pcm.Length : to; double sum = 0;
        for (int i = from; i < to; i++) sum += pcm[i] * pcm[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }
    private static int Crossings(float[] pcm) { int n = 0; for (int i = 1; i < pcm.Length; i++) if (pcm[i - 1] < 0 && pcm[i] >= 0) n++; return n; }

    public static object Run(JsonNode request, GatekeeperScenario scenario, GatekeeperStory.Document story)
    {
        var failures = new List<string>(); int checks = 0;
        void Check(bool ok, string what) { checks++; if (!ok) failures.Add(what); }

        var stereo = new float[] { 1, -1, .5f, .5f };
        Check(PlayerSpeechAudio.ToMono(stereo, 2).SequenceEqual(new float[] { 0, .5f }), "stereo is averaged to mono");

        foreach (int rate in new[] { 48000, 44100, 22050 })
        {
            var a440 = PlayerSpeechAudio.Resample(Tone(440, rate, 1, -6), rate);
            Check(Math.Abs(a440.Length - 16000) <= 1, rate + " Hz: one second stays one second (" + a440.Length + ")");
            Check(Math.Abs(Crossings(a440) - 440) <= 2, rate + " Hz: a 440 Hz tone stays 440 Hz (" + Crossings(a440) + ")");
            Check(Math.Abs(20 * Math.Log10(Rms(a440, 400, 15600) / Rms(Tone(440, rate, 1, -6)))) < 1, rate + " Hz: speech-band level is kept");
        }
        // 12 kHz is above the new 8 kHz Nyquist: without the low-pass it would fold to 4 kHz at full level.
        var alias = PlayerSpeechAudio.Resample(Tone(12000, 48000, 1, -6), 48000);
        double aliasDb = 20 * Math.Log10(Rms(alias, 400, 15600) / Rms(Tone(12000, 48000, 1, -6)));
        Check(aliasDb < -25, "48 kHz: content above 8 kHz is filtered, not folded into speech (" + aliasDb.ToString("0") + " dB)");
        Check(PlayerSpeechAudio.Resample(new float[0], 48000).Length == 0 && PlayerSpeechAudio.Resample(Tone(440, 16000, .5, -6), 16000).Length == 8000, "empty and 16 kHz input pass through");

        Check(!PlayerSpeechAudio.Analyze(Tone(0, 16000, 2, -120, -70)).HasSpeech, "room noise alone is not speech");
        Check(!PlayerSpeechAudio.Analyze(Tone(0, 16000, .02, -120)).HasSpeech, "a clip shorter than a frame is not speech");
        var said = Tone(0, 16000, .5, -120, -65).Concat(Tone(220, 16000, 1, -20, -65, 2)).Concat(Tone(0, 16000, .7, -120, -65, 3)).ToArray();
        var gate = PlayerSpeechAudio.Analyze(said);
        Check(gate.HasSpeech && Math.Abs(gate.VoicedSeconds - 1) < .05, "a second of speech in noise is found (" + gate.VoicedSeconds.ToString("0.00") + " s)");
        Check(gate.Start <= 8000 && gate.Start >= 8000 - 3200 && gate.End >= 24000 && gate.End <= 24000 + 3200, "the speech span keeps a short margin (" + gate.Start + ".." + gate.End + ")");
        var blip = Tone(0, 16000, .5, -120, -65).Concat(Tone(220, 16000, .1, -20, -65, 2)).Concat(Tone(0, 16000, .5, -120, -65, 3)).ToArray();
        Check(!PlayerSpeechAudio.Analyze(blip).HasSpeech, "a 100 ms click is not speech");
        var quiet = Tone(220, 16000, 1, -55, -80);
        Check(!PlayerSpeechAudio.Analyze(quiet).HasSpeech, "speech far below -45 dBFS is ignored");

        var pcm = new float[] { 0, .5f, -.5f, 1, -1, 2 };
        var wav = PlayerSpeechAudio.Wav(pcm);
        Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 8) == "WAVEfmt " && Encoding.ASCII.GetString(wav, 36, 4) == "data", "WAV chunk layout");
        Check(BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16 && BitConverter.ToInt32(wav, 40) == 12 && wav.Length == 56,
            "WAV header: 16 kHz, mono, 16-bit, data length");
        Check(BitConverter.ToInt16(wav, 46) == 16384 && BitConverter.ToInt16(wav, 50) == 32767 && BitConverter.ToInt16(wav, 54) == 32767, "samples are scaled and clipped");

        Check(PlayerSpeechAudio.Clean("[BLANK_AUDIO]", false) == null && PlayerSpeechAudio.Clean("  ", false) == null && PlayerSpeechAudio.Clean("♪♪", true) == null, "no words is nothing");
        Check(PlayerSpeechAudio.Clean(" you", true) == null && PlayerSpeechAudio.Clean("Thank you.", true) == null, "Whisper's silence fillers are dropped from a quiet clip");
        Check(PlayerSpeechAudio.Clean("Bye.", false) == "Bye." && PlayerSpeechAudio.Clean("you", false) == "you", "the same words spoken clearly are kept");
        Check(PlayerSpeechAudio.Clean("(wind blowing) My name is Malik.", false) == "My name is Malik." && PlayerSpeechAudio.Clean("♪ Malik ♪", false) == "Malik", "tags and notes are stripped");
        Check(PlayerSpeechAudio.Clean("123", false) == null, "digits alone are not words");

        return new { passed = failures.Count == 0, checks = checks + " checks", failures };
    }
}
