using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

// Vesper's voice: a second GatekeeperLiveSpeech (xn-ptts: its own handle and worker; liborc_phonon is shared with Trola
// whenever he runs xn-ptts, so she opens it at his thread count) on an active child object, played clause by clause on
// her own 2D source through an in-head filter chain. Trola's AudioSource, AudioListener.pause and mixers are never written.
// Her engine opens and synthesizes only while Trola waits (or has ended) and his worker is idle. Missing files or a
// provider error make every line subtitle-only plus a breath; Trola is unaffected. See docs/spirit.md.
public sealed class SpiritVoice : MonoBehaviour
{
    // Missing on purpose: analysis fails at File.Exists and no Rhubarb process is ever spawned for her.
    public const string NoLipSync = "Spirit/no-lipsync";
    public const string DebugFolder = "SpiritSpeechDebug";
    private const float ArmFallbackSeconds = 20, BaseVolume = .9f;
    private const int SfxRate = 32000;
    private static readonly string[] CheckpointFiles = { "config.json", "tokenizer.json", "model.q8.gguf" };
    private static readonly SpiritSound[] VoiceSounds = { SpiritSound.BreathIn, SpiritSound.Exhale, SpiritSound.Gasp, SpiritSound.Shimmer };

    [Tooltip("Seconds a line waits for her voice (or for Trola's worker to go idle) before it plays as a subtitle only. Director sets timing.voice_wait.")]
    public float voiceWait = 2;
    public GatekeeperController controller;
    public GatekeeperLiveSpeech Engine => engine;

    public bool Armed => engine != null;
    // Files present and no provider error. Before arming this only reflects the files.
    public bool Available => filesPresent && (engine == null || string.IsNullOrEmpty(engine.LastProviderError));
    public bool IsSpeaking => current != null || queue.Count > 0;
    public bool IsSilentLine { get; private set; }
    public float Envelope { get; private set; }
    public string LastFailure { get; private set; } = "";
    // Window token of the line in progress (or last queued); results of an older token are stale.
    public int Token { get; private set; }
    // Lens figures for the last voiced line: clause-0 synthesis, Say -> first clip start, and whether it was prefetched.
    public double LastSynthesisSeconds { get; private set; }
    public double LastStartSeconds { get; private set; }
    public bool LastPrefetched { get; private set; }
    public string VoiceName { get; private set; } = "";

    public event Action<SpiritCue> LineStarted;
    public event Action<SpiritCue> LineFinished;

    private sealed class Pending { public SpiritCue cue; public int token; public float requestedAt; }
    [Serializable] private sealed class Provenance { public string name = "", key = "", sha256 = ""; }

    private SpiritLines.Voice settings = new SpiritLines.Voice();
    private GatekeeperLiveSpeech engine;
    private AudioSource output, sfx;
    private readonly Queue<Pending> queue = new Queue<Pending>();
    private readonly Dictionary<string, Task<GatekeeperLiveSpeech.Generated>> prefetched = new Dictionary<string, Task<GatekeeperLiveSpeech.Generated>>(StringComparer.Ordinal);
    private readonly Dictionary<SpiritSound, AudioClip> sounds = new Dictionary<SpiritSound, AudioClip>();
    private readonly float[] meter = new float[256];
    private Pending current;
    private Coroutine player;
    private int generation, errors;
    private bool built, filesPresent, armRequested, supportedEngine, sfxBroken;
    private float armRequestedAt, fadeRate;
    private string checkpointPath = "", voicePath = "", warned = "";

    public static SpiritVoice Create(Transform parent, GatekeeperController controller, SpiritLines.Voice settings)
    {
        var host = parent != null ? parent.gameObject : new GameObject("Spirit voice");
        var voice = host.AddComponent<SpiritVoice>();
        voice.Build(controller, settings ?? new SpiritLines.Voice());
        return voice;
    }

    private void Build(GatekeeperController owner, SpiritLines.Voice voice)
    {
        controller = owner; settings = voice; built = true;
        output = MakeSource("Spirit voice output", 0);
        // In-head chain, in component order: thin the chest, soften the top, a faint double, a skull-sized room.
        var go = output.gameObject;
        go.AddComponent<AudioHighPassFilter>().cutoffFrequency = 180;
        go.AddComponent<AudioLowPassFilter>().cutoffFrequency = 7500;
        var chorus = go.AddComponent<AudioChorusFilter>();
        chorus.dryMix = .8f; chorus.wetMix1 = .25f; chorus.wetMix2 = .2f; chorus.wetMix3 = 0; chorus.delay = 16; chorus.rate = .4f; chorus.depth = .05f;
        var reverb = go.AddComponent<AudioReverbFilter>();
        reverb.reverbPreset = AudioReverbPreset.User;
        reverb.room = -1200; reverb.roomHF = -800; reverb.decayTime = 1; reverb.decayHFRatio = .5f; reverb.reflectionsLevel = -900; reverb.reverbLevel = -500;
        sfx = MakeSource("Spirit voice sfx", 16);
        sfx.volume = 1;
        supportedEngine = string.Equals(settings.engine, nameof(PhononEngine.XnPtts), StringComparison.OrdinalIgnoreCase);
        checkpointPath = Resolve(settings.checkpoint); voicePath = Resolve(settings.voice_file);
        CheckFiles();
        VoiceName = ReadVoiceName();
        if (controller != null) { controller.PlayerActed += OnPlayerActed; controller.Restarted += ResetSession; }
        StartCoroutine(PrerenderSounds());
    }

    private AudioSource MakeSource(string name, int priority)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var source = go.AddComponent<AudioSource>();
        source.playOnAwake = false; source.loop = false; source.spatialBlend = 0; source.priority = priority;
        source.bypassReverbZones = true; source.volume = BaseVolume; source.dopplerLevel = 0;
        return source;
    }

    private static string Resolve(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.IsPathRooted(path) ? path : Path.Combine(Application.streamingAssetsPath, path);

    private void CheckFiles()
    {
        filesPresent = false;
        if (!supportedEngine) { LastFailure = "unsupported spirit voice engine '" + settings.engine + "' (XnPtts only)"; return; }
        foreach (string file in CheckpointFiles)
            if (!File.Exists(Path.Combine(checkpointPath, file))) { LastFailure = "missing StreamingAssets/" + settings.checkpoint + "/" + file + "; run tools/setup_phonon.py"; return; }
        if (!File.Exists(voicePath)) { LastFailure = "missing StreamingAssets/" + settings.voice_file + "; run python3 tools/setup_spirit_voice.py"; return; }
        filesPresent = true;
    }

    // The staged voice's own name (setup_spirit_voice.py provenance) beats the JSON label after a by-ear swap.
    private string ReadVoiceName()
    {
        try
        {
            string folder = Path.GetDirectoryName(voicePath) ?? "";
            string receipt = Path.Combine(folder, Path.GetFileNameWithoutExtension(voicePath) + "-provenance.json");
            if (File.Exists(receipt))
            {
                var data = JsonUtility.FromJson<Provenance>(File.ReadAllText(receipt));
                if (data != null && !string.IsNullOrWhiteSpace(data.name)) return data.name.Trim();
            }
        }
        catch (Exception) { } // Cosmetic only.
        return string.IsNullOrWhiteSpace(settings.label) ? "spirit voice" : settings.label;
    }

    private IEnumerator PrerenderSounds()
    {
        // One sound per frame, so no frame pays for all of them.
        foreach (var sound in VoiceSounds) { Sound(sound); yield return null; }
    }

    // The engine opens at the first wait once Trola's own warm-up is done (or 20 s passed). Idempotent.
    public void Arm()
    {
        if (armRequested) return;
        armRequested = true; armRequestedAt = Time.realtimeSinceStartup;
        if (!filesPresent) Warn(LastFailure);
        TryCreateEngine();
    }

    private GatekeeperLiveSpeech Trola => controller != null ? controller.liveSpeech : null;
    private bool TrolaWaitingOrEnded => controller != null && (controller.IsWaitingForPlayer || controller.State == GatekeeperController.Phase.Ended);
    private bool TrolaWorkerIdle => Trola == null || !Trola.NativeCallRunning;
    // xn's worker pool is process-wide and sized once, at first use; it outlives Play sessions in the Editor. Whichever
    // voice builds it, it must be Trola's width, in case he runs (or is later switched to) xn-ptts in this process.
    private int Threads => Mathf.Clamp(Trola != null ? Trola.phononThreads : settings.threads, 1, 64);
    public bool CanSynthesize => Armed && Available && SpiritSignals.Enabled && TrolaWaitingOrEnded && TrolaWorkerIdle;
    // A voice is worth waiting for: present, enabled, and armed or about to be.
    private bool VoiceExpected => Available && SpiritSignals.Enabled && (Armed || armRequested);

    private void TryCreateEngine()
    {
        if (!armRequested || engine != null || !built || !filesPresent || !SpiritSignals.Enabled || !gameObject.activeInHierarchy) return;
        if (!TrolaWaitingOrEnded || !TrolaWorkerIdle) return;
        var trola = Trola;
        if (trola != null && !trola.WarmCompleted && Time.realtimeSinceStartup - armRequestedAt < ArmFallbackSeconds) return;
        // Active child: Awake runs inside AddComponent (reading any -xybridSpeech / -xybridVoiceFault flags), then our
        // values override them. Created inactive, a later activation would rerun Awake after this configuration.
        var host = new GameObject("Spirit voice engine");
        host.transform.SetParent(transform, false);
        var speech = host.AddComponent<GatekeeperLiveSpeech>();
        speech.startWithKitten = false; speech.startWithPhonon = true;
        speech.provider = GatekeeperLiveSpeech.Provider.Phonon; speech.phononEngine = PhononEngine.XnPtts;
        speech.phononCheckpoint = settings.checkpoint; speech.phononVoiceFile = settings.voice_file;
        speech.phononThreads = Threads; speech.preparationTimeout = Mathf.Max(1, settings.preparation_timeout);
        speech.rhubarbExecutable = NoLipSync;
        speech.simulatedFault = GatekeeperLiveSpeech.Fault.None; speech.debugFolder = DebugFolder;
        engine = speech; // Start -> Warm opens the provider on its worker next frame.
    }

    public void Prefetch(SpiritCue cue)
    {
        TryCreateEngine(); // A checkpoint's Changed can arrive before this frame's Update.
        if (cue == null || string.IsNullOrWhiteSpace(cue.Text) || !CanSynthesize) return;
        string text = cue.Text.Trim();
        if (prefetched.ContainsKey(text)) return;
        try { prefetched[text] = engine.Prepare(text); }
        catch (Exception error) { LastFailure = "prefetch failed: " + error.Message; }
    }

    // All clauses in order on her source. A different token cuts the line in progress; the same token queues behind it.
    public void Say(SpiritCue cue, int token)
    {
        if (!built || cue == null || string.IsNullOrWhiteSpace(cue.Text)) return;
        if (IsSpeaking && token != Token) Stop(.05f);
        Token = token;
        queue.Enqueue(new Pending { cue = cue, token = token, requestedAt = Time.realtimeSinceStartup });
        if (player == null) player = StartCoroutine(Play(generation));
    }

    private IEnumerator Play(int session)
    {
        while (session == generation && queue.Count > 0)
        {
            current = queue.Dequeue();
            var cue = current.cue;
            string text = cue.Text.Trim(), failure = null;
            float requested = current.requestedAt;
            var task = Take(text);
            LastPrefetched = task != null;
            if (task == null && VoiceExpected)
            {
                // Her synthesis starts only while Trola waits and his worker is idle.
                while (session == generation && !CanSynthesize && VoiceExpected && Time.realtimeSinceStartup - requested < voiceWait) yield return null;
                if (session != generation) yield break;
                if (CanSynthesize)
                {
                    try { task = engine.Prepare(text, true); }
                    catch (Exception error) { failure = "synthesis failed: " + error.Message; }
                }
            }
            GatekeeperLiveSpeech.Generated first = null;
            if (task != null)
            {
                while (session == generation && !task.IsCompleted && Time.realtimeSinceStartup - requested < voiceWait) yield return null;
                if (session != generation) yield break;
                if (!task.IsCompleted) failure = "voice not ready after " + voiceWait.ToString("0.0") + " s";
                else if (task.IsFaulted || task.IsCanceled) failure = "synthesis failed: " + (task.Exception?.GetBaseException().Message ?? "cancelled");
                else
                {
                    try { first = engine.Materialize(task); }
                    catch (Exception error) { failure = "playback failed: " + error.Message; }
                }
            }
            else if (failure == null) failure = !Available ? LastFailure : !SpiritSignals.Enabled ? "spirit off" : !Armed ? "voice not armed yet" : "Trola's voice was busy";
            if (failure != null)
            {
                yield return Silent(cue, failure, session);
                if (session != generation) yield break;
                continue;
            }
            LastSynthesisSeconds = first.synthesisSeconds;
            LastStartSeconds = Time.realtimeSinceStartup - requested;
            float lineStarted = Time.realtimeSinceStartup;
            PlayClip(first.clip);
            Raise(LineStarted, cue);
            if (session != generation) yield break;
            int count = first.clauses?.Length ?? 1;
            for (int i = 0; ; i++)
            {
                while (session == generation && (output.isPlaying || AudioListener.pause)) yield return null;
                if (session != generation) yield break;
                if (i + 1 >= count) break;
                var next = first.clauses[i + 1];
                float deadline = Time.realtimeSinceStartup + voiceWait;
                while (session == generation && !next.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                if (session != generation) yield break;
                GatekeeperLiveSpeech.Generated clause = null;
                if (next.Status == TaskStatus.RanToCompletion)
                    try { clause = engine.Materialize(next); } catch (Exception error) { Warn("clause failed: " + error.Message); }
                if (clause == null)
                {
                    // Keep the text readable; drop the rest of the audio.
                    if (next.Status != TaskStatus.RanToCompletion) Warn("clause " + (i + 2) + " of '" + cue.LineId + "' not ready");
                    float until = lineStarted + ReadableSeconds(text);
                    while (session == generation && Time.realtimeSinceStartup < until) yield return null;
                    if (session != generation) yield break;
                    break;
                }
                PlayClip(clause.clip);
            }
            current = null;
            Raise(LineFinished, cue);
        }
        if (session == generation) { player = null; current = null; }
    }

    private Task<GatekeeperLiveSpeech.Generated> Take(string text)
    {
        if (!prefetched.TryGetValue(text, out var task)) return null;
        prefetched.Remove(text);
        return task.IsCanceled ? null : task;
    }

    // Subtitle-only line: the director shows the text on LineStarted; this only holds the time and breathes.
    private IEnumerator Silent(SpiritCue cue, string failure, int session)
    {
        LastFailure = failure;
        float hold = ReadableSeconds(cue.Text);
        Warn(failure + "; her lines show as subtitles"); // No per-line hold in the text: one warning per failure.
        IsSilentLine = true;
        PlaySfx(SpiritSound.Exhale, .45f);
        Raise(LineStarted, cue);
        float until = Time.realtimeSinceStartup + hold;
        while (session == generation && Time.realtimeSinceStartup < until) yield return null;
        if (session != generation) yield break;
        IsSilentLine = false; current = null;
        Raise(LineFinished, cue);
    }

    public static float ReadableSeconds(string text) => Mathf.Clamp(1.2f + (text ?? "").Length / 15f, 2, 4.5f);

    private void PlayClip(AudioClip clip)
    {
        fadeRate = 0; output.Stop();
        output.volume = BaseVolume; output.clip = clip; output.Play();
    }

    public void PlaySfx(SpiritSound sound, float volume)
    {
        if (!built || !SpiritSignals.Enabled || volume <= 0) return;
        var clip = Sound(sound);
        if (clip != null) sfx.PlayOneShot(clip, Mathf.Clamp01(volume));
    }

    private AudioClip Sound(SpiritSound sound)
    {
        if (sounds.TryGetValue(sound, out var clip)) return clip;
        if (sfxBroken) return null;
        try
        {
            var data = ProceduralAudio.Render(sound, 7919 + (int)sound * 104729, SfxRate);
            if (data != null && data.Length > 0)
            {
                clip = AudioClip.Create("spirit_" + sound, data.Length, 1, SfxRate, false);
                clip.SetData(data, 0);
            }
        }
        catch (Exception error) { sfxBroken = true; Warn("procedural sounds unavailable: " + error.Message); }
        sounds[sound] = clip;
        return clip;
    }

    // Fade, cancel her pending synthesis and end any subtitle hold. Lines end silently: no LineFinished.
    public void Stop(float fadeSeconds)
    {
        generation++;
        if (player != null) { StopCoroutine(player); player = null; }
        queue.Clear(); current = null; IsSilentLine = false;
        if (engine != null) engine.InterruptPending();
        prefetched.Clear();
        if (output == null) return;
        if (output.isPlaying && fadeSeconds > 0) fadeRate = Mathf.Max(.01f, output.volume) / fadeSeconds;
        else { fadeRate = 0; output.Stop(); output.volume = BaseVolume; }
    }

    public void ResetSession()
    {
        Stop(0);
        if (engine != null) engine.ResetSession(); // Destroys her materialized clips.
        if (sfx != null) sfx.Stop();
    }

    private void OnPlayerActed(GatekeeperInputKind kind, string label)
    {
        // Any player act cancels her pending synthesis; Trola is about to need the CPU.
        if (IsSpeaking || prefetched.Count > 0) Stop(.12f);
        else if (engine != null) engine.InterruptPending();
    }

    private void Update()
    {
        if (!built) return;
        try { Tick(); }
        catch (Exception error) { if (++errors <= 3) Debug.LogException(error, this); }
    }

    private void Tick()
    {
        TryCreateEngine();
        // The golden rule, enforced here too: she is never heard unless Trola waits or has ended.
        if (IsSpeaking && (!SpiritSignals.Enabled || !TrolaWaitingOrEnded)) Stop(.12f);
        // Her sounds never cover his voice (the flinch gasp is cut the moment he speaks).
        if (sfx.isPlaying && controller != null && controller.State == GatekeeperController.Phase.Speaking) sfx.Stop();
        if (fadeRate > 0)
        {
            output.volume = Mathf.MoveTowards(output.volume, 0, fadeRate * Time.unscaledDeltaTime);
            if (output.volume <= 0 || !output.isPlaying) { output.Stop(); output.volume = BaseVolume; fadeRate = 0; }
        }
        output.panStereo = .15f * Mathf.Sin(2 * Mathf.PI * .08f * Time.realtimeSinceStartup);
        float level = 0;
        if (output.isPlaying && !AudioListener.pause)
        {
            output.GetOutputData(meter, 0);
            double sum = 0;
            foreach (float s in meter) sum += s * s;
            level = Mathf.Clamp01((float)Math.Sqrt(sum / meter.Length) / Mathf.Max(.05f, output.volume) * 4);
        }
        float rate = level > Envelope ? 30 : 8;
        Envelope = Mathf.Lerp(Envelope, level, 1 - Mathf.Exp(-rate * Time.unscaledDeltaTime));
        SpiritSignals.SpiritAudible = output.isPlaying || IsSilentLine || sfx.isPlaying;
        if (engine != null && !string.IsNullOrEmpty(engine.LastProviderError)) LastFailure = "provider: " + engine.LastProviderError;
    }

    private void Warn(string message)
    {
        // One warning per distinct failure: a missing voice must not flood the Console line after line.
        if (string.IsNullOrEmpty(message) || message == warned) return;
        warned = message;
        Debug.LogWarning("[Spirit voice] " + message, this);
    }

    private void Raise(Action<SpiritCue> handlers, SpiritCue cue)
    {
        if (handlers == null) return;
        foreach (Action<SpiritCue> handler in handlers.GetInvocationList())
            try { handler(cue); } catch (Exception error) { Debug.LogException(error, this); }
    }

    public string Describe()
    {
        string engineText = "xn-ptts 0.6.0; " + (engine != null ? engine.phononThreads : Threads) + " threads";
        if (!Available) return "subtitles only (" + (string.IsNullOrEmpty(LastFailure) ? "voice unavailable" : LastFailure) + ")";
        string state = engine == null ? (armRequested ? " (opens at Trola's next wait)" : " (not armed)")
            : !engine.WarmCompleted ? " (warming)" : "";
        string last = !string.IsNullOrEmpty(LastFailure) && engine != null && engine.WarmCompleted ? "; last: " + LastFailure : "";
        return "live Phonon / " + VoiceName + " / " + engineText + state + last;
    }

    private void OnDisable()
    {
        if (!built) return;
        Stop(0);
        if (sfx != null) sfx.Stop();
        Envelope = 0; SpiritSignals.SpiritAudible = false;
    }

    private void OnDestroy()
    {
        if (controller != null) { controller.PlayerActed -= OnPlayerActed; controller.Restarted -= ResetSession; }
        foreach (var clip in sounds.Values) if (clip != null) Destroy(clip);
        sounds.Clear();
    }
}
