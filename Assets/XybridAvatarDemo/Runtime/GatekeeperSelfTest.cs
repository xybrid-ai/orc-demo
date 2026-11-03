using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Plays the real gatekeeper scene the way a player does (typing, clicking choices and Try again)
// and writes a JSON report. Inert unless started: by XybridStabilityChecks in the editor, or in a
// built player with "-xybridSelfTest <stage> -xybridReport <file.json>", plus
// "-xybridVoiceFault <fault>" to force a voice failure. Stages:
//   live        passage route into free-form; every live line must be voiced and lip-synced by Rhubarb.
//   fallback    the same route with the voice failing; checks each line's fallback, the subtitle hold,
//               the performer, the log, and that every checkpoint takes input.
//   friendship  the friendship route: the gate, the settle pose and Try again.
//   timing      warm live lines: the name and business replies of a real run, then fixed short (5-word)
//               and long (12-word) replies typed in free-form, each voiced on demand. Every line's
//               GatekeeperLineTiming lands in "timings"; medians per length in "summary".
public sealed class GatekeeperSelfTest : MonoBehaviour
{
    [Serializable] public sealed class LineRecord
    {
        public string id, text, emotion, expectedFamily, family = "", fallback, failure;
        // What the voice was given for the line's first clip, and what it should have been.
        public string speechText = "", expectedSpeech = "";
        public bool live, audible, subtitleShown = true;
        public int cues, frames, jawOpenFrames;
        public float preparationSeconds, synthesisSeconds, analysisSeconds, deliverySeconds, readableSeconds, jawMin = 100, jawMax;
    }
    [Serializable] public sealed class StateMotion { public string state; public float seconds, longestStillSeconds; public int blinks; }
    [Serializable] public sealed class Report
    {
        public string stage = "", fault = "", platform = "", rhubarb = "", model = "", error = "", longestFrameAt = "";
        public bool complete, passed, rhubarbInstalled;
        public float seconds, longestFrameSeconds, longestPreparingSeconds;
        public List<string> passes = new List<string>(), failures = new List<string>(), logs = new List<string>(), screenshots = new List<string>();
        public List<LineRecord> lines = new List<LineRecord>();
        public List<StateMotion> states = new List<StateMotion>();
        public List<GatekeeperLineTiming> timings = new List<GatekeeperLineTiming>();
        public List<string> summary = new List<string>();
        public List<TimingSummary> timingSummary = new List<TimingSummary>();
    }
    [Serializable] public sealed class TimingSummary
    {
        public string label;
        public int count, firstSoundOverThreeSeconds, firstWordOverThreeSeconds;
        public float medianFirstSoundMs, medianFirstWordMs, minFirstWordMs, maxFirstWordMs;
    }
    public const string ShortReply = "Mm. Go on. I'm listening.";
    public const string LongReply = "Fine, traveller, keep your boots off my bridge and your hands visible.";

    public Report Result { get; private set; }
    public bool Done => Result != null && Result.complete;
    private string stage, path;
    private bool quit, clipHasSound, lineShot;
    private float started;
    private GatekeeperController c;
    private GatekeeperView view;
    private GatekeeperBarrier gate;
    private GatekeeperPerformer performer;
    private SkinnedMeshRenderer face;
    private int jaw = -1, cheek = -1, brow = -1, errors;
    private LineRecord line;
    private readonly List<InputDevice> suppressedPointers = new List<InputDevice>();

    private void RestorePointers()
    {
        foreach (var device in suppressedPointers)
            if (device.added && !device.enabled) InputSystem.EnableDevice(device);
        suppressedPointers.Clear();
    }
    private void OnDisable() { RestorePointers(); }

    public static GatekeeperSelfTest Run(string stage, string reportPath, bool quitWhenDone = false)
    {
        var test = new GameObject("Gatekeeper self-test").AddComponent<GatekeeperSelfTest>();
        test.stage = stage; test.path = Path.GetFullPath(reportPath); test.quit = quitWhenDone;
        return test;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void FromCommandLine()
    {
        string stage = Argument("-xybridSelfTest");
        if (stage != null) Run(stage, Argument("-xybridReport") ?? Path.Combine(Application.persistentDataPath, "selftest-" + stage + ".json"), true);
    }
    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private IEnumerator Start()
    {
        Result = new Report { stage = stage, platform = Application.platform.ToString() };
        started = Time.realtimeSinceStartup;
        Application.runInBackground = true;
        Application.logMessageReceived += Capture;
        Save();
        // These tests submit through the real UI buttons. Physical mouse/touch events from
        // other Mac windows must not interrupt their deterministic dialogue route.
        foreach (var device in InputSystem.devices)
            if (device is Pointer && device.enabled)
            { InputSystem.DisableDevice(device); suppressedPointers.Add(device); }
        // Let the controller's own auto-start (0.5 s) happen first, so it cannot restart a run in progress.
        yield return new WaitForSecondsRealtime(1);
        IEnumerator body = null;
        try
        {
            Bind();
            body = stage == "live" ? Live() : stage == "fallback" ? Fallback() : stage == "friendship" ? Friendship() : stage == "timing" ? Timing()
                : throw new ArgumentException("Unknown self-test stage: " + stage);
        }
        catch (Exception e) { Result.error = e.Message; }
        if (body != null)
        {
            var tracker = StartCoroutine(Track());
            yield return Guard(body);
            StopCoroutine(tracker);
        }
        Application.logMessageReceived -= Capture;
        if (c != null) { c.LineStarted -= OnLine; c.TimingRecorded -= OnTiming; }
        Result.complete = true; Result.passed = Result.failures.Count == 0 && string.IsNullOrEmpty(Result.error);
        Result.seconds = Time.realtimeSinceStartup - started;
        Save();
        RestorePointers();
        Debug.Log("[Gatekeeper self-test] " + stage + (Result.passed ? " passed" : " FAILED") + ": " + path);
        if (quit) { yield return new WaitForSecondsRealtime(.5f); Application.Quit(); }
    }

    private void Bind()
    {
        c = FindFirstObjectByType<GatekeeperController>(); view = FindFirstObjectByType<GatekeeperView>();
        gate = FindFirstObjectByType<GatekeeperBarrier>(); performer = FindFirstObjectByType<GatekeeperPerformer>();
        Require(c != null && view != null && gate != null && performer != null && c.liveSpeech != null, "Open the gatekeeper scene.");
        if (performer.Previewing) performer.EndPreview();
        foreach (var renderer in c.character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.sharedMesh == null) continue;
            int j = Shape(renderer, "jawOpen");
            if (j < 0) continue;
            face = renderer; jaw = j; cheek = Shape(renderer, "cheekSquintLeft"); brow = Shape(renderer, "browInnerDownLeft"); break;
        }
        Require(face != null, "The character has no jawOpen shape.");
        var speech = c.liveSpeech;
        Result.rhubarb = speech.RhubarbPath; Result.model = speech.ModelPath; Result.rhubarbInstalled = File.Exists(speech.RhubarbPath);
        Result.fault = !File.Exists(speech.ModelPath) ? "ModelMissing" : speech.simulatedFault == GatekeeperLiveSpeech.Fault.None && !Result.rhubarbInstalled ? "RhubarbMissing" : speech.simulatedFault.ToString();
        c.LineStarted += OnLine; c.TimingRecorded += OnTiming;
    }
    private static int Shape(SkinnedMeshRenderer renderer, string name)
    {
        for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
        {
            string shape = renderer.sharedMesh.GetBlendShapeName(i);
            if (shape == name || shape.EndsWith("." + name, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    // ---------- Stages ----------
    private IEnumerator Live()
    {
        string rhubarb = Result.rhubarb.Replace('\\', '/');
        Check(Result.rhubarbInstalled, "Rhubarb is installed", Result.rhubarb);
        Check(rhubarb.StartsWith(Application.streamingAssetsPath.Replace('\\', '/') + "/", StringComparison.Ordinal) && !rhubarb.Contains("/Temp/"),
            "Rhubarb resolves inside StreamingAssets, not Temp", Result.rhubarb);
        Require(c.liveSpeech.simulatedFault == GatekeeperLiveSpeech.Fault.None, "Run the live stage without a simulated fault.");
        yield return Passage();
        var live = Result.lines.Where(l => l.live).ToList();
        Check(live.Any(l => l.text.Contains("Aveline")) && live.Any(l => l.text.Contains("visit my sister")) && live.Any(l => l.id.StartsWith("free-reply", StringComparison.Ordinal)),
            "live lines covered the player's name, business and free-form replies", string.Join(", ", live.Select(l => l.id)));
        foreach (var l in live)
            Check(l.fallback == "None" && l.cues > 0 && l.audible && l.subtitleShown, "live line '" + l.id + "' is voiced and lip-synced by Rhubarb",
                l.cues + " cues, prepared in " + F(l.preparationSeconds) + " s");
        Check(c.VoiceFailureCount == 0, "no voice failures", c.LastVoiceFailure);
        Common();
        Shot("live-free-form");
    }

    private IEnumerator Fallback()
    {
        var speech = c.liveSpeech; var fault = speech.simulatedFault;
        bool silent = Result.fault == "ModelMissing" || fault == GatekeeperLiveSpeech.Fault.SynthesisFails || fault == GatekeeperLiveSpeech.Fault.SynthesisHangs;
        bool loudness = !silent && (Result.fault == "RhubarbMissing" || fault == GatekeeperLiveSpeech.Fault.LipSyncMissing || fault == GatekeeperLiveSpeech.Fault.LipSyncFails);
        Require(silent || loudness, "Force a voice fault first (simulatedFault, -xybridVoiceFault, or remove the model or Rhubarb).");
        string expected = silent ? "SubtitleOnly" : "LoudnessJaw";
        yield return Passage();
        var live = Result.lines.Where(l => l.live).ToList();
        foreach (var l in live)
        {
            bool ok = l.fallback == expected && l.subtitleShown && l.family == l.expectedFamily && l.failure.Length > 0;
            string detail = l.fallback + ", on screen " + F(l.deliverySeconds) + " s (readable " + F(l.readableSeconds) + " s), prepared in " + F(l.preparationSeconds)
                + " s, jaw " + F(l.jawMin) + "-" + F(l.jawMax) + " open " + l.jawOpenFrames + "/" + l.frames + " frames, family " + l.family + "/" + l.expectedFamily;
            if (silent) ok &= l.deliverySeconds >= l.readableSeconds - .1f && l.jawMax < 1 && !l.audible;
            else ok &= l.audible && l.cues == 0 && l.jawMax > 15 && l.jawOpenFrames > l.frames * .15f && l.jawOpenFrames < l.frames * .97f;
            Check(ok, "live line '" + l.id + "' falls back to " + (silent ? "a held subtitle" : "audio with a loudness-driven jaw"), detail);
        }
        var statics = Result.lines.Where(l => !l.live).ToList();
        Check(statics.Count > 0 && statics.All(l => l.fallback == "None" && l.cues > 0), "prepared lines still play with their own audio and cues", statics.Count + " lines");
        int logged = Result.logs.Count(m => m.Contains("[Gatekeeper voice] Line '"));
        Check(logged >= live.Count, "every failure is logged", logged + " warnings for " + live.Count + " live lines; first: " + Result.logs.FirstOrDefault(m => m.Contains("Line '")));
        if (fault == GatekeeperLiveSpeech.Fault.SynthesisHangs && live.Count > 1)
        {
            Check(live[0].preparationSeconds >= speech.preparationTimeout - .1f && live[0].preparationSeconds <= speech.preparationTimeout + 1,
                "the first hung line gives up after the preparation timeout", F(live[0].preparationSeconds) + " s");
            Check(live.Skip(1).All(l => l.preparationSeconds <= speech.stalledPreparationTimeout + .5f),
                "later lines wait only the stalled budget", string.Join(" ", live.Skip(1).Select(l => F(l.preparationSeconds))));
        }
        Common();
        Shot("fallback-" + Result.fault);
    }

    private IEnumerator Friendship()
    {
        Quaternion closed = gate.hinge.localRotation, secondClosed = gate.secondHinge != null ? gate.secondHinge.localRotation : Quaternion.identity;
        Require(!gate.IsOpen, "The gate must start closed.");
        c.Restart();
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingName, 60, "the name prompt"); InputReady("the name prompt");
        Type("Nora");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingBusiness, 90, "the business prompt"); InputReady("the business prompt");
        Type("find a warm meal");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput, 90, "the first hub choices"); InputReady("the first hub choices");
        Click("Ask about the camp");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput, 90, "the second hub choices"); InputReady("the second hub choices");
        Click("Ask about the camp");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput && c.CurrentChoices.Count == 3, 90, "the final choices"); InputReady("the final choices");
        int endingFrom = Result.lines.Count;
        Click("Share the joke");
        bool openedEarly = false;
        yield return Until(() => { openedEarly |= gate.IsOpen && c.State != GatekeeperController.Phase.Ended; return c.State == GatekeeperController.Phase.Ended; }, 90, "the friendship ending");
        var ending = Result.lines.Skip(endingFrom).ToList();
        Check(c.Outcome == GatekeeperOutcome.Liked, "sharing the joke at high patience wins his friendship", c.Outcome + ", patience " + c.Patience);
        Check(ending.Count == 2 && ending.All(l => l.family == "Warm" && l.audible && l.fallback == "None"), "the two friendship lines play warm, voiced and lip-synced",
            string.Join(" | ", ending.Select(l => l.text + " [" + l.family + "]")));
        Check(!openedEarly, "the gate stays shut until his last line ends");
        yield return Until(() => gate.IsFullyOpen, 3, "both gate doors to open");
        Check(gate.IsFullyOpen, "both gate doors open after the last line");
        InputReady("the friendship ending");
        Check(view.actionText.text == "Try again" && !view.inputRow.activeSelf && (view.choiceRow == null || !view.choiceRow.gameObject.activeSelf),
            "the ending offers Try again and nothing else", "button '" + view.actionText.text + "'");
        var s = performer.settings;
        float settle = s.settleSeconds + s.moodHoldSeconds + s.moodReleaseSeconds + .5f;
        for (float t = 0; t < settle; t += Time.unscaledDeltaTime) yield return null;
        float cheekWeight = face.GetBlendShapeWeight(cheek), browWeight = face.GetBlendShapeWeight(brow);
        Check(performer.MoodSource.EndsWith("Liked", StringComparison.Ordinal) && cheekWeight > 8 && browWeight < 10 && performer.ShakeOffset.magnitude < 1e-5f,
            "he settles into the warm friendship pose", performer.MoodSource + ", cheek squint " + F(cheekWeight) + ", inner brow down " + F(browWeight));
        Check(gate.IsFullyOpen && c.State == GatekeeperController.Phase.Ended && view.action.gameObject.activeSelf, "the gate and Try again hold while he settles");
        Shot("friendship-settled");
        yield return new WaitForSecondsRealtime(.3f);
        view.action.onClick.Invoke();
        Check(c.Outcome == GatekeeperOutcome.Playing && c.Patience == 3 && c.PlayerName == "" && c.Business == "" && !gate.IsOpen,
            "Try again resets the encounter", c.Outcome + ", patience " + c.Patience);
        yield return Until(() => Quaternion.Angle(gate.hinge.localRotation, closed) < .5f
            && (gate.secondHinge == null || Quaternion.Angle(gate.secondHinge.localRotation, secondClosed) < .5f), 3, "the gate to close");
        Check(true, "the gate closes again after Try again");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingName, 60, "the name prompt after Try again");
        InputReady("the name prompt after Try again");
        Check(!performer.MoodSource.EndsWith("Liked", StringComparison.Ordinal), "the friendship pose is released", performer.MoodSource);
        Check(Result.lines.All(l => l.fallback == "None" && l.cues > 0), "every line of the run is voiced and lip-synced",
            string.Join(", ", Result.lines.Where(l => l.fallback != "None" || l.cues == 0).Select(l => l.id + ":" + l.fallback)));
        Common();
        Shot("friendship-try-again");
    }

    private IEnumerator Timing()
    {
        Require(c.liveSpeech.simulatedFault == GatekeeperLiveSpeech.Fault.None, "Run the timing stage without a simulated fault.");
        var overlay = FindFirstObjectByType<GatekeeperTimingOverlay>();
        Check(overlay != null && overlay.gatekeeper == c, "the scene has the timing overlay");
        if (overlay != null) overlay.visible = true;
        Check(c.PreparedReactionCount == 8, "all eight authored reactions are prepared", c.PreparedReactionCount + " clips");
        // The intro runs a silent real synthesis plus Rhubarb; the first name reply is already warm.
        c.Restart();
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingName, 60, "the name prompt");
        Check(c.liveSpeech.WarmCompleted && string.IsNullOrEmpty(c.liveSpeech.WarmError),
            "silent synthesis and Rhubarb completed during the intro", c.liveSpeech.WarmError);
        int nameFrom = Result.timings.Count;
        Type("Aveline");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingBusiness, 90, "the business prompt");
        int businessFrom = Result.timings.Count;
        Type("visit my sister");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput, 90, "the first hub choices");
        Summarize("after the name", Result.timings.Skip(nameFrom).Take(businessFrom - nameFrom).Where(t => t.trigger == "typed input"));
        Summarize("after the business", Result.timings.Skip(businessFrom).Where(t => t.trigger == "typed input"));

        // Fixed replies, typed like free-form. The session cache is cleared first so each is synthesized on
        // demand, and a reply of each length is played once before measuring.
        c.UseLineSource(new TimingReplies(), new GatekeeperConversationState(new GatekeeperEncounter(3, 5, true)));
        yield return Until(() => c.AcceptsText, 30, "the timing prompt");
        yield return new WaitForSecondsRealtime(2);
        var measured = new List<GatekeeperLineTiming>();
        string[] plan = { ShortReply, LongReply, ShortReply, LongReply, ShortReply, LongReply, ShortReply, LongReply };
        for (int i = 0; i < plan.Length; i++)
        {
            c.liveSpeech.ResetSession();
            int before = Result.timings.Count;
            Type(plan[i]);
            yield return Until(() => Result.timings.Count > before && c.AcceptsText, 60, "a timing reply");
            var timing = Result.timings[before];
            yield return Until(() => timing.analysisComplete, 15, "the independent Rhubarb measurement");
            if (i >= 2) measured.Add(timing);
            yield return new WaitForSecondsRealtime(.5f);
        }
        foreach (var t in measured)
            Check(t.voice == "live, on demand" && t.fallback == "None" && t.trigger == "typed input" && t.synthesisMs > 0 && t.rhubarbMs == 0 && t.rawRhubarbMs > 0 && t.analysisComplete && t.analysisError.Length == 0
                && t.firstSoundMs < 250 && t.firstSoundMs < t.firstWordMs && !string.IsNullOrEmpty(t.reaction),
                "timing reply '" + t.id + "' is voiced on demand with Rhubarb", t.voice + ", " + t.fallback + ", first sound " + F(t.firstSoundMs) + ", first word " + F(t.firstWordMs) + " ms");
        foreach (var t in Result.timings)
        {
            float parts = t.pacingMs + t.lineSourceMs + t.queueMs + t.synthesisMs + t.rhubarbMs + t.handoffMs + t.clipMs + t.playbackStartMs;
            Check(Mathf.Abs(parts - t.totalMs) < 2, "the stages of '" + t.id + "' add up to its total", F(parts) + " vs " + F(t.totalMs) + " ms");
        }
        var liveTimings = Result.timings.Where(t => t.voice.StartsWith("live", StringComparison.Ordinal)).ToList();
        for (int i = 1; i < liveTimings.Count; i++)
            Check(liveTimings[i].reaction != liveTimings[i - 1].reaction, "opening reactions never repeat back to back",
                liveTimings[i - 1].reaction + " -> " + liveTimings[i].reaction);
        Summarize("all measured warm replies", measured);
        Summarize("short reply", measured.Where(t => t.text == ShortReply));
        Summarize("long reply", measured.Where(t => t.text == LongReply));
        Common();
        yield return new WaitForSecondsRealtime(.3f);
        Shot("timing-overlay");
    }
    private void Summarize(string label, IEnumerable<GatekeeperLineTiming> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) { Result.summary.Add(label + ": no line"); Save(); return; }
        string Median(Func<GatekeeperLineTiming, float> pick)
        {
            var v = list.Select(pick).OrderBy(x => x).ToList();
            float m = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
            return Mathf.RoundToInt(m) + (v.Count > 1 ? " (" + Mathf.RoundToInt(v[0]) + "-" + Mathf.RoundToInt(v[v.Count - 1]) + ")" : "");
        }
        var first = list[0];
        Result.summary.Add(label + ": " + first.words + " words, " + F(first.audioSeconds) + " s audio, " + list.Count + " run" + (list.Count > 1 ? "s" : "") + ", " + first.voice
            + " | first sound " + Median(t => t.firstSoundMs) + " ms | first word " + Median(t => t.firstWordMs)
            + " ms | over 3 s: sound " + list.Count(t => t.firstSoundMs > 3000) + "/" + list.Count
            + ", word " + list.Count(t => t.firstWordMs > 3000) + "/" + list.Count
            + " | synthesis " + Median(t => t.synthesisMs) + " | Rhubarb (parallel) " + Median(t => t.rawRhubarbMs)
            + " | other " + Median(t => t.OtherMs) + " (choosing " + Median(t => t.lineSourceMs) + ", voice worker queue " + Median(t => t.queueMs)
            + ", hand-off " + Median(t => t.handoffMs) + ", clip " + Median(t => t.clipMs) + ", playback start " + Median(t => t.playbackStartMs) + ")");
        float Med(Func<GatekeeperLineTiming, float> pick)
        {
            var values = list.Select(pick).OrderBy(x => x).ToList();
            return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
        }
        Result.timingSummary.Add(new TimingSummary
        {
            label = label, count = list.Count, medianFirstSoundMs = Med(t => t.firstSoundMs), medianFirstWordMs = Med(t => t.firstWordMs),
            minFirstWordMs = list.Min(t => t.firstWordMs), maxFirstWordMs = list.Max(t => t.firstWordMs),
            firstSoundOverThreeSeconds = list.Count(t => t.firstSoundMs > 3000), firstWordOverThreeSeconds = list.Count(t => t.firstWordMs > 3000)
        });
        Save();
    }
    // Answers every typed line by saying it back, so the typed text is exactly the line voiced.
    private sealed class TimingReplies : IGatekeeperLineSource
    {
        public GatekeeperPresentation Presentation { get; } = new GatekeeperPresentation
        {
            InputSlot = GatekeeperInputSlot.Reply, Placeholder = "Timing", CharacterLimit = 240, PersistentText = true,
            PointerInterrupts = false, ShowInterruptButton = false, IdleSeconds = 0
        };
        public IEnumerable<GatekeeperScenario.Line> AnticipatedLines => Array.Empty<GatekeeperScenario.Line>();
        public Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState state, GatekeeperPlayerInput input, CancellationToken cancellation)
        {
            if (input.Kind != GatekeeperInputKind.Text) return Task.FromResult<GatekeeperScenario.Line>(null);
            return Task.FromResult(new GatekeeperScenario.Line
            {
                id = "timing-" + GatekeeperLineTiming.CountWords(input.Text) + "-words", text = input.Text, emotion = "interest", intensity = .5f, delivery = ""
            });
        }
    }

    // Aveline / visit my sister / Flatter / Bribe / Offer a fair toll, through the gate, two free-form replies.
    private IEnumerator Passage()
    {
        c.Restart();
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingName, 60, "the name prompt"); InputReady("the name prompt");
        Type("Aveline");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingBusiness, 90, "the business prompt"); InputReady("the business prompt");
        Type("visit my sister");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput, 90, "the first hub choices"); InputReady("the first hub choices");
        Click("Flatter");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput, 90, "the second hub choices"); InputReady("the second hub choices");
        Click("Bribe");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput && c.CurrentChoices.Count == 3, 90, "the final choices"); InputReady("the final choices");
        Click("Offer a fair toll");
        yield return Until(() => c.State == GatekeeperController.Phase.AwaitingInput && c.Prompt.PersistentText, 120, "the free-form handoff");
        Check(c.Outcome == GatekeeperOutcome.Passed && gate.IsFullyOpen, "passage opens the gate and hands over to free-form");
        InputReady("free-form text");
        foreach (string text in new[] { "Hello, gatekeeper.", "What is in the stew?" })
        {
            int before = Result.lines.Count;
            Type(text);
            yield return Until(() => Result.lines.Count > before && c.State == GatekeeperController.Phase.AwaitingInput, 60, "a free-form reply");
            InputReady("free-form text after a reply");
        }
    }

    private void Common()
    {
        Check(Result.states.Count > 0 && Result.states.All(m => m.longestStillSeconds < .75f), "never frozen: he moves in every state",
            string.Join(" ", Result.states.Select(m => m.state + ":" + F(m.seconds) + "s/still " + F(m.longestStillSeconds) + "s/" + m.blinks + " blinks")));
        Check(Result.longestFrameSeconds < .5f, "no frame hitch", "longest frame " + F(Result.longestFrameSeconds) + " s (" + Result.longestFrameAt + ")");
        Check(errors == 0, "no errors or exceptions logged", string.Join(" | ", Result.logs.Where(m => m.StartsWith("Error", StringComparison.Ordinal) || m.StartsWith("Exception", StringComparison.Ordinal)).Take(3)));
        var voiced = Result.lines.Where(l => l.speechText.Length > 0).ToList();
        Check(voiced.All(l => l.speechText == l.expectedSpeech), "every voiced line was given " + (c.liveSpeech.FlowsSpeech ? "its flowing speech text" : "its text as written"),
            string.Join(" | ", voiced.Where(l => l.speechText != l.expectedSpeech).Take(3).Select(l => l.id + ": '" + l.speechText + "', expected '" + l.expectedSpeech + "'")));
    }

    // ---------- Observation ----------
    private void OnTiming(GatekeeperLineTiming timing) { if (!Result.timings.Contains(timing)) Result.timings.Add(timing); Save(); }
    private void OnLine(GatekeeperScenario.Line spoken)
    {
        bool silent = c.CurrentFallback == GatekeeperController.VoiceFallback.SubtitleOnly;
        var voice = c.LastGenerated; var engine = c.liveSpeech;
        bool whole = engine.provider == GatekeeperLiveSpeech.Provider.Phonon && engine.phononWholeLines;
        line = new LineRecord
        {
            // Phonon speaks the whole line, flowed unless the style is as written; other voices speak each clause as written.
            speechText = voice?.speechText ?? "",
            expectedSpeech = voice == null ? "" : engine.FlowsSpeech ? GatekeeperSpeechText.Flow(spoken.text) : whole ? spoken.text : voice.text,
            id = spoken.id, text = spoken.text, emotion = spoken.emotion, live = c.CurrentWasLive, fallback = c.CurrentFallback.ToString(),
            failure = c.CurrentFallback == GatekeeperController.VoiceFallback.None ? "" : c.LastVoiceFailure,
            cues = silent ? 0 : c.character.CueCount, preparationSeconds = c.PlaybackPreparationSeconds, readableSeconds = c.ReadableSeconds(spoken.text),
            expectedFamily = performer.settings.FamilyOf(spoken.emotion).ToString(),
            synthesisSeconds = c.LastGenerated != null ? (float)c.LastGenerated.synthesisSeconds : 0,
            analysisSeconds = c.LastGenerated != null ? (float)c.LastGenerated.analysisSeconds : 0
        };
        clipHasSound = !silent && HasSound(c.character.CurrentClip);
        Result.lines.Add(line); Save();
    }
    private static bool HasSound(AudioClip clip)
    {
        if (clip == null || clip.samples <= 0) return false;
        var samples = new float[Math.Min(clip.samples, 48000) * clip.channels];
        return clip.GetData(samples, 0) && samples.Any(v => Math.Abs(v) > .01f);
    }

    // Per frame: the current line's delivery, and the "never frozen" rule from the performance checks:
    // still = head, upper chest and eye turn less than 0.05 degrees in a 0.25 s window with no blink.
    private IEnumerator Track()
    {
        var motion = new Dictionary<string, StateMotion>(); var still = new Dictionary<string, float>();
        var animator = c.character.GetComponent<Animator>(); var root = c.character.transform;
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head), chest = animator.GetBoneTransform(HumanBodyBones.UpperChest), eye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
        Quaternion refHead = Quaternion.identity, refChest = Quaternion.identity, refEye = Quaternion.identity;
        float refTime = -1, lastBlink = 0, preparing = 0; bool blinked = false;
        while (true)
        {
            yield return new WaitForEndOfFrame();
            float dt = Time.unscaledDeltaTime, now = Time.realtimeSinceStartup;
            if (now - started > 3 && dt > Result.longestFrameSeconds)
            { Result.longestFrameSeconds = dt; Result.longestFrameAt = c.State + ", line " + c.CurrentLineId + ", " + F(now - started) + " s in"; }
            preparing = c.State == GatekeeperController.Phase.PreparingSpeech ? preparing + dt : 0;
            Result.longestPreparingSeconds = Mathf.Max(Result.longestPreparingSeconds, preparing);
            if (line != null && c.State == GatekeeperController.Phase.Speaking && c.CurrentLine != null && c.CurrentLine.id == line.id && (c.IsSilentLine || c.character.IsSpeaking))
            {
                line.deliverySeconds += dt; line.frames++;
                line.cues = c.character.CueCount; line.fallback = c.CurrentFallback.ToString();
                line.failure = c.CurrentFallback == GatekeeperController.VoiceFallback.None ? "" : c.LastVoiceFailure;
                if (c.LastGenerated != null) line.analysisSeconds = (float)c.LastGenerated.analysisSeconds;
                if (view.sentence.text != line.text || !view.sentence.isActiveAndEnabled) line.subtitleShown = false;
                float w = face.GetBlendShapeWeight(jaw);
                line.jawMin = Mathf.Min(line.jawMin, w); line.jawMax = Mathf.Max(line.jawMax, w);
                if (w > 5) line.jawOpenFrames++;
                if (clipHasSound && c.character.speech != null && c.character.speech.isPlaying && c.character.speech.timeSamples > 0) line.audible = true;
                if (line.deliverySeconds > .6f && line.family.Length == 0) line.family = performer.ActiveFamily.ToString();
                // One frame of the first fallback line: the held subtitle, or the jaw open on a loud syllable.
                if (!lineShot && line.live && line.fallback != "None" && line.deliverySeconds > 1 && (line.fallback == "SubtitleOnly" || w > 25)) { lineShot = true; Shot("line"); }
            }
            string state = c.State.ToString();
            if (!motion.TryGetValue(state, out var m)) { m = new StateMotion { state = state }; motion[state] = m; Result.states.Add(m); still[state] = 0; }
            Quaternion h = Quaternion.Inverse(root.rotation) * head.rotation, ch = Quaternion.Inverse(root.rotation) * chest.rotation, e = eye.localRotation;
            if (performer.BlinkAmount > .5f && lastBlink <= .5f) { m.blinks++; blinked = true; }
            lastBlink = performer.BlinkAmount;
            m.seconds += dt;
            if (refTime < 0) { refTime = now; refHead = h; refChest = ch; refEye = e; continue; }
            if (now - refTime < .25f) continue;
            float change = Quaternion.Angle(h, refHead) + Quaternion.Angle(ch, refChest) + Quaternion.Angle(e, refEye);
            still[state] = change < .05f && !blinked ? still[state] + (now - refTime) : 0;
            m.longestStillSeconds = Mathf.Max(m.longestStillSeconds, still[state]);
            refTime = now; refHead = h; refChest = ch; refEye = e; blinked = false;
        }
    }
    private void Capture(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors++;
        if (type != LogType.Log || message.StartsWith("[Gatekeeper", StringComparison.Ordinal)) Result.logs.Add(type + ": " + message);
    }

    // ---------- Player actions ----------
    private void Type(string text) { view.input.text = text; view.send.onClick.Invoke(); }
    private void Click(string label)
    {
        // Skip the previous checkpoint's buttons, which stay as inactive children until destroyed.
        Button button = null;
        if (view.choiceRow != null)
            foreach (Transform child in view.choiceRow)
                if (child.name == "Choice " + label && child.gameObject.activeInHierarchy) button = child.GetComponent<Button>();
        Require(button != null && button.interactable, "Choice is not clickable: " + label);
        button.onClick.Invoke();
    }
    // A checkpoint must take input: the text field, the choice buttons, or Try again.
    private void InputReady(string where)
    {
        var phase = c.State;
        bool typing = phase == GatekeeperController.Phase.AwaitingName || phase == GatekeeperController.Phase.AwaitingBusiness
            || phase == GatekeeperController.Phase.AwaitingInput && c.Prompt.PersistentText;
        bool ok = typing ? view.inputRow.activeSelf && view.input.interactable && view.send.interactable
            : phase == GatekeeperController.Phase.AwaitingInput ? view.choiceRow != null && view.choiceRow.gameObject.activeSelf && view.choiceRow.GetComponentsInChildren<Button>().Any(b => b.interactable)
            : phase == GatekeeperController.Phase.Ended && view.action.gameObject.activeSelf && view.action.interactable;
        Check(ok, "input is enabled at " + where, phase.ToString());
    }
    private IEnumerator Until(Func<bool> done, float seconds, string what)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!done())
        {
            Require(c.State != GatekeeperController.Phase.VoiceFailed, "The conversation halted (\"" + c.CurrentText + "\") while waiting for " + what + ".");
            Require(Time.realtimeSinceStartup < deadline, "Timed out after " + seconds + " s waiting for " + what + " (state " + c.State + ", line " + c.CurrentLineId + ").");
            yield return null;
        }
    }
    private void Shot(string name)
    {
        string file = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-" + name + ".png");
        ScreenCapture.CaptureScreenshot(file); Result.screenshots.Add(file); Save();
    }

    // ---------- Report ----------
    private IEnumerator Guard(IEnumerator body)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(body);
        while (stack.Count > 0)
        {
            object current = null; bool moved = false; Exception failure = null;
            try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
            catch (Exception e) { failure = e; }
            if (failure != null) { Result.error = failure.Message; Debug.LogException(failure); yield break; }
            if (!moved) { stack.Pop(); continue; }
            if (current is IEnumerator nested) { stack.Push(nested); continue; }
            yield return current;
        }
    }
    private void Check(bool ok, string name, string detail = "") { (ok ? Result.passes : Result.failures).Add(name + (!string.IsNullOrEmpty(detail) ? ": " + detail : "")); Save(); }
    private static void Require(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
    private void Save() { if (Result != null) File.WriteAllText(path, JsonUtility.ToJson(Result, true)); }
    private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
