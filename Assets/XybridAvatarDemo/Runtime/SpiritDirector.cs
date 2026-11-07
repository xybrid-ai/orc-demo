using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Advice only. Choice visibility, player input and idle holds belong to GatekeeperView / PlayerVoiceInput.
// No SpiritWindow, reply field, listening timer or /spirit request is installed in the encounter.
// Before the encounter starts she also gives the road approach's one authored warning, and after Trola's strike her
// one authored reaction, both on ExperienceDirector's cues.
public sealed class SpiritDirector : MonoBehaviour
{
    public const string LinesResource = "Spirit/SpiritLines";
    private const int MaxErrors = 3, JournalSize = 40;
    private const float OpeningDelay = .35f, LineWatchdog = 30;
    private static bool explanationUsed, approachWarned, defeatSeen;

    public static SpiritDirector Instance { get; private set; }
    public GatekeeperController Controller { get; private set; }
    public SpiritLines Lines { get; private set; }
    public SpiritAdvisor Advisor { get; private set; }
    public SpiritVoice Voice { get; private set; }
    public SpiritPresence Presence { get; private set; }
    public SpiritLens Lens { get; private set; }
    public SpiritCheckpoint Checkpoint { get; private set; }
    public SpiritCue CurrentCue { get; private set; }
    public IReadOnlyList<string> Journal => journal;
    public bool IsAdvising => advicePending;
    // The road approach (ExperienceDirector, docs/approach.md): her warning is chosen and prefetched, said once on cue,
    // and ended before Trola speaks. Advice guards never cancel it while Trola has not started.
    public SpiritCue ApproachCue => approachCue;
    public bool IsWarning => approachSaying;
    // Trola's strike (docs/defeat.md): her reaction is chosen and prefetched as he winds up, said once the player is down,
    // and ended by a restart. While it is active nothing else she does may stop her voice.
    public SpiritCue ReactionCue => reactionCue;
    public bool IsReacting => reactionSaying;
    public int AdviceCount { get; private set; }
    public float EndedAt { get; private set; } = -1;
    public bool Active => built && !broken && SpiritSignals.Enabled && Controller != null && !Controller.IsSuspended;
    public string Status => broken ? "Spirit disabled for this session: " + lastError
        : !SpiritSignals.Enabled ? "Spirit off (F7)" : Controller != null && Controller.IsSuspended ? "Spirit paused: Trola is suspended" : "";

    private readonly HashSet<string> checkpoints = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<string> journal = new List<string>();
    private GatekeeperView view;
    private bool built, broken, advicePending, started, nameShown, previousRepeated, approachActive, approachSaying, reactionActive, reactionSaying;
    private SpiritCue approachCue, reactionCue;
    private int errors, token, arrivalFrame, previews;
    private float openingAt, deadline, nextTestCheck;
    private string lastError = "", lastChoice = "", lastApproach = "";

    // Encounter restart deliberately does not reset onboarding; a new Play session does, including without domain reload.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetPlaySession() { explanationUsed = approachWarned = defeatSeen = false; Instance = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SpiritSignals.Reset();
        if (HasArgument("-xybridNoSpirit") || HasArgument("-xybridSelfTest") || HasArgument("-xybridSpeechTest")) return;
        var controller = FindAnyObjectByType<GatekeeperController>();
        if (controller == null || controller.scenario == null || !controller.scenario.authoredConversation || UnderTest()) return;
        GameObject host = null;
        try
        {
            var asset = Resources.Load<TextAsset>(LinesResource);
            if (asset == null) throw new InvalidOperationException("Resources/" + LinesResource + ".json is missing");
            var lines = SpiritLines.Parse(asset.text);
            if (!lines.Validate(SpiritLines.AdviserRequirements(), out string error)) throw new InvalidOperationException("SpiritLines.json: " + error);
            var advisor = new SpiritAdvisor(controller.scenario.ReadStory(), lines.rules.reckoning_preference);
            host = new GameObject("Spirit (runtime)");
            host.AddComponent<SpiritDirector>().Build(controller, lines, advisor);
        }
        catch (Exception error)
        {
            if (host != null) Destroy(host);
            Instance = null;
            string reason = error.Message;
            Debug.LogWarning("[Spirit] Vesper stays off: " + reason + ". Trola is unaffected.");
            SpiritSignals.Describer = () => "Spirit: off (" + reason + ")";
        }
    }

    private static bool HasArgument(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    private static bool UnderTest() => FindAnyObjectByType<GatekeeperSelfTest>() != null || FindAnyObjectByType<GatekeeperSpeechValidation>() != null;

    private void Build(GatekeeperController controller, SpiritLines lines, SpiritAdvisor advisor)
    {
        Instance = this; Controller = controller; Lines = lines; Advisor = advisor;
        view = FindAnyObjectByType<GatekeeperView>();
        Presence = SpiritPresence.Create(view, FindAnyObjectByType<TrolaAtmosphere>(), lines.visual);
        Presence.transform.SetParent(transform, false);
        Presence.haloIn = lines.timing.halo_in; Presence.haloOut = lines.timing.halo_out; Presence.snapOut = lines.timing.snap_out;
        Presence.spiritName = lines.spirit_name;
        Voice = SpiritVoice.Create(transform, controller, lines.voice);
        Voice.voiceWait = lines.timing.voice_wait; Presence.voice = Voice;
        Lens = gameObject.AddComponent<SpiritLens>(); Lens.director = this; Lens.Visible = HasArgument("-xybridLens");
        controller.Changed += OnChanged; controller.PlayerActed += OnPlayerActed; controller.Restarted += OnRestarted;
        Voice.LineStarted += OnLineStarted; Voice.LineFinished += OnLineFinished;
        SpiritSignals.Describer = Describe;
        built = true;
        Note("installed: advice-only " + lines.spirit_name);
        OnChanged();
    }

    private void OnChanged()
    {
        if (!built || broken) return;
        try
        {
            if (Controller.State == GatekeeperController.Phase.Ended && EndedAt < 0) EndedAt = Time.realtimeSinceStartup;
            if (Controller.IsOpening && Active) { Voice.Arm(); return; } // No checkpoint yet; the approach owns her line.
            if (reactionActive) return; // The strike owns her line.
            if (!Controller.IsWaitingForPlayer || !Active)
            {
                CancelAdvice(true);
                return;
            }
            Voice.Arm(); // Warm her second engine at the first wait; speech is only offered at choice checkpoints.
            if (Controller.State == GatekeeperController.Phase.AwaitingInput && Controller.CurrentChoices.Count > 0) EnterCheckpoint();
        }
        catch (Exception error) { Fail(error); }
    }

    private void EnterCheckpoint()
    {
        var labels = new List<string>();
        foreach (var choice in Controller.CurrentChoices) if (choice != null) labels.Add(choice.label ?? "");
        string node = Advisor.NodeFor(labels);
        var approaches = Controller.Conversation?.Approaches ?? (IReadOnlyList<string>)Array.Empty<string>();
        var checkpoint = new SpiritCheckpoint
        {
            Stage = node == SpiritAdvisor.ReckoningNode ? SpiritStage.Reckoning : SpiritStage.Hub,
            NodeId = node, HubRoundsDone = Controller.Turns, Patience = Controller.Patience, Choices = labels,
            UsedTargets = approaches, LastChoiceLabel = lastChoice, LastApproach = lastApproach,
            PreviousApproachRepeated = previousRepeated, PlayerName = Controller.PlayerName ?? ""
        };
        // Patience and re-entry count are intentionally absent: neutral, clarification and idle return to the same key.
        if (!checkpoints.Add(checkpoint.AdviceKey)) return;
        CancelAdvice(true);
        Checkpoint = checkpoint;
        CurrentCue = Lines.AdviserCue(checkpoint, !explanationUsed);
        if (CurrentCue == null) return;
        explanationUsed = true; // An interrupted explanation is consumed too; never force the player to hear it again.
        advicePending = true; started = false; token++;
        arrivalFrame = Time.frameCount; openingAt = Time.realtimeSinceStartup + OpeningDelay;
        deadline = openingAt + Lines.timing.voice_wait + LineWatchdog;
        // Must happen synchronously: the view's no-advice preview begins two frames after checkpoint arrival.
        if (view != null) view.BeginChoiceAdvice();
        Presence.Show(SpiritMomentKind.Counsel, SpiritAdvisor.Band(checkpoint.Patience));
        Voice.Prefetch(CurrentCue);
        AdviceCount++;
        Note("scheduled " + checkpoint.AdviceKey + " → " + CurrentCue.LineId);
    }

    // A new approach: choose the warning (full on the Play session's first approach, short on replays), arm her voice and
    // let Update prefetch it while Trola's worker is idle.
    public void PrepareApproach()
    {
        EndApproach(true);
        if (!built || broken || !Active || !Controller.IsOpening) return;
        try
        {
            CancelAdvice(true);
            approachCue = Lines.ApproachCue(!approachWarned);
            if (approachCue == null) return;
            approachActive = true;
            Voice.Arm();
            Voice.Prefetch(approachCue);
            Note("approach prepared → " + approachCue.LineId);
        }
        catch (Exception error) { Fail(error); }
    }
    // Her warning's audio is ready or failed, or no voiced warning is coming: the walk need not wait for her.
    public bool ApproachReady => !approachActive || approachSaying || !Active || !Voice.Available || Voice.PrefetchSettled(approachCue.Text);
    public void SayApproach()
    {
        if (!approachActive || approachSaying || !Active || !Controller.IsOpening) return;
        try
        {
            approachSaying = true; approachWarned = true; // Consumed once said, even if skipped mid-line.
            Presence.Show(SpiritMomentKind.Whisper, SpiritAdvisor.Band(Controller.Patience), true);
            Voice.Say(approachCue, ++token);
            Note("approach warning → " + approachCue.LineId);
        }
        catch (Exception error) { Fail(error); }
    }
    // Before Trola speaks, on skip, restart or shutdown. snap: a fast fade (skip or interruption) rather than the normal one.
    public void EndApproach(bool snap)
    {
        if (!approachActive) return;
        bool saying = approachSaying;
        approachActive = approachSaying = false; approachCue = null; token++;
        if (Voice != null) Voice.Stop(snap ? .12f : .3f); // Also cancels a warning still being prefetched.
        if (Presence != null && saying) Presence.Hide(snap);
    }

    // Trola strikes: choose her reaction (the full one the first time in a Play session) and prefetch it while he swings.
    public void PrepareReaction()
    {
        EndReaction(true);
        if (!built || broken || !Active) return;
        try
        {
            CancelAdvice(true);
            reactionCue = Lines.DefeatCue(!defeatSeen);
            if (reactionCue == null) return;
            reactionActive = true;
            Voice.Arm();
            Voice.Prefetch(reactionCue);
            Note("defeat reaction prepared → " + reactionCue.LineId);
        }
        catch (Exception error) { Fail(error); }
    }
    public void SayReaction()
    {
        if (!reactionActive || reactionSaying || !Active) return;
        try
        {
            reactionSaying = true; defeatSeen = true;
            Presence.Show(SpiritMomentKind.Whisper, SpiritAdvisor.Band(Controller.Patience), true);
            Voice.Say(reactionCue, ++token);
            Note("defeat reaction → " + reactionCue.LineId);
        }
        catch (Exception error) { Fail(error); }
    }
    public void EndReaction(bool snap)
    {
        if (!reactionActive) return;
        bool saying = reactionSaying;
        reactionActive = reactionSaying = false; reactionCue = null; token++;
        if (Voice != null) Voice.Stop(snap ? .12f : .3f);
        if (Presence != null && saying) Presence.Hide(snap);
    }

    // Called by the root input owner on Option-down / suggestion click, before recording or acknowledging a choice.
    public void InterruptAdvice()
    {
        if (!built) return;
        CancelAdvice(true);
        if (Presence != null && Presence.Weight > 0) Presence.Hide(true); // Shorten any normal fade still under way.
        if (view != null) view.InterruptChoicePresentation(); // Also hides a preview after advice has already ended.
    }

    private void CancelAdvice(bool snap)
    {
        bool pending = advicePending;
        advicePending = false; started = false; token++;
        if (Voice != null && (pending || Voice.IsSpeaking)) Voice.Stop(snap ? .12f : 0);
        if (Presence != null && pending) Presence.Hide(snap);
        if (pending && view != null) view.InterruptChoicePresentation();
    }

    private void FinishAdvice()
    {
        if (!advicePending) return;
        advicePending = false; started = false;
        Presence.Hide(false);
        Note("advice complete");
        if (view != null) view.EndChoiceAdvice();
    }

    private void OnLineStarted(SpiritCue cue)
    {
        if ((approachSaying && ReferenceEquals(cue, approachCue) || reactionSaying && ReferenceEquals(cue, reactionCue)) && Voice.Token == token)
        {
            Presence.ShowLine(cue.Text, !nameShown); nameShown = true;
            Note("Vesper: " + cue.Text + (Voice.IsSilentLine ? " [subtitle only]" : ""));
            return;
        }
        if (!advicePending || !ReferenceEquals(cue, CurrentCue) || Voice.Token != token) return;
        Presence.ShowLine(cue.Text, !nameShown); nameShown = true;
        Note("Vesper: " + cue.Text + (Voice.IsSilentLine ? " [subtitle only]" : ""));
    }

    private void OnLineFinished(SpiritCue cue)
    {
        if (approachSaying && ReferenceEquals(cue, approachCue) && Voice.Token == token)
        {
            approachSaying = false; approachActive = false; approachCue = null;
            Presence.Hide(false); Note("approach warning complete");
            return;
        }
        if (reactionSaying && ReferenceEquals(cue, reactionCue) && Voice.Token == token)
        {
            reactionSaying = false; reactionActive = false; reactionCue = null;
            Presence.Hide(false); Note("defeat reaction complete");
            return;
        }
        if (!advicePending || !ReferenceEquals(cue, CurrentCue) || Voice.Token != token) return;
        try { FinishAdvice(); } catch (Exception error) { Fail(error); }
    }

    private void OnPlayerActed(GatekeeperInputKind kind, string label)
    {
        if (!built || broken) return;
        try
        {
            if (kind == GatekeeperInputKind.Choice)
            {
                lastChoice = label ?? "";
                lastApproach = Advisor.TargetForLabel(Checkpoint?.NodeId, lastChoice);
                previousRepeated = false;
                var used = Controller.Conversation?.Approaches;
                if (used != null) foreach (string target in used) if (target == lastApproach) previousRepeated = true;
            }
            InterruptAdvice();
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnRestarted()
    {
        if (!built) return;
        CancelAdvice(true); EndApproach(true); EndReaction(true);
        checkpoints.Clear(); Checkpoint = null; CurrentCue = null;
        lastChoice = lastApproach = ""; previousRepeated = false; EndedAt = -1;
        Note("encounter restarted; Play-session explanation remains consumed");
    }

    private void Update()
    {
        if (!built) return;
        try
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.f6Key.wasPressedThisFrame) Lens.Visible = !Lens.Visible;
                if (keyboard.f7Key.wasPressedThisFrame) ToggleSpirit();
                if (keyboard.f9Key.wasPressedThisFrame && Voice.SelectNextVoice()) PreviewVoice();
            }
            if (broken) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextTestCheck)
            {
                nextTestCheck = now + .5f;
                if (UnderTest()) { Shutdown("a self-test or speech validation is running"); return; }
            }
            if (approachActive)
            {
                // The encounter started without ending her warning (a debug restart): Trola is about to speak.
                if (!Active || !Controller.IsOpening) EndApproach(true);
                else if (approachSaying && !Voice.IsSpeaking) // Stopped without LineFinished (a voice swap): release the look.
                {
                    approachSaying = approachActive = false; approachCue = null;
                    Presence.Hide(false);
                }
                else
                {
                    if (!approachSaying && Voice.CanSynthesize) Voice.Prefetch(approachCue); // Retried until Trola's worker is idle.
                    Presence.SetVoiceEnvelope(Voice.Envelope);
                    return;
                }
            }
            if (reactionActive)
            {
                // A new encounter (or Vesper switched off) ends her reaction; otherwise nothing else may stop her voice.
                if (!Active || Controller.State != GatekeeperController.Phase.Ended) EndReaction(true);
                else if (reactionSaying && !Voice.IsSpeaking) // Stopped without LineFinished: release the look.
                {
                    reactionSaying = reactionActive = false; reactionCue = null;
                    Presence.Hide(false);
                }
                else
                {
                    if (!reactionSaying && Voice.CanSynthesize) Voice.Prefetch(reactionCue);
                    Presence.SetVoiceEnvelope(Voice.Envelope);
                    return;
                }
            }
            if (!Active || !Controller.IsWaitingForPlayer) { CancelAdvice(true); return; }
            Presence.SetVoiceEnvelope(Voice.Envelope);
            if (!advicePending) return;
            if (!started && Time.frameCount > arrivalFrame && now >= openingAt)
            {
                started = true;
                Voice.Say(CurrentCue, token);
            }
            if (now >= deadline || started && !Voice.IsSpeaking)
            {
                // Voice swaps/debug stops and failed coroutines must release the view's hold and start its preview.
                Voice.Stop(.12f);
                FinishAdvice();
            }
        }
        catch (Exception error) { Fail(error); }
    }

    public bool CanPreviewVoice => Active && !advicePending && !approachActive && !reactionActive && !Controller.IsOpening && Voice != null && !Voice.IsSpeaking && Voice.CanSynthesize;
    public bool PreviewVoice()
    {
        if (!CanPreviewVoice) return false;
        var line = Lines.lines.Find(l => l.family == "adviser.remark" && l.key == "none" && Controller.Patience >= l.min_patience && Controller.Patience <= l.max_patience);
        if (line == null) return false;
        Voice.Say(new SpiritCue { LineId = "voice-preview", Family = "preview", Text = line.text }, -++previews);
        Note("voice preview: " + Voice.VoiceName);
        return true;
    }

    private void ToggleSpirit()
    {
        SpiritSignals.Enabled = !SpiritSignals.Enabled;
        if (!SpiritSignals.Enabled)
        {
            CancelAdvice(false); EndApproach(false); EndReaction(false);
            Presence.enabled = false; Voice.enabled = false;
        }
        else { Presence.enabled = true; Voice.enabled = true; }
    }

    private void Fail(Exception error)
    {
        lastError = error.GetType().Name + ": " + error.Message;
        Debug.LogException(error, this);
        // Release the presentation immediately; one malformed callback must not strand the choice checkpoint.
        CancelAdvice(true); EndApproach(true); EndReaction(true);
        if (++errors >= MaxErrors) Shutdown(lastError);
    }

    private void Shutdown(string reason)
    {
        broken = true; lastError = reason;
        CancelAdvice(false); EndApproach(false); EndReaction(false);
        if (Presence != null) Presence.enabled = false;
        if (Voice != null) Voice.enabled = false;
        Debug.LogWarning("[Spirit] Vesper is off for this session (" + reason + ").", this);
    }

    public string Describe() => !built ? "Spirit: not installed" : Status.Length > 0 ? Status
        : "Spirit: " + Lines.spirit_name + " · advice only · " + Voice.Describe();

    private void Note(string message)
    {
        journal.Add(Time.realtimeSinceStartup.ToString("0.0") + " s  " + message);
        if (journal.Count > JournalSize) journal.RemoveRange(0, journal.Count - JournalSize);
    }

    private void OnDisable() { if (built) { CancelAdvice(false); EndApproach(false); EndReaction(false); } }
    private void OnDestroy()
    {
        if (Controller != null) { Controller.Changed -= OnChanged; Controller.PlayerActed -= OnPlayerActed; Controller.Restarted -= OnRestarted; }
        if (Voice != null) { Voice.LineStarted -= OnLineStarted; Voice.LineFinished -= OnLineFinished; }
        if (Instance == this) { Instance = null; SpiritSignals.Describer = null; }
    }
}
