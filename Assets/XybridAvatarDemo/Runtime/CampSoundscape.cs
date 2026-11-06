using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

// The camp around the gate: hearth, torches, campfire, wind, crickets, camp life, laughter, the gate
// creak, and tension music (a drone and a war drum). It installs on its own: -xybridNoSpirit keeps it, while
// -xybridNoAmbience, -xybridSelfTest and -xybridSpeechTest skip it, and a self-test or speech validation found later
// shuts it down. ProceduralAudio renders every clip on a worker thread; the clips become AudioClips one per frame.
// It only reads the gatekeeper (state, patience, outcome, whether Trola's source is playing) and SpiritSignals: it never
// writes Trola's AudioSource, AudioListener.pause or any mixer. Mix rules live in CampMix. F8 mutes ambience and music.
public sealed class CampSoundscape : MonoBehaviour
{
    public GatekeeperController controller;
    public static CampSoundscape Instance { get; private set; }
    public bool Muted { get; private set; }
    public static string Describe() => Instance != null ? Instance.Summary() : "Ambience: off";

    // World positions checked against GoblinSpeakingDemo.unity (Village Gate Set at the origin).
    private static readonly Vector3 HearthAt = new Vector3(-1, .4f, 2.2f), CampfireAt = new Vector3(1, .25f, -10), GateAt = new Vector3(0, 1, -5.2f);
    private static readonly Vector3[] TorchAt = { new Vector3(-2.22f, 2.38f, -3.95f), new Vector3(2.22f, 2.38f, -3.95f) };
    private static readonly Vector3[] TentAt = { new Vector3(-3.7f, 0, -12), new Vector3(3.7f, 0, -12) };
    private static readonly string[] ReactionLaughs = { "heh" };   // "Hmph." and "Hmm?" are a snort and a question, not laughs
    private const double DrumLead = .12;                        // PlayScheduled this far ahead
    private const int DrumVoices = 7;                           // three ringing war-pattern hits, a flinch and Refused's three

    private enum Bus { HearthCalm, HearthRoar, Torch, Campfire, Wind, Crickets, Drone, Resolve, Shimmer }
    private sealed class Loop
    {
        public ProceduralAudio.LoopSpec spec; public Bus bus; public AudioSource source; public AudioLowPassFilter filter;
        public AudioClip clip; public float ready; public bool paused;
    }
    private sealed class Voice { public AudioSource source; public AudioLowPassFilter filter; public float gain; }
    // Worker-thread output: plain samples only; no Unity object is touched off the main thread.
    private sealed class Rendered { public ProceduralAudio.LoopSpec spec; public SpiritSound sound; public int seed; public float[] samples; public string error; }

    private TrolaAtmosphere atmosphere;
    private GatekeeperBarrier barrier;
    private readonly List<Loop> loops = new List<Loop>();
    private readonly Dictionary<SpiritSound, List<AudioClip>> shots = new Dictionary<SpiritSound, List<AudioClip>>();
    private readonly List<AudioClip> owned = new List<AudioClip>(), laughs = new List<AudioClip>();
    private readonly ConcurrentQueue<Rendered> rendered = new ConcurrentQueue<Rendered>();
    private readonly XorShift32 dice = new XorShift32((uint)Environment.TickCount);
    private Voice[] drums, events, laughVoices;
    private Voice gate;
    private readonly float[] drumVelocity = new float[DrumVoices];
    private readonly double[] drumEnd = new double[DrumVoices]; // dspTime each drum voice falls silent
    private volatile bool cancelled;
    private bool userLaughs, spiritVoice, success, drumStopped, campStopped, gateWasOpen, resyncGate;
    private int patience = 3, errors, drumStep;
    private double nextStepAt;
    private float tension = CampMix.Tension(3), fire = 1, moment, duck, master = 1, cricketLevel, resolveLevel, musicEnd = 1, laughGate = 1;
    private float drumLevel, eventLevel, laughLevel, worldLevel = 1;
    // The road approach (docs/approach.md): music waits for Trola's challenge, and the whole camp follows the director's
    // fades (from black, and the dip that hides a skip's cut), read-only like SpiritSignals.
    private float musicHold, faded = 1;
    private float nextEventAt, campPausedUntil, startledUntil, trolaHeardAt = -10, nextInertCheck, musicFadeAt = -1, musicFadeSeconds = 8, bigLaughAt = -1;
    private GatekeeperOutcome outcome;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (HasArgument("-xybridNoAmbience") || HasArgument("-xybridSelfTest") || HasArgument("-xybridSpeechTest") || Instance != null) return;
        var controller = FindAnyObjectByType<GatekeeperController>();
        if (controller == null || UnderTest()) return;
        new GameObject("Camp soundscape (runtime)").AddComponent<CampSoundscape>().controller = controller;
    }
    private static bool HasArgument(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    private static bool UnderTest() => FindAnyObjectByType<GatekeeperSelfTest>() != null || FindAnyObjectByType<GatekeeperSpeechValidation>() != null;

    private void Awake() { Instance = this; }
    private void Start()
    {
        if (controller == null) { Destroy(gameObject); return; }
        atmosphere = FindAnyObjectByType<TrolaAtmosphere>();
        barrier = FindAnyObjectByType<GatekeeperBarrier>();
        gateWasOpen = barrier != null && barrier.IsOpen;
        outcome = controller.Outcome;
        patience = controller.Patience; tension = CampMix.Tension(patience); // An approach may have started the encounter first.
        controller.PatienceChanged += OnPatience;
        controller.Restarted += OnRestarted;
        Subscribe();
        Build();
        LoadLaughs();
        Render();
        nextEventAt = Time.realtimeSinceStartup + dice.Range(3.5f, 9);
    }
    private void OnDestroy()
    {
        cancelled = true;
        if (controller != null) { controller.PatienceChanged -= OnPatience; controller.Restarted -= OnRestarted; }
        SpiritSignals.Flinched -= OnFlinch;
        foreach (var clip in owned) if (clip != null) Destroy(clip);
        if (Instance == this) Instance = null;
    }
    // SpiritDirector's SpiritSignals.Reset() clears every Flinched handler; install order is not defined, so re-subscribe.
    private void Subscribe() { SpiritSignals.Flinched -= OnFlinch; SpiritSignals.Flinched += OnFlinch; }
    private void Shutdown() { cancelled = true; Destroy(gameObject); }

    private void Build()
    {
        foreach (var spec in ProceduralAudio.CampLoops)
        {
            var bus = spec.Layer switch
            {
                "hearth" => spec.Fire < 1 ? Bus.HearthCalm : Bus.HearthRoar, "torch" => Bus.Torch, "campfire" => Bus.Campfire, "wind" => Bus.Wind,
                "drone" => Bus.Drone, "resolve" => Bus.Resolve, "shimmer" => Bus.Shimmer, _ => Bus.Crickets
            };
            bool spatial = bus is Bus.HearthCalm or Bus.HearthRoar or Bus.Torch or Bus.Campfire;
            var at = bus == Bus.Torch ? TorchAt[spec.Name.EndsWith("east") ? 1 : 0] : bus == Bus.Campfire ? CampfireAt : HearthAt;
            float minDistance = bus == Bus.Torch ? 3 : bus == Bus.Campfire ? 5 : 1.5f;
            int priority = bus switch { Bus.Drone or Bus.Resolve or Bus.Shimmer => 160, Bus.HearthCalm or Bus.HearthRoar => 180, Bus.Wind => 185, Bus.Torch => 190, Bus.Campfire => 200, _ => 210 };
            float pan = spec.Name.EndsWith("left") ? -.4f : spec.Name.EndsWith("right") ? .4f : 0;
            var source = Source(spec.Name, spatial, at, minDistance, priority, true, pan);
            loops.Add(new Loop { spec = spec, bus = bus, source = source, filter = bus == Bus.Shimmer ? null : Lowpass(source) });
        }
        drums = Pool("War drum", DrumVoices, false, Vector3.zero, 1, 160, false);
        events = Pool("Camp life", 2, true, TentAt[0], 4, 200, false);
        laughVoices = Pool("Camp laughter", 3, true, TentAt[0], 4, 205, true);
        Vector3 gateAt = GateAt;
        if (barrier != null && barrier.hinge != null)
        {
            var middle = barrier.secondHinge != null ? (barrier.hinge.position + barrier.secondHinge.position) / 2 : barrier.hinge.position;
            gateAt = new Vector3(middle.x, GateAt.y, middle.z);
        }
        gate = Pool("Gate creak", 1, true, gateAt, 3, 170, false)[0];
    }
    private AudioSource Source(string name, bool spatial, Vector3 at, float minDistance, int priority, bool loop, float pan)
    {
        var child = new GameObject(name);
        child.transform.SetParent(transform, false); child.transform.position = at;
        var source = child.AddComponent<AudioSource>();
        source.playOnAwake = false; source.loop = loop; source.volume = 0; source.priority = priority; source.dopplerLevel = 0; source.panStereo = pan;
        source.spatialBlend = spatial ? 1 : 0; source.rolloffMode = AudioRolloffMode.Logarithmic; source.minDistance = minDistance; source.maxDistance = 60;
        return source;
    }
    private static AudioLowPassFilter Lowpass(AudioSource source)
    {
        var filter = source.gameObject.AddComponent<AudioLowPassFilter>();
        filter.cutoffFrequency = 22000; filter.lowpassResonanceQ = 1;
        return filter;
    }
    private Voice[] Pool(string name, int count, bool spatial, Vector3 at, float minDistance, int priority, bool reverb)
    {
        var pool = new Voice[count];
        for (int i = 0; i < count; i++)
        {
            var source = Source(name + " " + (i + 1), spatial, at, minDistance, priority, false, 0);
            pool[i] = new Voice { source = source, filter = Lowpass(source) };
            if (reverb) source.gameObject.AddComponent<AudioReverbFilter>().reverbPreset = AudioReverbPreset.Forest;
        }
        return pool;
    }
    // User clips Resources/CampAmbience/laugh_* win; otherwise Trola's local "heh" reaction, pitched down into a crowd.
    private void LoadLaughs()
    {
        foreach (var clip in Resources.LoadAll<AudioClip>("CampAmbience"))
            if (clip != null && clip.name.StartsWith("laugh_", StringComparison.OrdinalIgnoreCase)) laughs.Add(clip);
        userLaughs = laughs.Count > 0;
        if (!userLaughs)
            foreach (string id in ReactionLaughs) { var clip = Resources.Load<AudioClip>("GatekeeperReactions/" + id); if (clip != null) laughs.Add(clip); }
        foreach (var clip in laughs) clip.LoadAudioData();
    }
    private void Render()
    {
        var specs = ProceduralAudio.CampLoops;
        var sounds = new[] { (SpiritSound.DrumHit, 1), (SpiritSound.GateCreak, 1), (SpiritSound.Hammer, 2), (SpiritSound.Clank, 2), (SpiritSound.Thud, 2), (SpiritSound.Clatter, 2) };
        Task.Run(() =>
        {
            foreach (var spec in specs)
            {
                if (cancelled) return;
                rendered.Enqueue(Try(() => ProceduralAudio.RenderLoop(spec.Layer, spec.Seed, spec.Rate, spec.Seconds, spec.Fire), new Rendered { spec = spec }));
            }
            foreach (var (sound, variants) in sounds)
                for (int seed = 1; seed <= variants; seed++)
                {
                    if (cancelled) return;
                    int s = seed;
                    rendered.Enqueue(Try(() => ProceduralAudio.Render(sound, s, ProceduralAudio.CampOneShotRate), new Rendered { sound = sound, seed = s }));
                }
        });
    }
    private static Rendered Try(Func<float[]> render, Rendered into)
    {
        try { into.samples = render(); } catch (Exception error) { into.error = error.Message; }
        return into;
    }
    private void Materialize(Rendered r)
    {
        string name = r.spec != null ? r.spec.Name : r.sound + " " + r.seed;
        if (r.samples == null || r.samples.Length == 0) { Debug.LogWarning("[Camp soundscape] Could not render " + name + ": " + r.error, this); return; }
        var clip = AudioClip.Create("Camp " + name, r.samples.Length, 1, r.spec != null ? r.spec.Rate : ProceduralAudio.CampOneShotRate, false);
        clip.SetData(r.samples, 0);
        owned.Add(clip);
        if (r.spec != null)
        {
            var loop = loops.Find(l => l.spec == r.spec);
            loop.clip = clip; loop.source.clip = clip; loop.source.timeSamples = dice.Range(0, r.samples.Length);
        }
        else
        {
            if (!shots.TryGetValue(r.sound, out var list)) shots[r.sound] = list = new List<AudioClip>();
            list.Add(clip);
        }
    }

    private void Update()
    {
        if (cancelled) return;
        try { Tick(); }
        catch (Exception error)
        {
            // Warnings only: the gatekeeper's runs expect a clean Console, and Trola never depends on the camp.
            Debug.LogWarning("[Camp soundscape] " + error.Message, this);
            if (++errors >= 3) { Debug.LogWarning("[Camp soundscape] Disabled for this session after repeated errors.", this); Shutdown(); }
        }
    }
    private void Tick()
    {
        float now = Time.realtimeSinceStartup, dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
        if (controller == null) { Shutdown(); return; }
        if (now >= nextInertCheck)
        {
            nextInertCheck = now + .5f;
            if (UnderTest()) { Shutdown(); return; }
            Subscribe();
        }
        if (rendered.TryDequeue(out var r)) Materialize(r);
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f8Key.wasPressedThisFrame)
        {
            Muted = !Muted;
            Debug.Log("[Camp soundscape] " + (Muted ? "Ambience and music muted (F8)." : "Ambience and music on (F8)."), this);
        }
        Sense(now, dt);
        Endings(now);
        Gate();
        Mix(dt);
        Drum();
        CampLife(now);
        Laughter(now, dt);
    }
    private void Sense(float now, float dt)
    {
        bool spirit = SpiritSignals.Enabled;                     // F7 off: a stale weight must not keep the world muffled
        moment = spirit ? Mathf.Clamp01(SpiritSignals.MomentWeight) : 0;
        spiritVoice = spirit && SpiritSignals.SpiritAudible;
        var speech = controller.character != null ? controller.character.speech : null;
        if (speech != null && speech.isPlaying) trolaHeardAt = now;   // read only
        float speaking = controller.State == GatekeeperController.Phase.Speaking || spiritVoice ? 1 : 0;
        duck = Mathf.MoveTowards(duck, speaking, dt / (speaking > duck ? .08f : .6f));
        master = Mathf.MoveTowards(master, Muted ? 0 : 1, dt / .25f);
        var director = ExperienceDirector.Instance;
        faded = 1 - (director != null ? Mathf.Clamp01(director.FadeAlpha) : 0);
        musicHold = Mathf.MoveTowards(musicHold, controller.IsOpening ? 0 : 1, dt / (controller.IsOpening ? .5f : 1.5f));
        tension = CampMix.SmoothTension(tension, patience, dt);   // about 2 s to settle
        fire = atmosphere != null ? atmosphere.PatienceFireScale : Mathf.MoveTowards(fire, CampMix.FireScale(patience), dt * 2);
        float crickets = campStopped || now < startledUntil ? 0 : CampMix.CricketLevel(patience, success);
        cricketLevel = Mathf.MoveTowards(cricketLevel, crickets, dt * (crickets >= cricketLevel ? .013f : now < startledUntil ? .3f : .02f));
        resolveLevel = Mathf.MoveTowards(resolveLevel, success ? 1 : 0, dt / 1.5f);
        if (musicFadeAt < 0) musicEnd = Mathf.MoveTowards(musicEnd, 1, dt / 2);
        else if (now >= musicFadeAt) musicEnd = Mathf.MoveTowards(musicEnd, 0, dt / musicFadeSeconds);
    }
    private void OnPatience(int previous, int current) { patience = current; }
    private void OnRestarted()
    {
        outcome = GatekeeperOutcome.Playing; success = drumStopped = campStopped = false;
        musicFadeAt = bigLaughAt = -1; startledUntil = campPausedUntil = 0;
        nextEventAt = Time.realtimeSinceStartup + dice.Range(3.5f, 9);
        // A new encounter starts from its own state, not the last ending's: Restarted follows PatienceChanged, so
        // patience is already the new encounter's. Scheduled ending hits and ringing one-shots stop.
        tension = CampMix.Tension(patience); cricketLevel = CampMix.CricketLevel(patience, false); resolveLevel = 0;
        Array.Clear(drumEnd, 0, drumEnd.Length); Array.Clear(drumVelocity, 0, drumVelocity.Length);
        foreach (var pool in new[] { drums, events, laughVoices }) if (pool != null) foreach (var v in pool) v.source.Stop();
        if (gate != null) gate.source.Stop();
        resyncGate = true; // Doors snapped shut by a new approach must not creak (Gate, next Update, after Changed).
    }
    private void OnFlinch(bool aggressive)
    {
        try
        {
            if (cancelled) return;
            // One drum hit and the camp holds its breath: no camp life, laughter or crickets for 6 s.
            ScheduleHit(AudioSettings.dspTime + .03, 1);
            campPausedUntil = startledUntil = Time.realtimeSinceStartup + 6;
        }
        catch (Exception error) { Debug.LogWarning("[Camp soundscape] " + error.Message, this); }
    }
    // Outcome is set once the ending lines are done; Passed and Liked flip it as the gate starts to open.
    private void Endings(float now)
    {
        var current = controller.Outcome;
        if (current == outcome) return;
        outcome = current;
        if (current == GatekeeperOutcome.Playing) return;
        drumStopped = true;
        if (current == GatekeeperOutcome.Refused)
        {
            double at = AudioSettings.dspTime + .1;
            for (int i = 0; i < 3; i++) ScheduleHit(at + i * .45, .8f + .1f * i);
            musicFadeAt = now + 1.3f; musicFadeSeconds = 2.5f; campStopped = true;   // then wind (and fire) only
        }
        else
        {
            success = true; musicFadeAt = now + 1.5f; musicFadeSeconds = 8;            // F#3 resolves the drone, then it fades
            if (current == GatekeeperOutcome.Liked) bigLaughAt = now + 1.6f;
        }
    }
    private void Gate()
    {
        if (barrier == null) return;
        bool open = barrier.IsOpen;
        if (resyncGate) { resyncGate = false; if (barrier.AtRest) { gateWasOpen = open; return; } }
        if (open == gateWasOpen) return;
        gateWasOpen = open;
        if (!shots.TryGetValue(SpiritSound.GateCreak, out var clips) || clips.Count == 0) return;
        gate.source.clip = clips[0]; gate.source.pitch = open ? 1 : .9f; gate.gain = CampMix.Gate * (open ? 1 : .7f);
        gate.source.volume = gate.gain * worldLevel;
        gate.source.Play();
    }

    private void Mix(float dt)
    {
        float world = CampMix.WorldGain(moment), cutoff = CampMix.WorldCutoff(moment), music = CampMix.MusicGain(moment) * musicEnd * musicHold;
        float output = master * faded;
        float ambience = Mathf.Lerp(1, CampMix.SpeakingAmbience, duck), fireDuck = Mathf.Lerp(1, CampMix.SpeakingFire, duck);
        float musicDuck = Mathf.Lerp(1, CampMix.SpeakingMusic, duck), roar = CampMix.RoarMix(fire), droneCutoff = CampMix.DroneCutoff(tension, moment);
        foreach (var loop in loops)
        {
            float gain, cut = cutoff;
            switch (loop.bus)
            {
                case Bus.HearthCalm: gain = CampMix.Hearth * fire * Mathf.Cos(roar * Mathf.PI / 2) * fireDuck * world; break;
                case Bus.HearthRoar: gain = CampMix.Hearth * fire * Mathf.Sin(roar * Mathf.PI / 2) * fireDuck * world; break;
                case Bus.Torch: gain = CampMix.Torch * fire * fireDuck * world; break;
                case Bus.Campfire: gain = CampMix.Campfire * fire * fireDuck * world; break;
                case Bus.Wind: gain = CampMix.Wind * ambience * world; break;
                case Bus.Crickets: gain = cricketLevel * ambience * world; break;
                case Bus.Drone: gain = CampMix.DroneGain(tension) * musicDuck * music; cut = droneCutoff; break;
                case Bus.Resolve: gain = CampMix.Resolve * resolveLevel * musicDuck * music; cut = droneCutoff; break;
                default: gain = CampMix.Shimmer * moment; break;            // her presence, even when her voice fails
            }
            Play(loop, gain * output, cut, dt);
        }
        drumLevel = CampMix.Drum * musicDuck * music * output;
        float drumCutoff = CampMix.MusicCutoff(moment);
        for (int i = 0; i < drums.Length; i++) { drums[i].source.volume = drumVelocity[i] * drumLevel; Cutoff(drums[i].filter, drumCutoff); }
        eventLevel = ambience * world * output;
        worldLevel = world * output;
        laughLevel = laughGate * worldLevel;
        foreach (var v in events) { v.source.volume = v.gain * eventLevel; Cutoff(v.filter, cutoff); }
        foreach (var v in laughVoices) { v.source.volume = v.gain * laughLevel; Cutoff(v.filter, Mathf.Min(1400, cutoff)); }
        gate.source.volume = gate.gain * worldLevel; Cutoff(gate.filter, cutoff);
    }
    // Silent loops are paused, so the camp uses only the voices it hears (at most 16 of Unity's 32).
    private static void Play(Loop loop, float gain, float cutoff, float dt)
    {
        if (loop.clip == null) return;
        loop.ready = Mathf.MoveTowards(loop.ready, 1, dt / 2);   // each layer fades in as it arrives
        gain *= loop.ready;
        var source = loop.source;
        source.volume = gain;
        if (loop.filter != null) Cutoff(loop.filter, cutoff);
        if (gain > 1e-4f)
        {
            if (!source.isPlaying) { if (loop.paused) source.UnPause(); else source.Play(); loop.paused = false; }
        }
        else if (source.isPlaying) { source.Pause(); loop.paused = true; }
    }
    private static void Cutoff(AudioLowPassFilter filter, float hz) { if (Mathf.Abs(filter.cutoffFrequency - hz) > 1) filter.cutoffFrequency = hz; }

    // Sixteenth-note grid at 54 + 36T bpm; each hit is scheduled DrumLead ahead on a free pooled source. Mind moments
    // and endings skip hits but keep the grid, so the beat comes back in time.
    private void Drum()
    {
        double now = AudioSettings.dspTime;
        // First use or a hitch: re-grid instead of firing late. An output device change restarts the audio system and
        // dspTime with it, leaving the grid far ahead (normally it is at most DrumLead + one step, .4 s): re-grid too.
        if (nextStepAt < now || nextStepAt > now + 1) nextStepAt = now + .05;
        bool allowed = !drumStopped && moment < .05f && !controller.IsOpening; // The heartbeat starts with Trola's challenge.
        for (int guard = 0; nextStepAt <= now + DrumLead && guard < 8; guard++)
        {
            float hit = allowed ? CampMix.DrumStep(tension, drumStep) : 0;
            if (hit > 0) ScheduleHit(nextStepAt, hit, false);
            drumStep = (drumStep + 1) & 15;
            nextStepAt += 15.0 / CampMix.Bpm(tension);
        }
    }
    // PlayScheduled on a source that is still sounding stops it when called, not at `at`, and the cut 40 Hz tail clicks.
    // So a hit takes a voice that is silent now; if none is, a grid hit is skipped and a flinch or ending hit (must)
    // takes the voice that ends first. An end more than 3 s ahead is stale (the audio system restarted; see Drum).
    private void ScheduleHit(double at, float velocity, bool must = true)
    {
        if (drums == null || !shots.TryGetValue(SpiritSound.DrumHit, out var clips) || clips.Count == 0) return;
        double now = AudioSettings.dspTime;
        int i = -1, first = 0;
        for (int k = 0; k < drums.Length; k++)
        {
            if (drumEnd[k] <= now || drumEnd[k] > now + 3) { i = k; break; }
            if (drumEnd[k] < drumEnd[first]) first = k;
        }
        if (i < 0) { if (!must) return; i = first; }
        drumEnd[i] = at + clips[0].length;
        drumVelocity[i] = velocity;
        var source = drums[i].source;
        source.clip = clips[0]; source.volume = velocity * drumLevel;
        source.PlayScheduled(at);
    }

    private void CampLife(float now)
    {
        if (now < nextEventAt) return;
        nextEventAt = now + dice.Range(3.5f, 9);
        if (campStopped || now < campPausedUntil || moment > .05f) return;
        float laugh = laughs.Count > 0 && CanLaugh(now) ? CampMix.LaughWeight(patience) : 0, total = laugh;
        foreach (var e in CampMix.Events) total += e.weight;
        float pick = dice.NextFloat() * total;
        if (pick < laugh) { Laugh(false); return; }
        pick -= laugh;
        foreach (var e in CampMix.Events)
        {
            if ((pick -= e.weight) >= 0) continue;
            if (!shots.TryGetValue(e.sound, out var clips) || clips.Count == 0) return;
            var v = Array.Find(events, x => !x.source.isPlaying) ?? events[dice.Range(0, events.Length)];
            // Somewhere between the tents and the gate.
            v.source.transform.position = new Vector3(dice.Range(-4.2f, 4.2f), .6f, dice.Range(-12f, -7f));
            v.source.clip = clips[dice.Range(0, clips.Count)]; v.source.pitch = dice.Range(.94f, 1.06f); v.gain = e.gain;
            v.source.volume = v.gain * eventLevel;
            v.source.Play();
            return;
        }
    }
    // Never over Trola (speaking or preparing), within 1 s of his audio, in a spirit moment, or while the camp is startled.
    private bool CanLaugh(float now)
    {
        var state = controller.State;
        return state != GatekeeperController.Phase.Speaking && state != GatekeeperController.Phase.PreparingSpeech && state != GatekeeperController.Phase.PreparingLine
            && now - trolaHeardAt >= 1 && moment < .01f && !spiritVoice && now >= campPausedUntil;
    }
    private void Laughter(float now, float dt)
    {
        laughGate = Mathf.MoveTowards(laughGate, CanLaugh(now) ? 1 : 0, dt / .15f);
        if (laughGate <= 0) foreach (var v in laughVoices) if (v.source.isPlaying) v.source.Stop();
        if (bigLaughAt < 0 || now < bigLaughAt) return;
        if (CanLaugh(now)) { Laugh(true); bigLaughAt = -1; }
        else if (now > bigLaughAt + 8) bigLaughAt = -1;
    }
    private void Laugh(bool big)
    {
        if (laughs.Count == 0) return;
        laughGate = 1; laughLevel = worldLevel;
        int voices = userLaughs ? (big ? 2 : 1) : big ? 3 : dice.Range(2, 4);
        var tent = TentAt[dice.Range(0, TentAt.Length)];
        float delay = 0;
        for (int i = 0; i < voices && i < laughVoices.Length; i++)
        {
            var v = laughVoices[i];
            v.source.Stop();
            v.source.clip = laughs[dice.Range(0, laughs.Count)];
            v.source.pitch = userLaughs ? dice.Range(.95f, 1.05f) : dice.Range(.72f, .85f);
            v.source.transform.position = tent + new Vector3(dice.Range(-1.2f, 1.2f), .9f, dice.Range(-1f, 1.5f));
            v.gain = big ? CampMix.BigLaugh : CampMix.Laugh;
            v.source.volume = v.gain * laughLevel;
            v.source.PlayDelayed(delay);
            delay += dice.Range(.07f, .2f);
        }
    }

    private string Summary()
    {
        int ready = 0;
        foreach (var loop in loops) if (loop.clip != null) ready++;
        string drum = drumStopped ? "stopped" : CampMix.DrumPattern(tension);
        return "Ambience: " + (Muted ? "muted (F8)" : "on (F8 mutes)") + "; " + ready + "/" + loops.Count + " loops; tension " + tension.ToString("0.00")
            + ", drum " + drum + " " + Mathf.RoundToInt(CampMix.Bpm(tension)) + " bpm; fire " + fire.ToString("0.00") + "; muffle " + moment.ToString("0.00");
    }
}
