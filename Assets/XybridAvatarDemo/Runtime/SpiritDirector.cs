using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

// Vesper's conductor. It installs itself beside the authored gatekeeper, turns every checkpoint into a SpiritCheckpoint
// for SpiritPolicy, and drives one SpiritWindow per moment: her voice, the halo, the mind field, d1 /spirit (keywords
// after decision_timeout or when d1 is away), answers and cut-offs. The golden rule: she is audible only while Trola
// waits or has ended, and any other state snaps her shut. Trola's idle clock is frozen only while she speaks or thinks
// (never NotifyActivity), every async result carries the window token, and three exceptions switch her off for the
// session while Trola carries on. F7 off removes every spirit effect; the camp soundscape is separate (F8). Nothing here
// drives the controller. See docs/spirit.md.
public sealed class SpiritDirector : MonoBehaviour
{
    public const string LinesResource = "Spirit/SpiritLines";
    private const int PolicySeed = 11, MaxErrors = 3, TranscriptTurns = 6, HistoryTurns = 2, JournalSize = 40;
    // Opening cues start this long after the halo; the thought dissolves for LabelDelay before its label shows.
    private const float OpeningDelay = .35f, LabelDelay = .25f, InertEvery = .5f, HealthEvery = 3, LineWatchdog = 9;
    // Below the controller's 20 s watchdog: a long subtitle-only opening lets Trola's clock run rather than trip it.
    private const float MaxHoldSeconds = 15;

    // One thought to her: what was typed, what d1 (or the keywords) made of it, and what she answered.
    public sealed class Thought
    {
        public int Number, Token;
        public string Text = "", Label = "";
        public float SubmittedAt;
        public Task<SpiritDecision> Pending;
        public CancellationTokenSource Cancel;
        public SpiritDecision Decision;
        public SpiritCue Cue;
        public bool Answered, Silent;
        public double LabelMs = -1, VoiceMs = -1, FirstAudioMs = -1;
    }
    private enum After { Opening, Answer, Warning, Closing }

    public static SpiritDirector Instance { get; private set; }
    public GatekeeperController Controller { get; private set; }
    public SpiritLines Lines { get; private set; }
    public SpiritAdvisor Advisor { get; private set; }
    public SpiritPolicy Policy { get; private set; }
    public SpiritWindow Window { get; private set; }
    public SpiritVoice Voice { get; private set; }
    public SpiritPresence Presence { get; private set; }
    public SpiritLens Lens { get; private set; }
    // The latest waiting state, its graph read (hub and reckoning only) and the open or last moment.
    public SpiritCheckpoint Checkpoint { get; private set; }
    public SpiritAdvice Advice { get; private set; }
    public SpiritMomentPlan Moment { get; private set; }
    public string MomentLabel { get; private set; } = "";
    public Thought LastThought => thought;
    public int Thoughts { get; private set; }
    public int KeywordDecisions { get; private set; }
    public IReadOnlyList<double> D1Milliseconds => d1Milliseconds;
    public IReadOnlyList<string> Journal => journal;
    public float EndedAt { get; private set; } = -1;
    public bool Active => built && !broken && !inert && SpiritSignals.Enabled && Controller != null && !Controller.IsSuspended;
    public string Status => broken ? "Spirit disabled for this session after " + MaxErrors + " errors: " + lastError
        : inert ? "Spirit inert: " + inertReason : !SpiritSignals.Enabled ? "Spirit off (F7)" : Controller != null && Controller.IsSuspended ? "Spirit paused: Trola is suspended" : "";
    public string DecisionStatus { get { string why = D1Problem(); return why == null ? "d1 /spirit ready (" + healthSeconds.ToString("0.0#") + " s health)" : "keywords (" + why + ")"; } }

    private readonly SpiritCue[] one = new SpiritCue[1];
    // Cues of the current token not yet finished; `queued` ones are not handed to her voice yet (one at a time, so each
    // line's voice_wait starts when it is due, not when the moment was planned).
    private readonly List<SpiritCue> speaking = new List<SpiritCue>(), queued = new List<SpiritCue>();
    private readonly List<SpiritTurn> transcript = new List<SpiritTurn>();
    private readonly Dictionary<string, int> visits = new Dictionary<string, int>();
    private readonly List<double> d1Milliseconds = new List<double>();
    private readonly List<string> journal = new List<string>();
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private SpiritDecisionClient client;
    private Thought thought;
    private After after;
    private bool built, broken, inert, armed, holding, holdSpent, momentOpen, openingStarted, arrivalPending, nameShown, d1Ready, d1Off;
    private int errors, encounters, arrivalFrame, spentToken, patienceAtCheckpoint = -1, trolaLastDelta, d1Failures;
    private SpiritWindowState spentState;
    private GatekeeperController.Phase lastState, arrivalState;
    private float openingAt, speechDeadline, heldSince, nextInertCheck, retryAt, healthSeconds;
    private bool interruptedSince, tauntedSince;
    private string lastChoiceLabel = "", trolaLastLineId = "", lastError = "", inertReason = "", d1Problem = "connecting", loggedD1 = "";
    private string spiritSaid = ""; // d1's spirit_said: her latest advice-bearing line (see CarriesAdvice).
    private int previews;

    private static double Now => Time.realtimeSinceStartupAsDouble;
    private bool Audible => Controller != null && (Controller.IsWaitingForPlayer || Controller.State == GatekeeperController.Phase.Ended);
    public float Impatience01 => Controller != null && Controller.IdleTauntSeconds > 0 ? Mathf.Clamp01(Controller.IdleElapsed / Controller.IdleTauntSeconds) : 0;

    // Trola never depends on any of this: every bail-out leaves the game exactly as it was.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SpiritSignals.Reset(); // Play Mode without a domain reload keeps the last session's statics.
        Instance = null;
        if (HasArgument("-xybridNoSpirit") || HasArgument("-xybridSelfTest") || HasArgument("-xybridSpeechTest")) return;
        var controller = FindAnyObjectByType<GatekeeperController>();
        if (controller == null || controller.scenario == null || !controller.scenario.authoredConversation || UnderTest()) return;
        SpiritLines lines; SpiritAdvisor advisor; SpiritPolicy policy;
        try
        {
            var asset = Resources.Load<TextAsset>(LinesResource);
            if (asset == null) throw new InvalidOperationException("Resources/" + LinesResource + ".json is missing");
            lines = SpiritLines.Parse(asset.text);
            advisor = new SpiritAdvisor(controller.scenario.ReadStory(), lines.rules.reckoning_preference);
            policy = new SpiritPolicy(lines, advisor, PolicySeed);
            if (!lines.Validate(policy.RequiredLines(), out string error)) throw new InvalidOperationException("SpiritLines.json: " + error);
        }
        catch (Exception error)
        {
            string reason = error.Message;
            Debug.LogWarning("[Spirit] Vesper stays off: " + reason + ". Trola is unaffected.");
            SpiritSignals.Describer = () => "Spirit: off (" + reason + ")";
            return;
        }
        // Never on the controller's object: editor builders call GetComponent<GatekeeperLiveSpeech>() there.
        var host = new GameObject("Spirit (runtime)");
        try { host.AddComponent<SpiritDirector>().Build(controller, lines, advisor, policy); }
        catch (Exception error)
        {
            // A half-built spirit must not linger: OnDestroy unsubscribes whatever Build had wired up.
            Instance = null;
            Destroy(host);
            Debug.LogWarning("[Spirit] Vesper stays off: " + error.Message + ". Trola is unaffected.");
            SpiritSignals.Describer = () => "Spirit: off (" + error.Message + ")";
        }
    }

    private static bool HasArgument(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    private static bool UnderTest() => FindAnyObjectByType<GatekeeperSelfTest>() != null || FindAnyObjectByType<GatekeeperSpeechValidation>() != null;

    private void Build(GatekeeperController controller, SpiritLines lines, SpiritAdvisor advisor, SpiritPolicy policy)
    {
        Instance = this; Controller = controller; Lines = lines; Advisor = advisor; Policy = policy;
        Window = new SpiritWindow(lines.timing);
        lastState = controller.State;
        Presence = SpiritPresence.Create(FindAnyObjectByType<GatekeeperView>(), FindAnyObjectByType<TrolaAtmosphere>(), lines.visual);
        Presence.transform.SetParent(transform, false);
        Presence.haloIn = lines.timing.halo_in; Presence.haloOut = lines.timing.halo_out; Presence.snapOut = lines.timing.snap_out;
        Presence.spiritName = lines.spirit_name;
        Voice = SpiritVoice.Create(transform, controller, lines.voice);
        Voice.voiceWait = lines.timing.voice_wait;
        Presence.voice = Voice; // Her line's phrase reveal follows her clip.
        Lens = gameObject.AddComponent<SpiritLens>();
        Lens.director = this; Lens.Visible = HasArgument("-xybridLens");
        controller.Changed += OnChanged;
        controller.PlayerActed += OnPlayerActed;
        controller.LineStarted += OnTrolaLine;
        controller.Restarted += OnRestarted;
        Presence.Submitted += OnSubmitted;
        Presence.Escaped += OnEscaped;
        Voice.LineStarted += OnLineStarted;
        Voice.LineFinished += OnLineFinished;
        SpiritSignals.Describer = Describe;
        ConnectDecisions();
        built = true;
        Note("installed: " + lines.spirit_name + ", " + lines.lines.Count + " lines, preference " + advisor.Preference);
    }

    // ---- d1 /spirit (health probe like GatekeeperDecisionService; keywords whenever it is away) ----

    private void ConnectDecisions()
    {
        string endpoint = SpiritDecisionClient.DefaultEndpoint;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-xybridDecisions");
        if (at >= 0 && at + 1 < args.Length) { if (args[at + 1] == "off") d1Off = true; else endpoint = args[at + 1]; }
        if (d1Off) { d1Problem = "off (-xybridDecisions off)"; return; }
        try { client = new SpiritDecisionClient(endpoint, Lines.timing.decision_timeout); }
        catch (Exception error) when (error is ArgumentException || error is UriFormatException)
        {
            d1Problem = "unavailable: " + error.Message;
            Debug.LogWarning("[Spirit] d1 thoughts disabled: " + error.Message + "; keywords instead.", this);
            return;
        }
        StartCoroutine(Probe());
    }

    // The server may start before or after Play Mode, or restart to gain /spirit; follow whatever /health says.
    private IEnumerator Probe()
    {
        var pause = new WaitForSecondsRealtime(HealthEvery);
        while (client != null)
        {
            if (!broken && !inert && SpiritSignals.Enabled && GatekeeperDecisionService.UseD1)
            {
                float started = Time.realtimeSinceStartup;
                var health = client.HealthAsync(lifetime.Token);
                while (!health.IsCompleted) yield return null;
                string problem = health.Status == TaskStatus.RanToCompletion ? SpiritDecisionClient.Problem(health.Result)
                    : health.IsFaulted ? "offline (" + Short(health.Exception.GetBaseException()) + ")" : "offline";
                if (problem == null) healthSeconds = Time.realtimeSinceStartup - started;
                d1Ready = problem == null; d1Problem = problem ?? "";
                string logged = problem == null ? "ready" : problem.StartsWith("offline", StringComparison.Ordinal) ? "offline" : problem;
                if (logged != loggedD1)
                {
                    loggedD1 = logged;
                    Debug.Log(problem == null ? "[Spirit] d1 /spirit ready at " + client.Endpoint
                        : "[Spirit] Vesper reads thoughts with keywords: " + problem + " at " + client.Endpoint + ".", this);
                }
            }
            yield return pause;
        }
    }

    // null when a thought may go to d1; otherwise why the keywords answer.
    private string D1Problem()
    {
        if (d1Off || client == null) return d1Problem;
        if (!GatekeeperDecisionService.UseD1) return "d1 off (F4)";
        if (Time.realtimeSinceStartup < retryAt) return "waiting after a failed thought";
        return d1Ready ? null : string.IsNullOrEmpty(d1Problem) ? "connecting" : d1Problem;
    }

    private void D1Failed(Exception error)
    {
        if (error is TimeoutException || error is OperationCanceledException)
        {
            // A slow thought is no outage: keywords for 2.5 s, then d1 again (still ready, no failure counted).
            retryAt = Time.realtimeSinceStartup + 2.5f; d1Problem = "d1 too slow";
        }
        else if (error is SpiritEndpointMissingException) { d1Ready = false; d1Problem = "no /spirit (restart serve_decisions.py)"; }
        else
        {
            // Like Trola's service: back off 5, 10 ... 30 s, then the health probe reconnects.
            d1Ready = false; d1Failures++; retryAt = Time.realtimeSinceStartup + Mathf.Min(30, 5 * d1Failures);
            d1Problem = "d1 failed: " + Short(error);
        }
        if (d1Problem != loggedD1) { loggedD1 = d1Problem; Debug.LogWarning("[Spirit] d1 thought failed (" + Short(error) + "); keywords meanwhile.", this); }
    }

    // ---- Controller events ----

    private void OnChanged()
    {
        if (!built || broken || inert) return;
        try
        {
            var state = Controller.State;
            bool entered = state != lastState;
            lastState = state;
            if (!Audible)
            {
                // The golden rule: Trola is acting, so she snaps shut (voice .12 s, halo .2 s).
                arrivalPending = false;
                if (Window.IsOpen) Abort(SpiritCloseReason.TrolaActive);
                return;
            }
            if (entered) EnterCheckpoint(state);
        }
        catch (Exception error) { Fail(error); }
    }

    private void EnterCheckpoint(GatekeeperController.Phase state)
    {
        if (Window.IsOpen) Abort(SpiritCloseReason.TrolaActive);
        var c = BuildCheckpoint(state);
        Checkpoint = c;
        Advice = c.Stage == SpiritStage.Hub || c.Stage == SpiritStage.Reckoning ? Advisor.Advise(c.NodeId, c.Patience, c.HubRoundsDone, c.UsedTargets) : null;
        if (state == GatekeeperController.Phase.Ended) EndedAt = Time.realtimeSinceStartup;
        Mark(); // The view rebuilt its buttons in its own Changed handler, which runs before this one.
        if (!Active) return;
        // Her engine opens at the first wait, once Trola's own warm-up is done (SpiritVoice.Arm decides).
        if (!armed) { armed = true; Voice.Arm(); }
        // Arrival is the next frame: the checkpoint's buttons and prompt have settled by then.
        arrivalPending = true; arrivalFrame = Time.frameCount; arrivalState = state;
    }

    private SpiritCheckpoint BuildCheckpoint(GatekeeperController.Phase state)
    {
        var labels = new List<string>();
        foreach (var choice in Controller.CurrentChoices) if (choice != null) labels.Add(choice.label ?? "");
        var stage = StageOf(state, labels, out string node);
        int turns = Controller.Turns, patience = Controller.Patience;
        string key = stage + ":" + turns;
        visits.TryGetValue(key, out int visit);
        visits[key] = visit + 1; // A re-entry after a taunt (or a cut-off taunt) is a later visit of the same checkpoint.
        var snapshot = Controller.Conversation;
        var c = new SpiritCheckpoint
        {
            Stage = stage, NodeId = node, Patience = patience, LastDelta = patienceAtCheckpoint >= 0 ? patience - patienceAtCheckpoint : 0,
            HubRoundsDone = turns, Visit = visit, Choices = labels,
            UsedTargets = snapshot != null ? snapshot.Approaches : (IReadOnlyList<string>)Array.Empty<string>(),
            NameAssigned = Controller.NameWasAssigned, Interrupted = interruptedSince, Taunted = tauntedSince, FirstSession = encounters <= 1,
            Outcome = Controller.Outcome, PlayerName = Controller.PlayerName ?? "", LastChoiceLabel = lastChoiceLabel,
            TrolaLastLineId = trolaLastLineId, TrolaLastDelta = trolaLastDelta, Now = Now
        };
        patienceAtCheckpoint = patience; interruptedSince = tauntedSince = false;
        return c;
    }

    // Choices are matched to a story node by their labels; unknown labels keep her silent rather than guessing.
    private SpiritStage StageOf(GatekeeperController.Phase state, IReadOnlyList<string> labels, out string node)
    {
        node = "";
        switch (state)
        {
            case GatekeeperController.Phase.AwaitingName: return SpiritStage.Name;
            case GatekeeperController.Phase.AwaitingBusiness: return SpiritStage.Business;
            case GatekeeperController.Phase.Ended: return SpiritStage.Ended;
            case GatekeeperController.Phase.AwaitingInput:
                if (labels.Count == 0) return Controller.Prompt.InputSlot == GatekeeperInputSlot.Reply ? SpiritStage.FreeTalk : SpiritStage.None;
                node = Advisor.NodeFor(labels);
                return node == SpiritAdvisor.ReckoningNode ? SpiritStage.Reckoning : node == SpiritAdvisor.HubNode ? SpiritStage.Hub : SpiritStage.None;
            default: return SpiritStage.None;
        }
    }

    // Raised before the controller acts on the input: note the choice against the checkpoint it was made at.
    private void OnPlayerActed(GatekeeperInputKind kind, string label)
    {
        if (!built || broken || inert) return;
        try
        {
            arrivalPending = false;
            bool aggressive = false;
            if (kind == GatekeeperInputKind.Choice)
            {
                lastChoiceLabel = label ?? "";
                tauntedSince = false; // A click can answer an idle taunt; the taunt must not make the next checkpoint a re-entry.
                if (Checkpoint != null) Policy.NoteChoice(Checkpoint, lastChoiceLabel);
                SpiritOption chosen = null;
                if (Advice != null) foreach (var option in Advice.Options) if (option.Label == lastChoiceLabel) chosen = option;
                aggressive = chosen != null && chosen.Aggressive;
                Note("choice: " + lastChoiceLabel + (chosen != null ? " (" + chosen.Verdict + (chosen.Aggressive ? ", aggressive" : "") + ")" : ""));
            }
            else if (kind == GatekeeperInputKind.Interrupt) { interruptedSince = true; Policy.NoteInterrupted(); Note("interrupted Trola"); }
            if (Window.IsOpen) Abort(SpiritCloseReason.PlayerActed);
            if (Active && (aggressive || kind == GatekeeperInputKind.Interrupt)) Flinch(aggressive);
        }
        catch (Exception error) { Fail(error); }
    }

    // No words and no hold: a red pulse, a gasp (cut when Trola speaks), and the camp goes quiet.
    private void Flinch(bool aggressive)
    {
        Presence.Flinch();
        Voice.PlaySfx(SpiritSound.Gasp, .8f);
        try { SpiritSignals.Flinch(aggressive); }
        catch (Exception error) { Debug.LogException(error, this); } // A listener's failure is not hers.
        Note(aggressive ? "flinch: aggressive choice" : "flinch: interruption");
    }

    private void OnTrolaLine(GatekeeperScenario.Line line)
    {
        if (!built || broken || inert || line == null) return;
        try
        {
            string id = line.id ?? "";
            Policy.NoteTrolaLine(id, line.patience_delta, Controller.IsReaction);
            trolaLastLineId = id; trolaLastDelta = line.patience_delta;
            if (id.StartsWith("idle-", StringComparison.Ordinal)) tauntedSince = true;
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnRestarted()
    {
        if (!built || broken) return;
        try
        {
            Abort(SpiritCloseReason.Restart);
            arrivalPending = false; encounters++;
            Policy.ResetEncounter(encounters == 1);
            visits.Clear(); transcript.Clear(); spiritSaid = "";
            interruptedSince = tauntedSince = false; lastChoiceLabel = trolaLastLineId = ""; trolaLastDelta = 0; patienceAtCheckpoint = -1;
            Checkpoint = null; Advice = null; Moment = null; MomentLabel = ""; EndedAt = -1;
            if (Presence != null) Presence.MarkChoices(null, false);
            Note("encounter " + encounters + (encounters == 1 ? " (first session)" : ""));
        }
        catch (Exception error) { Fail(error); }
    }

    // ---- Moments ----

    private void Arrive()
    {
        arrivalPending = false;
        if (Window.IsOpen || !Audible || Controller.State != arrivalState || Checkpoint == null) return;
        var plan = Policy.AtCheckpoint(At(), true);
        if (plan != null) Begin(plan);
    }

    // Tab at the hub or reckoning: a one-reply counsel, within the caps.
    private void Summon()
    {
        if (!Active || Window.IsOpen || arrivalPending || Controller.State != GatekeeperController.Phase.AwaitingInput || Checkpoint == null) return;
        if (Checkpoint.Stage != SpiritStage.Hub && Checkpoint.Stage != SpiritStage.Reckoning) return;
        var plan = Policy.Summon(At());
        if (plan != null) Begin(plan);
        else Note("Tab: no counsel left in this encounter");
    }

    private void Begin(SpiritMomentPlan plan)
    {
        var c = Checkpoint;
        Moment = plan; momentOpen = true;
        MomentLabel = StageName(c) + " " + plan.Kind.ToString().ToLowerInvariant() + (plan.Trigger == SpiritTrigger.Summon ? " (Tab)" : "");
        Window.Open(plan.Kind, plan.MaxReplies, plan.ListenSeconds);
        openingAt = Time.realtimeSinceStartup + OpeningDelay; openingStarted = false; after = After.Opening;
        SyncHold(); // Arrival freezes Trola's clock before anything is heard.
        Presence.Show(plan.Kind, SpiritAdvisor.Band(c.Patience));
        Voice.PlaySfx(SpiritSound.BreathIn, .5f);
        foreach (var cue in plan.Opening) Voice.Prefetch(cue); // His worker is idle while he waits.
        foreach (var cue in plan.Opening)
            if (cue.Family == "end.liked" || cue.Family == "end.passed") { Presence.Warm(3.5f); break; }
        var families = new StringBuilder();
        foreach (var cue in plan.Opening) families.Append(families.Length > 0 ? " + " : "").Append(cue.Family).Append(cue.Key.Length > 0 ? "." + cue.Key : "");
        Note(MomentLabel + " (" + plan.Trigger + "): " + families);
    }

    // Speaks cues on her voice, in order, under the current window token. When the last one ends, `then` continues.
    private void Speak(IReadOnlyList<SpiritCue> cues, After then)
    {
        after = then;
        foreach (var cue in cues)
            if (cue != null && !string.IsNullOrWhiteSpace(cue.Text)) { speaking.Add(cue); queued.Add(cue); }
        speechDeadline = Time.realtimeSinceStartup + Lines.timing.voice_wait + LineWatchdog;
        if (speaking.Count == 0) Continue();
        else { Presence.SetSpeakingDim(true); SayNext(); }
        SyncHold();
    }

    // Hands the next cue to her voice once the previous one finished. Called from LineFinished, inside her play loop,
    // so the line follows without a gap; a prefetched clip may even start (and raise LineStarted) inside Say.
    private void SayNext()
    {
        if (queued.Count == 0 || speaking.Count != queued.Count) return;
        var cue = queued[0];
        queued.RemoveAt(0);
        Voice.Say(cue, Window.Token);
    }

    private void OnLineStarted(SpiritCue cue)
    {
        if (!built || broken || !speaking.Contains(cue) || Voice.Token != Window.Token) return;
        try
        {
            speechDeadline = Time.realtimeSinceStartup + Lines.timing.voice_wait + LineWatchdog;
            Presence.ShowLine(cue.Text, !nameShown); nameShown = true;
            Policy.NoteSpoken(cue);
            if (CarriesAdvice(cue) && !string.IsNullOrEmpty(cue.Text)) spiritSaid = cue.Text;
            AddTurn("Spirit", cue.Text);
            if (thought != null && thought.Cue == cue)
            {
                thought.Silent = Voice.IsSilentLine;
                thought.VoiceMs = thought.Silent ? -1 : Voice.LastSynthesisSeconds * 1000;
                thought.FirstAudioMs = (Time.realtimeSinceStartup - thought.SubmittedAt) * 1000;
            }
            Note("Vesper: \"" + cue.Text + "\" [" + cue.LineId + (Voice.IsSilentLine ? ", subtitle only" : "") + "]");
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnLineFinished(SpiritCue cue)
    {
        if (!built || broken || Voice.Token != Window.Token || !speaking.Remove(cue)) return;
        try
        {
            speechDeadline = Time.realtimeSinceStartup + Lines.timing.voice_wait + LineWatchdog;
            if (queued.Count > 0) SayNext();
            else if (speaking.Count == 0) Continue();
            SyncHold();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Continue()
    {
        switch (after)
        {
            case After.Opening: Window.OpeningFinished(); Next(false); break;
            case After.Answer: Window.AnswerFinished(); Next(true); break;
            case After.Closing: FinishClose(); break;
            default: if (Window.State == SpiritWindowState.Listening) Presence.SetSpeakingDim(false); break; // The draft warning: the field stays open.
        }
    }

    private void Next(bool answered)
    {
        if (Window.State == SpiritWindowState.Listening)
        {
            if (answered) Presence.ClearLine(); // Her answer and its label have done their job.
            Presence.SetSpeakingDim(false);
            Presence.OpenField(Lines.timing.reply_chars);
        }
        else if (Window.State == SpiritWindowState.Closing) Closing();
        else if (Window.State == SpiritWindowState.Closed) FinishClose();
    }

    // Budget and Replies close with a spoken line (clock held while she says it); everything else fades quietly.
    private void Closing()
    {
        var reason = Window.CloseReason;
        var cue = reason == SpiritCloseReason.Budget || reason == SpiritCloseReason.Replies ? Policy.CutOff(At(), reason) : null;
        Presence.CloseField(reason == SpiritCloseReason.Budget); // A cut-off draft dissolves with a strike-through.
        if (cue == null || string.IsNullOrWhiteSpace(cue.Text)) { FinishClose(); return; }
        Window.BeginClosingLine();
        Note("cut-off: " + reason);
        one[0] = cue; Speak(one, After.Closing);
    }

    private void FinishClose()
    {
        if (Window.IsOpen) { Window.Closed(); Presence.Hide(false); Note("closed: " + Window.CloseReason); }
        EndMoment();
    }

    // Snap: voice fade .12 s, halo out .2 s, the field and every hold gone; in-flight results go stale with the token.
    private void Abort(SpiritCloseReason reason)
    {
        bool open = Window.IsOpen;
        Window.Abort(reason);
        if (thought != null && thought.Cancel != null && !thought.Answered) thought.Cancel.Cancel();
        if (open)
        {
            Voice.Stop(.12f);
            Presence.Hide(true);
            Note("snap: " + reason);
        }
        EndMoment();
    }

    private void EndMoment()
    {
        speaking.Clear(); queued.Clear();
        if (momentOpen) { momentOpen = false; Policy.Scheduler.Ended(Now); }
        SyncHold();
    }

    // ---- The mind field and decisions ----

    private void OnSubmitted(string text)
    {
        if (!built || broken || inert) return;
        try
        {
            text = (text ?? "").Trim();
            if (!Active || Window.State != SpiritWindowState.Listening || text.Length == 0 || Checkpoint == null) return;
            var c = At();
            if (!Window.Submit(text.Length)) { if (Window.InputOpen) Presence.OpenField(Lines.timing.reply_chars); return; }
            var t = new Thought { Number = ++Thoughts, Text = text, Token = Window.Token, SubmittedAt = Time.realtimeSinceStartup };
            thought = t;
            Presence.ShowThinking(text);
            var request = Request(c, text);
            AddTurn("Player", text); // Her transcript only: never GatekeeperConversationState, so Gemma and Trola's d1 never see it.
            string why = D1Problem();
            if (why == null)
            {
                t.Cancel = new CancellationTokenSource();
                t.Pending = client.DecideAsync(request, t.Cancel.Token);
                t.Pending.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            else Keywords(t, why);
            SyncHold();
        }
        catch (Exception error) { Fail(error); }
    }

    private SpiritRequest Request(SpiritCheckpoint c, string text)
    {
        var options = new string[c.Choices.Count];
        for (int i = 0; i < options.Length; i++) options[i] = c.Choices[i];
        // A short prompt keeps d1 quick: the last two turns, never repeating spirit_said.
        var history = new List<SpiritTurn>(HistoryTurns);
        for (int i = transcript.Count - 1; i >= 0 && history.Count < HistoryTurns; i--)
            if (transcript[i].speaker != "Spirit" || transcript[i].text != spiritSaid) history.Insert(0, transcript[i]);
        // No patience number ever leaves the game, only the mood band.
        return new SpiritRequest
        {
            message = text, name = Controller.PlayerName ?? "", business = Controller.Business ?? "", stage = StageLabel(c.Stage),
            mood = SpiritAdvisor.D1Mood(SpiritAdvisor.Band(c.Patience)), spirit_said = spiritSaid, options = options, history = history.ToArray()
        };
    }

    private void Think()
    {
        var t = thought;
        if (t == null || t.Token != Window.Token) { Abort(SpiritCloseReason.Timeout); return; }
        if (t.Decision == null && t.Pending != null)
        {
            if (t.Pending.IsCompleted) Collect(t);
            else if (Time.realtimeSinceStartup - t.SubmittedAt > Lines.timing.decision_timeout + .3f)
            {
                // The client's own decision_timeout should already have fired; never wait on a stuck task.
                t.Cancel?.Cancel();
                D1Failed(new TimeoutException("no answer within " + Lines.timing.decision_timeout.ToString("0.0#") + " s"));
                Keywords(t, "d1 slower than " + Lines.timing.decision_timeout.ToString("0.0#") + " s");
            }
        }
        if (t.Decision != null && Time.realtimeSinceStartup - t.SubmittedAt >= LabelDelay) Answer(t);
    }

    private void Collect(Thought t)
    {
        var task = t.Pending;
        if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
        {
            t.Decision = task.Result; d1Failures = 0;
            d1Milliseconds.Add(t.Decision.Ms);
            return;
        }
        var error = task.Exception?.GetBaseException() ?? (Exception)new OperationCanceledException("d1 thought cancelled");
        D1Failed(error);
        Keywords(t, error is TimeoutException ? "d1 timed out" : error is SpiritEndpointMissingException ? "no /spirit" : "d1 failed");
    }

    private void Keywords(Thought t, string why)
    {
        var stage = Checkpoint != null ? Checkpoint.Stage : SpiritStage.Hub;
        t.Decision = SpiritKeywords.Interpret(t.Text, stage);
        t.Decision.Detail += " (" + why + ")";
        KeywordDecisions++;
    }

    // The label shows before her voice; the rules (never d1) pick what she says.
    private void Answer(Thought t)
    {
        t.Answered = true;
        var d = t.Decision;
        t.LabelMs = (Time.realtimeSinceStartup - t.SubmittedAt) * 1000;
        var cue = Policy.Answer(At(), d, Math.Max(0, Window.RepliesUsed - 1), Moment != null ? Moment.MaxReplies : 1);
        t.Cue = cue; t.Label = LabelFor(d);
        // A draft warning still playing is moot once the thought was sent: cut it so the answer is not queued behind it.
        if (speaking.Count > 0) { Voice.Stop(.05f); speaking.Clear(); queued.Clear(); }
        if (Lines.rules.label_in_fiction || Lens.Visible) Presence.ShowLabel(t.Label, cue.Alarm || d.Reckless);
        Voice.PlaySfx(SpiritSound.Shimmer, .25f);
        Window.AnswerReady(cue.CloseAfter);
        Note("#" + t.Number + " \"" + t.Text + "\" → " + d.Detail + " → " + cue.Rule);
        one[0] = cue; Speak(one, After.Answer);
    }

    // d1's own label, e.g. "state_plan · threaten"; the presence turns underscores into spaces.
    public static string LabelFor(SpiritDecision d)
    {
        if (d == null) return "unclear";
        if (d.Scope != SpiritScope.InWorld) return Snake(d.Scope.ToString());
        if (d.ToTrola) return "to_trola";
        string label = Snake(d.Intent.ToString());
        return d.Plan != SpiritPlan.None && d.PlanP >= .6f ? label + " · " + Snake(d.Plan.ToString()) : label;
    }

    private static string Snake(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) text.Append('_');
            text.Append(char.ToLowerInvariant(name[i]));
        }
        return text.ToString();
    }

    private static string StageLabel(SpiritStage stage) => stage == SpiritStage.Name ? "name" : stage == SpiritStage.Business ? "business"
        : stage == SpiritStage.Reckoning ? "reckoning" : stage == SpiritStage.FreeTalk ? "free_talk" : "hub";

    private static string StageName(SpiritCheckpoint c) => c == null ? "" : c.Stage == SpiritStage.Hub ? "hub-" + (c.HubRoundsDone + 1)
        : c.Stage == SpiritStage.FreeTalk ? "free talk" : c.Stage.ToString().ToLowerInvariant();

    // Framing lines never become spirit_said, so "are you sure?" after the first counsel's teach line (or a draft
    // warning) still points d1 at her advice, as in the recorded /spirit cases; history carries the rest.
    private static bool CarriesAdvice(SpiritCue cue) => cue.Family != "teach" && cue.Family != "summon" && cue.Family != "hub.open"
        && !cue.Family.StartsWith("react.", StringComparison.Ordinal) && !cue.Family.StartsWith("close.", StringComparison.Ordinal);

    private void AddTurn(string speaker, string text)
    {
        transcript.Add(new SpiritTurn { speaker = speaker, text = text });
        if (transcript.Count > TranscriptTurns) transcript.RemoveRange(0, transcript.Count - TranscriptTurns);
    }

    private SpiritCheckpoint At() { Checkpoint.Now = Now; return Checkpoint; }

    // ---- Frame ----

    private void Update()
    {
        if (!built) return;
        try
        {
            Keys();
            if (!broken && !inert) Tick();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Keys()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.f6Key.wasPressedThisFrame) { Lens.Visible = !Lens.Visible; if (!broken && !inert) Mark(); }
        if (broken || inert) return;
        if (keyboard.f7Key.wasPressedThisFrame) ToggleSpirit();
        if (keyboard.f8Key.wasPressedThisFrame && Window.IsOpen) Abort(SpiritCloseReason.Escape); // F8 mutes the camp; she lets go too.
        if (keyboard.tabKey.wasPressedThisFrame) Summon();
        if (keyboard.f9Key.wasPressedThisFrame && Voice.SelectNextVoice()) { Note("voice: " + Voice.VoiceName + " (F9)"); PreviewVoice(); }
    }

    private void Tick()
    {
        if (Controller == null) { Shutdown("the gatekeeper is gone"); return; }
        float now = Time.realtimeSinceStartup;
        if (now >= nextInertCheck) { nextInertCheck = now + InertEvery; CheckInert(); if (inert) return; }
        if (!Active) { if (Window.IsOpen) Abort(SpiritCloseReason.Disabled); arrivalPending = false; SyncHold(); return; }
        if (Window.IsOpen && !Audible) Abort(SpiritCloseReason.TrolaActive); // Backstop for the golden rule.
        if (arrivalPending && Time.frameCount > arrivalFrame) Arrive();
        if (Window.State == SpiritWindowState.Speaking && !openingStarted && now >= openingAt)
        {
            openingStarted = true;
            Speak(Moment.Opening, After.Opening);
        }
        if (Window.State == SpiritWindowState.Listening)
        {
            // Listening time stops while her voice plays (the draft warning).
            bool voiced = speaking.Count > 0 || Voice.IsSpeaking;
            var happened = Window.Tick(voiced ? 0 : Time.unscaledDeltaTime, Presence.Draft.Length);
            if (happened == SpiritWindowEvent.DraftTooLong)
            {
                var cue = Policy.DraftWarning(At());
                if (cue != null) { one[0] = cue; Speak(one, After.Warning); }
            }
            else if (happened == SpiritWindowEvent.Close) Closing();
        }
        if (Window.State == SpiritWindowState.Thinking) Think();
        if (Window.IsOpen && speaking.Count > 0 && now > speechDeadline)
        {
            Debug.LogWarning("[Spirit] Her line did not finish in time; letting the moment go.", this);
            Abort(SpiritCloseReason.Timeout);
        }
        // The ring counts down the reply window, so it appears once she listens (not during her opening lines).
        if (Window.State == SpiritWindowState.Listening || Window.State == SpiritWindowState.Thinking || Window.State == SpiritWindowState.Answering)
            Presence.SetCountdown(Window.ListenRemaining01, Impatience01);
        Presence.SetVoiceEnvelope(Voice.Envelope);
        SyncHold();
    }

    // One clock: held while she speaks, d1 thinks, she answers or says a cut-off; running while the player reads or types.
    private void SyncHold()
    {
        if (Controller == null) return;
        float now = Time.realtimeSinceStartup;
        if (holding && !Controller.IsIdleHeld) holding = false; // Restart, Halt or the controller's watchdog cleared it.
        bool want = Active && Window.IsOpen && (Window.HoldsIdleClock || speaking.Count > 0) && Controller.IsWaitingForPlayer;
        if (holdSpent && (Window.State != spentState || Window.Token != spentToken)) holdSpent = false;
        if (want && holding && now - heldSince > MaxHoldSeconds)
        {
            holdSpent = true; spentState = Window.State; spentToken = Window.Token;
            Note("hold released after " + MaxHoldSeconds + " s; Trola's clock runs");
        }
        if (holdSpent) want = false;
        if (want == holding) return;
        if (want) { Controller.HoldIdle(this); heldSince = now; }
        else Controller.ReleaseIdle(this);
        holding = want;
    }

    private void ReleaseHold()
    {
        if (holding && Controller != null) Controller.ReleaseIdle(this);
        holding = false;
    }

    private void OnEscaped()
    {
        if (!built || broken || inert) return;
        try { if (Window.IsOpen) Abort(SpiritCloseReason.Escape); }
        catch (Exception error) { Fail(error); }
    }

    // A voice audition from F4 or F9: her opening hub line in the current voice, without halo or holds. Only between
    // moments and while Trola waits, so it never cuts him or her; a moment that opens meanwhile cuts the preview instead.
    public bool CanPreviewVoice => Active && !Window.IsOpen && Voice != null && Voice.CanSynthesize;
    public bool PreviewVoice()
    {
        if (!CanPreviewVoice) return false;
        var line = Lines.lines.Find(l => l.family == "hub.open") ?? Lines.lines.Find(l => l.family == "intro");
        if (line == null) return false;
        string name = string.IsNullOrWhiteSpace(Controller.PlayerName) || Controller.NameWasAssigned ? SpiritLines.Traveller : Controller.PlayerName;
        Voice.Say(new SpiritCue { LineId = "voice-preview", Family = "preview", Text = SpiritLines.Resolve(line.text, name, Controller.PlayerName, true) }, -++previews);
        Note("voice preview: " + Voice.VoiceName);
        return true;
    }

    // F7: off snaps her shut and removes every effect; on waits for the next checkpoint.
    private void ToggleSpirit()
    {
        SpiritSignals.Enabled = !SpiritSignals.Enabled;
        if (!SpiritSignals.Enabled)
        {
            Abort(SpiritCloseReason.Disabled);
            arrivalPending = false;
            Presence.MarkChoices(null, false);
            Presence.enabled = false; Voice.enabled = false; // OnDisable restores the world and stops her audio.
        }
        else { Presence.enabled = true; Voice.enabled = true; Mark(); }
        Debug.Log("[Spirit] Vesper " + (SpiritSignals.Enabled ? "on" : "off") + " (F7).", this);
    }

    // A self-test or speech validation makes her stand down for good; a suspended controller pauses her.
    private void CheckInert()
    {
        if (UnderTest()) { StandDown("a self-test or speech validation is running"); return; }
        if (Controller.IsSuspended && Window.IsOpen) Abort(SpiritCloseReason.Disabled);
    }

    private void StandDown(string reason)
    {
        if (inert) return;
        Abort(SpiritCloseReason.Disabled);
        inert = true; inertReason = reason; arrivalPending = false;
        ReleaseHold();
        if (Presence != null) { Presence.MarkChoices(null, false); Presence.enabled = false; }
        if (Voice != null) Voice.enabled = false;
        Debug.Log("[Spirit] Vesper stands down: " + reason + ".", this);
    }

    private void Mark()
    {
        if (Presence == null || Controller == null) return;
        bool show = Lens != null && Lens.Visible && Active && Advice != null && Controller.State == GatekeeperController.Phase.AwaitingInput;
        Presence.MarkChoices(show ? Advice.Options : null, show);
    }

    // ---- Errors ----

    private void Fail(Exception error)
    {
        errors++;
        lastError = error.GetType().Name + ": " + error.Message;
        Debug.LogException(error, this);
        if (errors >= MaxErrors) Shutdown("after " + errors + " errors");
    }

    private void Shutdown(string reason)
    {
        if (broken) return;
        broken = true;
        try { Window.Abort(SpiritCloseReason.Disabled); speaking.Clear(); queued.Clear(); if (thought?.Cancel != null) thought.Cancel.Cancel(); } catch (Exception) { }
        ReleaseHold();
        Unsubscribe();
        try
        {
            if (Presence != null) { Presence.MarkChoices(null, false); Presence.enabled = false; }
            if (Voice != null) Voice.enabled = false;
        }
        catch (Exception) { } // Already failing; Trola must carry on regardless.
        if (string.IsNullOrEmpty(lastError)) lastError = reason;
        Debug.LogWarning("[Spirit] Vesper is off for this session (" + reason + "); Trola carries on.", this);
    }

    private void Unsubscribe()
    {
        if (Controller != null)
        {
            Controller.Changed -= OnChanged; Controller.PlayerActed -= OnPlayerActed;
            Controller.LineStarted -= OnTrolaLine; Controller.Restarted -= OnRestarted;
        }
        if (Presence != null) { Presence.Submitted -= OnSubmitted; Presence.Escaped -= OnEscaped; }
        if (Voice != null) { Voice.LineStarted -= OnLineStarted; Voice.LineFinished -= OnLineFinished; }
    }

    // ---- Reporting ----

    public string Describe()
    {
        if (!built) return "Spirit: not installed";
        if (Status.Length > 0) return Status;
        return "Spirit: " + Lines.spirit_name + " · " + Voice.Describe() + " · " + DecisionStatus;
    }

    private void Note(string message)
    {
        journal.Add(Time.realtimeSinceStartup.ToString("0.0") + " s  " + message);
        if (journal.Count > JournalSize) journal.RemoveRange(0, journal.Count - JournalSize);
    }

    private static string Short(Exception error)
    {
        string message = error?.Message ?? "unknown error";
        return message.Length > 120 ? message.Substring(0, 120) + "…" : message;
    }

    private void OnDisable()
    {
        if (!built) return;
        ReleaseHold();
        if (broken || !Window.IsOpen) return;
        // Leaving Play Mode tears objects down in no fixed order; her voice and presence restore themselves on disable.
        try { Abort(SpiritCloseReason.Disabled); } catch (Exception) { }
    }

    private void OnDestroy()
    {
        lifetime.Cancel();
        if (thought?.Cancel != null) thought.Cancel.Cancel();
        Unsubscribe();
        ReleaseHold();
        if (Instance == this) { Instance = null; SpiritSignals.Describer = null; }
    }
}
