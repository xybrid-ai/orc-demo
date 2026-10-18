using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Speech generation is separate from playback: this accepts a WAV and Rhubarb cues.
// The initial WAV was synthesized by Xybrid/Kokoro; no inference happens here.
// Owns the idle base pose and the mouth (jaw, mouth, tongue shapes). Everything else the
// character does is GatekeeperPerformer's, which runs after this and asks for snarls here.
public sealed class XybridAvatarPlayer : MonoBehaviour
{
    [Serializable] public sealed class Cue { public float start, end; public string value; }
    [Serializable] public sealed class CueFile { public Cue[] mouthCues; }
    private sealed class Binding { public SkinnedMeshRenderer mesh; public int index; public string name; }
    public AudioSource speech;
    public TextAsset mouthCues;
    public AnimationClip idle;
    public Text caption;
    public Text status;
    public Text buttonLabel;
    public Slider progress;
    public bool autoPlay = true;
    public float leadIn = 0.75f;
    public string Script = "Welcome to the tavern. I am your local guide, powered by Xybrid. My voice runs on device, and my face follows every word. Pick a character, give them a voice, and bring your world to life.";
    private readonly List<Binding> bindings = new List<Binding>();
    private Cue[] cues = Array.Empty<Cue>();
    [NonSerialized] private bool initialized;
    private bool paused, started;
    private float elapsed;
    private float anger, amusement, snarlRequest;
    public int CueCount { get { Initialize(); return cues.Length; } }
    public bool IsPlaybackPaused => paused;
    public bool IsSpeaking => started && speech != null && (speech.isPlaying || paused);
    // Audio-clock time last evaluated; -1 when no line is playing.
    public float SpeechTime { get; private set; } = -1;
    public AudioClip CurrentClip => speech != null ? speech.clip : null;
    // Snarl request (0-1) from the performance layer, applied as a capped additive mouthUpperUp.
    public float SnarlRequest { get => snarlRequest; set => snarlRequest = Mathf.Clamp01(value); }
    [Range(0, 1)] public float snarlCapSpeaking = .12f, snarlCapSilent = .3f;
    [Tooltip("Lip-sync fallback: a line with audio but no cue track opens the jaw with its loudness, up to this weight.")]
    [Range(0, 1)] public float loudnessJawOpen = .5f;
    // Loudness envelope of the current clip when it has no cues; null when cues drive the mouth.
    private float[] jawEnvelope;
    private float jawPeak, jawSmoothed;
    private readonly float[] jawSamples = new float[256];
    private const float JawHop = .01f;
    private float cuesArrivedAt = -1;
    public const float CueBlendSeconds = .15f;
    public bool UsesLoudnessJaw => jawEnvelope != null && (cuesArrivedAt < 0 || Time.realtimeSinceStartup - cuesArrivedAt < CueBlendSeconds);

    // Placeholder prosody presets; sample position still drives cues when pitch changes.
    public void SetAffect(float pitch, float volume, float anger, float amusement)
    {
        this.anger = Mathf.Clamp01(anger);
        this.amusement = Mathf.Clamp01(amusement);
        if (speech != null) { speech.pitch = Mathf.Clamp(pitch, .7f, 1.2f); speech.volume = Mathf.Clamp01(volume); }
    }

    public static bool ValidateCues(TextAsset track, AudioClip audio, out string error)
    {
        error = null;
        if (track == null) return true;
        if (audio == null) { error = "Mouth cues need an audio clip."; return false; }
        Cue[] parsed;
        try { parsed = JsonUtility.FromJson<CueFile>(track.text)?.mouthCues; }
        catch (Exception exception) { error = "Invalid mouth-cue JSON: " + exception.Message; return false; }
        if (parsed == null || parsed.Length == 0) { error = "The mouth-cue track is empty."; return false; }
        float end = 0;
        foreach (var cue in parsed)
        {
            if (cue == null || float.IsNaN(cue.start) || float.IsNaN(cue.end) || float.IsInfinity(cue.start) || float.IsInfinity(cue.end)
                || cue.start < end - .001f || cue.end <= cue.start || cue.end > audio.length + .04f
                || string.IsNullOrEmpty(cue.value) || cue.value.Length != 1 || "ABCDEFGHX".IndexOf(cue.value, StringComparison.Ordinal) < 0)
            { error = "Mouth cues must have ordered times within the audio duration and valid shapes."; return false; }
            end = cue.end;
        }
        return true;
    }

    public void PlayLine(AudioClip audio, TextAsset track, bool streaming = false)
    {
        Initialize();
        if (!ValidateCues(track, audio, out var error)) throw new ArgumentException(error);
        StopSpeech();
        if (speech == null) speech = gameObject.AddComponent<AudioSource>();
        speech.clip = audio;
        speech.loop = streaming; // Complete clauses finish once; only the packet ring loops.
        mouthCues = track;
        // A new line must never inherit the preceding line's mouth track.
        cues = track != null ? JsonUtility.FromJson<CueFile>(track.text).mouthCues : Array.Empty<Cue>();
        jawEnvelope = track == null && audio != null ? streaming ? Array.Empty<float>() : LoudnessEnvelope(audio) : null;
        jawSmoothed = 0; jawPeak = .05f; cuesArrivedAt = -1;
        if (audio != null) Replay();
    }

    // Attach only to the still-playing clip. The audio clock keeps advancing; cues are evaluated
    // at its current position while all mouth channels crossfade from the loudness jaw over 150 ms.
    public bool AttachCues(AudioClip expectedClip, TextAsset track)
    {
        if (!IsSpeaking || CurrentClip != expectedClip || track == null) return false;
        if (!ValidateCues(track, expectedClip, out var error)) throw new ArgumentException(error);
        mouthCues = track; cues = JsonUtility.FromJson<CueFile>(track.text).mouthCues;
        cuesArrivedAt = Time.realtimeSinceStartup;
        return true;
    }

    public void StopSpeech()
    {
        if (speech != null) speech.Stop();
        started = paused = false;
        EvaluateAt(-1, elapsed);
    }

    public void Initialize()
    {
        if (initialized) return;
        initialized = true;
        if (mouthCues != null) cues = JsonUtility.FromJson<CueFile>(mouthCues.text)?.mouthCues ?? Array.Empty<Cue>();
        foreach (var renderer in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.sharedMesh == null) continue;
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                string name = renderer.sharedMesh.GetBlendShapeName(i);
                int dot = name.LastIndexOf('.');
                if (dot >= 0) name = name.Substring(dot + 1);
                // Mouth shapes only. Weights are absolute (neutral 0), so a weight saved into the scene cannot become a new rest pose.
                if (name.StartsWith("mouth") || name.StartsWith("jaw") || name.StartsWith("tongue"))
                    bindings.Add(new Binding { mesh = renderer, index = i, name = name });
            }
        }
    }

    private IEnumerator Start()
    {
        Initialize();
        if (autoPlay) { yield return new WaitForSecondsRealtime(leadIn); Replay(); }
    }

    private void LateUpdate()
    {
        Initialize();
        elapsed += Time.unscaledDeltaTime;
        if (speech == null || speech.clip == null) { EvaluateAt(-1, elapsed); return; }
        float time = speech.timeSamples / (float)speech.clip.frequency;
        bool finished = started && !paused && !speech.isPlaying;
        if (finished) time = speech.clip.length;
        EvaluateAt(started ? time : -1f, elapsed);
        if (progress != null) progress.SetValueWithoutNotify(Mathf.Clamp01(time / speech.clip.length));
        if (buttonLabel != null) buttonLabel.text = speech.isPlaying ? "PAUSE" : "PLAY";
        if (status != null) status.text = speech.isPlaying ? "SPEAKING" : paused ? "PAUSED" : finished ? "READY TO REPLAY" : "READY";
    }

    public void TogglePlayback()
    {
        if (speech == null || speech.clip == null) return;
        if (speech.isPlaying) { speech.Pause(); paused = true; }
        else if (paused) { speech.UnPause(); paused = false; }
        else Replay();
    }

    public void ResumePlayback()
    {
        if (!paused || speech == null) return;
        speech.UnPause(); paused = false;
    }

    public void Replay()
    {
        if (speech == null || speech.clip == null) return;
        speech.Stop(); speech.timeSamples = 0; speech.Play(); paused = false; started = true;
    }

    public void Seek(float normalized)
    {
        if (speech == null || speech.clip == null) return;
        speech.timeSamples = Mathf.Clamp(Mathf.RoundToInt(normalized * speech.clip.samples), 0, speech.clip.samples - 1);
        if (!started) { started = true; paused = true; }
    }

    // Deterministic: playback, seeking, screenshots, and export share this mapping.
    public void EvaluateAt(float speechTime, float sceneTime)
    {
        Initialize();
        // The idle is the base pose; GatekeeperPerformer layers breathing, gaze and mood on top.
        if (idle != null && idle.length > 0) idle.SampleAnimation(gameObject, Mathf.Repeat(sceneTime, idle.length));
        SpeechTime = speechTime;
        int lo = 0, hi = cues.Length - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (speechTime < cues[mid].start) hi = mid - 1; else { found = mid; lo = mid + 1; } }
        string current = "X", previous = "X";
        float blend = 1;
        if (found >= 0 && speechTime < cues[found].end)
        {
            current = cues[found].value;
            if (found > 0 && cues[found - 1].end >= cues[found].start - .001f) previous = cues[found - 1].value;
            // 40 ms transitions retain brief consonants while avoiding snapped poses.
            blend = Mathf.SmoothStep(0, 1, Mathf.Clamp01((speechTime - cues[found].start) / .04f));
        }
        bool cueActive = speech != null && speech.clip != null && speechTime >= 0 && speechTime < speech.clip.length;
        float snarl = snarlRequest * (cueActive ? snarlCapSpeaking : snarlCapSilent);
        float jaw = cueActive ? LoudnessJaw(speechTime) : 0;
        foreach (var binding in bindings)
        {
            float cueValue = Mathf.Lerp(Pose(previous, binding.name), Pose(current, binding.name), blend);
            float cueMix = jawEnvelope == null ? 1 : cuesArrivedAt < 0 ? 0
                : Mathf.SmoothStep(0, 1, Mathf.Clamp01((Time.realtimeSinceStartup - cuesArrivedAt) / CueBlendSeconds));
            float value = Mathf.Lerp(binding.name == "jawOpen" ? jaw : 0, cueValue, cueMix);
            if (binding.name.StartsWith("mouthSmile")) value += amusement * .17f;
            if (binding.name.StartsWith("mouthFrown")) value += anger * .14f;
            if (binding.name.StartsWith("mouthUpperUp")) value += snarl;
            binding.mesh.SetBlendShapeWeight(binding.index, Mathf.Clamp(value * 100, 0, 100));
        }
        if (caption != null) caption.text = Caption(speechTime);
        if (!Application.isPlaying)
        {
            bool speaking = speech != null && speech.clip != null && speechTime >= 0 && speechTime < speech.clip.length;
            if (status != null) status.text = speaking ? "SPEAKING" : "READY";
            if (buttonLabel != null) buttonLabel.text = speaking ? "PAUSE" : "PLAY";
            if (progress != null && speech != null && speech.clip != null) progress.SetValueWithoutNotify(Mathf.Clamp01(speechTime / speech.clip.length));
        }
    }

    private float LoudnessJaw(float time)
    {
        if (jawEnvelope == null) return 0;
        float level;
        if (jawEnvelope.Length > 0)
        {
            float x = time / JawHop; int i = Mathf.Clamp(Mathf.FloorToInt(x), 0, jawEnvelope.Length - 1);
            level = i + 1 < jawEnvelope.Length ? Mathf.Lerp(jawEnvelope[i], jawEnvelope[i + 1], x - i) : jawEnvelope[i];
        }
        else
        {
            // Streamed clips cannot be read ahead; follow the live output instead.
            if (!Application.isPlaying || speech == null) return 0;
            var buffer = jawSamples; speech.GetOutputData(buffer, 0);
            double sum = 0, previous = 0, high = 0;
            double alpha = 1 / (1 + 2 * Math.PI * 120 / AudioSettings.outputSampleRate);
            foreach (float v in buffer) { high = alpha * (high + v - previous); previous = v; sum += high * high; }
            float rms = Mathf.Sqrt((float)(sum / buffer.Length));
            jawPeak = Mathf.Max(jawPeak * .998f, rms);
            level = Gate(rms / jawPeak);
        }
        jawSmoothed = Mathf.Lerp(jawSmoothed, level, 1 - Mathf.Exp(-Time.unscaledDeltaTime / (level > jawSmoothed ? .045f : .10f)));
        return loudnessJawOpen * jawSmoothed;
    }

    // RMS in 10 ms hops, opened fast and closed slower, normalised to the clip's 95th percentile
    // and gated so pauses close the mouth. Empty when the samples cannot be read.
    private static float[] LoudnessEnvelope(AudioClip clip)
    {
        if (clip.samples <= 0 || clip.channels <= 0) return Array.Empty<float>();
        var pcm = new float[clip.samples * clip.channels];
        if (!clip.GetData(pcm, 0)) return Array.Empty<float>();
        int size = Mathf.Max(1, Mathf.RoundToInt(JawHop * clip.frequency)), frames = Mathf.CeilToInt(clip.samples / (float)size);
        var envelope = new float[frames];
        float level = 0, attack = 1 - Mathf.Exp(-JawHop / .025f), release = 1 - Mathf.Exp(-JawHop / .08f);
        double previousSample = 0, filtered = 0;
        double highPass = 1 / (1 + 2 * Math.PI * 120 / clip.frequency);
        for (int f = 0; f < frames; f++)
        {
            double sum = 0; int count = 0;
            for (int i = f * size; i < Mathf.Min(clip.samples, (f + 1) * size); i++)
            {
                float mono = 0;
                for (int c = 0; c < clip.channels; c++) mono += pcm[i * clip.channels + c];
                mono /= clip.channels;
                filtered = highPass * (filtered + mono - previousSample); previousSample = mono;
                sum += filtered * filtered; count++;
            }
            float rms = count > 0 ? Mathf.Sqrt((float)(sum / count)) : 0;
            level += (rms - level) * (rms > level ? attack : release);
            envelope[f] = level;
        }
        var sorted = (float[])envelope.Clone(); Array.Sort(sorted);
        float reference = sorted[Mathf.Clamp(Mathf.RoundToInt(sorted.Length * .95f), 0, sorted.Length - 1)];
        // A low percentile estimates the continuous rumble; subtract it before normalization.
        float noise = Mathf.Max(.012f, sorted[Mathf.Clamp(Mathf.RoundToInt(sorted.Length * .15f), 0, sorted.Length - 1)]);
        for (int f = 0; f < frames; f++) envelope[f] = Gate((envelope[f] - noise) / Mathf.Max(reference - noise, 1e-4f));
        return envelope;
    }
    private static float Gate(float level) => Mathf.Clamp01((level - .30f) / .70f);

    private static float Pose(string cue, string shape)
    {
        switch (shape)
        {
            case "jawOpen":
                switch (cue) { case "B": return .13f; case "C": return .36f; case "D": return .66f; case "E": return .31f; case "F": return .17f; case "G": return .06f; case "H": return .22f; default: return 0; }
            case "mouthClose": return cue == "A" ? .8f : 0;
            case "mouthPressLeft": case "mouthPressRight": return cue == "A" ? .35f : 0;
            case "mouthStretchLeft": case "mouthStretchRight": return cue == "B" ? .18f : cue == "C" ? .22f : 0;
            case "mouthFunnel": return cue == "E" ? .72f : cue == "F" ? .25f : 0;
            case "mouthPucker": return cue == "F" ? .8f : cue == "E" ? .18f : 0;
            case "mouthRollLower": return cue == "G" ? .5f : 0;
            case "mouthUpperUpLeft": case "mouthUpperUpRight": return cue == "G" ? .22f : 0;
            case "tongueUp": return cue == "H" ? .42f : 0;
            case "tongueOut": return cue == "H" ? .15f : 0;
            default: return 0;
        }
    }

    private string Caption(float time)
    {
        if (speech == null || speech.clip == null || time < 0 || time >= speech.clip.length) return "A voice. A character. Your world.";
        return Script;
    }
}
