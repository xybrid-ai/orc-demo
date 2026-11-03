using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Performs the line-source contract. It never reads nodes or concrete source types.
// A voice failure never stops the conversation: a line with no voice is held on screen as a
// subtitle, a line with audio but no lip sync plays with a loudness-driven jaw, and both are
// logged as "[Gatekeeper voice]" warnings. VoiceFailed is only for errors with no line to show
// (missing scene wiring, an invalid scenario, a line source that fails or times out).
public sealed class GatekeeperController : MonoBehaviour
{
    public enum Phase { Inactive, Speaking, AwaitingInput, Ended, AwaitingName, PreparingSpeech, VoiceFailed, AwaitingBusiness, PreparingLine, GateOpening }
    public enum VoiceFallback { None, LoudnessJaw, SubtitleOnly }
    public GatekeeperScenario scenario;
    public XybridAvatarPlayer character;
    public GatekeeperLiveSpeech liveSpeech;
    public bool autoStart = true;
    public float lineSourceTimeout = 45;
    [Tooltip("A line without a voice stays on screen for base + characters / rate seconds, within the minimum and maximum.")]
    public float subtitleHoldBase = 1.2f, subtitleCharactersPerSecond = 15, subtitleHoldMinimum = 2.5f, subtitleHoldMaximum = 8;
    public event Action Changed;
    public event Action<int, int> PatienceChanged;
    // Performance hooks: the resolved line as it starts playing, a player interruption, a restart.
    public event Action<GatekeeperScenario.Line> LineStarted;
    public event Action Interrupted;
    public event Action Restarted;
    // Where the wait before each line's first audio went; raised once the audio starts (or the subtitle, without a voice).
    public event Action<GatekeeperLineTiming> TimingRecorded;
    // A player choice, typed text (direct or queued) or interruption, raised before the controller acts on it.
    // Label: the choice label, the typed text, or "" for an interruption. Subscribers must not drive the controller.
    public event Action<GatekeeperInputKind, string> PlayerActed;
    public GatekeeperLineTiming LastTiming { get; private set; }
    public sealed class DebugLine
    {
        public GatekeeperScenario.Line line;
        public int patienceBefore, patienceAfter;
        public GatekeeperLiveSpeech.Generated voice;
        public string voiceError;
    }
    public readonly List<DebugLine> DebugLines = new List<DebugLine>();
    private DebugLine debugLine;
    public void DebugFreeForm(string name, string business, string approaches, GatekeeperOutcome ending, int patience)
    {
        var encounter = new GatekeeperEncounter(Mathf.Clamp(patience, 0, 5), 5, true);
        encounter.CaptureName(name, scenario.nameBlocklist, "Mira");
        encounter.CaptureBusiness(business, scenario.nameBlocklist);
        encounter.FinishStory(ending);
        var state = new GatekeeperConversationState(encounter) { GateOpenRequested = ending == GatekeeperOutcome.Passed || ending == GatekeeperOutcome.Liked };
        foreach (var approach in (approaches ?? "").Split(',')) if (!string.IsNullOrWhiteSpace(approach)) state.RecordApproach(approach.Trim());
        IGatekeeperLineSource next = scenario.debugUsePlaceholderReplies ? (IGatekeeperLineSource)new PlaceholderGatekeeperLineSource(true)
            : new FreeFormGatekeeperLineSource(scenario, GatekeeperLineSources.PrepareWriter(scenario), true, GatekeeperDecisionService.Prepare());
        UseLineSource(next, state);
    }
    private IGatekeeperLineSource source;
    private GatekeeperConversationState conversation;
    private GatekeeperEncounter Encounter => conversation?.Encounter;
    private static readonly GatekeeperPresentation EmptyPrompt = new GatekeeperPresentation();
    public GatekeeperPresentation Prompt => source?.Presentation ?? EmptyPrompt;
    public bool IsReaction => Prompt.IsReaction;
    public bool IsInterruptionReaction => Prompt.ProtectedReaction;
    public bool AcceptsText => IsWaiting && Prompt.InputSlot != GatekeeperInputSlot.None;
    [NonSerialized] private string queuedReply;
    public bool HasQueuedReply => !string.IsNullOrWhiteSpace(queuedReply);
    public string QueuedReplyText => queuedReply ?? "";
    // A persistent free-form field remains editable during voice preparation/playback.
    // Accept one reply without interrupting any clause; the performer submits it after
    // the current line finishes, provided the encounter has not ended.
    private bool CanQueueReply => !HasQueuedReply && Prompt.PersistentText && Prompt.InputSlot == GatekeeperInputSlot.Reply
        && Patience > 0 && CurrentLine != null && !CurrentLine.ends_scene
        && (State == Phase.PreparingSpeech || State == Phase.Speaking);
    public bool CanSubmitText => !HasQueuedReply && (AcceptsText || CanQueueReply);
    public bool GateOpenRequested => conversation?.GateOpenRequested ?? false;
    public bool IsWaitingForGate => State == Phase.GateOpening;
    public IReadOnlyList<GatekeeperChoice> CurrentChoices => Prompt.Choices;
    // Choices are live while he waits, and through an idle taunt at a choice checkpoint (the source sets
    // TauntKeepsChoices): a click during the taunt is the choice, never an interruption and never lost.
    public bool AcceptsChoice => source != null && CurrentChoices.Count > 0 && (State == Phase.AwaitingInput
        || performing == GatekeeperInputKind.Idle && Prompt.TauntKeepsChoices
           && (State == Phase.Inactive || State == Phase.PreparingLine || State == Phase.PreparingSpeech || State == Phase.Speaking));
    private GatekeeperInputKind performing = GatekeeperInputKind.Start; // The input the current performance answers; set before its first Changed.
    public GatekeeperConversationSnapshot Conversation => conversation?.Snapshot();
    public string Business => Encounter?.Business ?? "";
    public bool BusinessWasAssigned => Encounter?.BusinessWasAssigned ?? false;
    public Phase State { get; private set; }
    private float phaseStartedAt;
    public float PhaseElapsed => Mathf.Max(0, Time.realtimeSinceStartup - phaseStartedAt);
    public int SpeechStartedFrame { get; private set; } = -1;
    public string CurrentText { get; private set; } = "";
    // Subtitle reveal (GatekeeperView, docs/subtitles.md): how many phrases of CurrentText the voice has reached and the
    // newest one's fade-in (0-1); int.MaxValue shows the whole line. A line shows its first phrase until its voice starts,
    // then follows its clip; once the line is no longer playing (finished, skipped, no voice) it shows whole. It never
    // shrinks within a line, so an interrupted line keeps only what was heard until the next line replaces it.
    public int RevealedPhrases(out float newestAlpha)
    {
        bool whole = revealed == int.MaxValue || IsPreparedReaction;
        newestAlpha = whole ? 1 : revealAlpha;
        return whole ? int.MaxValue : revealed;
    }
    // A phrase shows 100 ms before its onset (the onset is where speech resumes after the pause, and real pauses are
    // 190-440 ms), then fades in over 150 ms, both in clip time.
    public const float RevealLeadSeconds = .1f, RevealFadeSeconds = .15f;
    private int revealed = int.MaxValue;
    private float revealAlpha = 1;
    private int[] revealClauses; // ClauseStarts while the line plays clause by clause; null for one clip per line
    public string CurrentLineId { get; private set; } = "";
    public GatekeeperScenario.Line CurrentLine { get; private set; }
    public bool IsWaitingForPlayer => IsWaiting;
    public float IdleElapsed => IsWaiting ? IdleNow - idleSince : 0;
    public float IdleTauntSeconds => Prompt.IdleSeconds;
    public bool IsSuspended => suspended;
    public GatekeeperMood Mood => Encounter?.Mood ?? GatekeeperMood.Bored;
    public GatekeeperOutcome Outcome => Encounter?.Outcome ?? GatekeeperOutcome.Playing;
    public int Patience => Encounter?.Patience ?? 0;
    public int Turns => Encounter?.Turns ?? 0;
    public string PlayerName => Encounter?.PlayerName ?? "";
    public string MangledName => Encounter?.MangledName ?? "";
    public bool NameWasAssigned => Encounter?.NameWasAssigned ?? false;
    public GatekeeperNameReading NameReading => Encounter?.NameReading ?? GatekeeperNameReading.Empty;
    public bool HasPendingOffer => Encounter?.HasPendingOffer ?? false;
    public bool CurrentWasLive { get; private set; }
    public GatekeeperLiveSpeech.Generated LastGenerated { get; private set; }
    public float PlaybackPreparationSeconds { get; private set; }
    // How the current line is delivered when its voice failed, and the last failure logged.
    public VoiceFallback CurrentFallback { get; private set; }
    public bool IsSilentLine { get; private set; }
    public float CurrentLineSeconds { get; private set; }
    public string LastVoiceFailure { get; private set; } = "";
    public int VoiceFailureCount { get; private set; }
    public float ReadableSeconds(string text) =>
        Mathf.Clamp(subtitleHoldBase + (text ?? "").Length / Mathf.Max(1, subtitleCharactersPerSecond), subtitleHoldMinimum, subtitleHoldMaximum);
    private float idleSince;
    // Owner-keyed holds freeze (never reset) the idle-taunt clock: the restlessness pauses, and on the last
    // release the clock resumes where it stopped. Holds never block Choose, Submit or Interrupt.
    private readonly HashSet<object> idleHolds = new HashSet<object>();
    private float idleHeldAt;
    private const float MaxIdleHoldSeconds = 20;
    private float IdleNow => idleHolds.Count > 0 ? idleHeldAt : Time.realtimeSinceStartup;
    public bool IsIdleHeld => idleHolds.Count > 0;
    public void HoldIdle(object owner) { if (owner != null && idleHolds.Add(owner) && idleHolds.Count == 1) idleHeldAt = Time.realtimeSinceStartup; }
    public void ReleaseIdle(object owner) { if (owner != null && idleHolds.Remove(owner) && idleHolds.Count == 0) idleSince += Time.realtimeSinceStartup - idleHeldAt; }
    private void ClearIdleHolds() { if (idleHolds.Count == 0) return; idleSince += Time.realtimeSinceStartup - idleHeldAt; idleHolds.Clear(); }
    // Each subscriber runs in isolation: a failing one is logged and never stops the input or the others.
    private void RaisePlayerActed(GatekeeperInputKind kind, string label)
    {
        var handlers = PlayerActed; if (handlers == null) return;
        foreach (Action<GatekeeperInputKind, string> handler in handlers.GetInvocationList())
            try { handler(kind, label ?? ""); } catch (Exception error) { Debug.LogException(error, this); }
    }
    private Coroutine performance;
    private int revision;
    private bool gateReady, suspended, holdSkipped;
    private CancellationTokenSource requestCancellation = new CancellationTokenSource();
    private readonly List<Task<GatekeeperLiveSpeech.Generated>> prefetch = new List<Task<GatekeeperLiveSpeech.Generated>>();
    // GatekeeperLineTiming.Now stamps: what started the wait for the next line, the line request, the line.
    private long triggerAt, lineRequestedAt, lineReadyAt;
    private string trigger = "";
    private GatekeeperReactions reactions;
    private long reactionFirstAt, reactionPlayAt;
    private string reactionId = "";
    public bool IsPreparedReaction { get; private set; }
    private float reactionStarted;
    public int PreparedReactionCount => reactions?.Count ?? 0;
    private void Awake() { reactions = new GatekeeperReactions(); }

    private IEnumerator Start()
    {
        if (!autoStart) yield break;
        int startupRevision = revision;
        yield return new WaitForSecondsRealtime(.5f);
        // A debug jump or suspension during startup owns the new conversation.
        if (autoStart && revision == startupRevision && !suspended) Restart();
    }
    public bool Restart()
    {
        if (suspended) return false;
        Cancel();
        if (Encounter != null) Encounter.PatienceChanged -= ForwardPatience;
        if (scenario == null || character == null || liveSpeech == null)
        { Halt("Assign scenario, character and live speech."); return false; }
        if (!GatekeeperLineSources.ValidateScenario(scenario, out var error)) { Halt(error); return false; }
        liveSpeech.ResetSession(); liveSpeech.Warm(); prefetch.Clear(); LastGenerated = null; CurrentText = ""; CurrentWasLive = false; RevealWhole();
        source = GatekeeperLineSources.Create(scenario, conversation?.AssignedNameIndex ?? 0, out conversation);
        Encounter.PatienceChanged += ForwardPatience;
        PatienceChanged?.Invoke(Patience, Patience);
        CurrentLine = null; ClearIdleHolds(); Restarted?.Invoke();
        Begin(new GatekeeperPlayerInput(GatekeeperInputKind.Start)); return true;
    }
    // Debug preview: stop the conversation without tearing down the scene; Resume restarts it.
    public void Suspend()
    {
        if (suspended) return;
        Cancel(); suspended = true; CurrentText = ""; CurrentLine = null; RevealWhole();
        SetState(Phase.Inactive);
    }
    public void Resume() { if (!suspended) return; suspended = false; Restart(); }
    // Allows a third source to use the exact same performer in integration checks.
    public void UseLineSource(IGatekeeperLineSource lineSource, GatekeeperConversationState state)
    {
        if (lineSource == null || state == null) throw new ArgumentNullException();
        Cancel(); if (Encounter != null) Encounter.PatienceChanged -= ForwardPatience;
        liveSpeech.ResetSession(); prefetch.Clear();
        source = lineSource; conversation = state; Encounter.PatienceChanged += ForwardPatience;
        PatienceChanged?.Invoke(Patience, Patience);
        CurrentLine = null; ClearIdleHolds(); Restarted?.Invoke();
        Begin(new GatekeeperPlayerInput(GatekeeperInputKind.Start));
    }
    public bool Submit(string text)
    {
        if (!CanSubmitText) return false;
        if (Prompt.InputSlot == GatekeeperInputSlot.Reply && (string.IsNullOrWhiteSpace(text) || text.Length > Prompt.CharacterLimit)) return false;
        RaisePlayerActed(GatekeeperInputKind.Text, text);
        if (!AcceptsText)
        {
            queuedReply = text;
            if (State == Phase.Speaking) character.ResumePlayback();
            Changed?.Invoke();
            return true;
        }
        Begin(new GatekeeperPlayerInput(GatekeeperInputKind.Text, text)); return true;
    }
    public bool Choose(int index)
    {
        if (!AcceptsChoice || index < 0 || index >= CurrentChoices.Count) return false;
        RaisePlayerActed(GatekeeperInputKind.Choice, CurrentChoices[index].label);
        Begin(new GatekeeperPlayerInput(GatekeeperInputKind.Choice, choice: index)); return true;
    }
    public void Skip()
    {
        if (Prompt.CanInterrupt) { Interrupt(); return; }
        if (State == Phase.Speaking) { character.StopSpeech(); holdSkipped = true; }
    }
    public void Interrupt()
    {
        if (source == null || State != Phase.Speaking || !Prompt.CanInterrupt || Prompt.ProtectedReaction) return;
        Interrupted?.Invoke();
        RaisePlayerActed(GatekeeperInputKind.Interrupt, "");
        Begin(new GatekeeperPlayerInput(GatekeeperInputKind.Interrupt));
    }
    public void NotifyActivity() { idleSince = IdleNow; }
    public void NotifyGateOpened() { if (IsWaitingForGate) gateReady = true; }
    private bool IsWaiting => State == Phase.AwaitingInput || State == Phase.AwaitingName || State == Phase.AwaitingBusiness;
    private void ForwardPatience(int previous, int current) { PatienceChanged?.Invoke(previous, current); }
    private void Update()
    {
        // A hold its owner forgot to release must not freeze Trola for the rest of the session.
        if (idleHolds.Count > 0 && Time.realtimeSinceStartup - idleHeldAt > MaxIdleHoldSeconds)
        {
            Debug.LogWarning("[Gatekeeper] Idle clock held for over " + MaxIdleHoldSeconds + " s; releasing " + idleHolds.Count + " hold(s).", this);
            ClearIdleHolds();
        }
        if (!suspended && source != null && IsWaiting && Prompt.IdleSeconds > 0 && IdleNow - idleSince >= Prompt.IdleSeconds)
            Begin(new GatekeeperPlayerInput(GatekeeperInputKind.Idle));
    }
    private void Begin(GatekeeperPlayerInput input)
    {
        MarkTrigger(input.Kind == GatekeeperInputKind.Text ? "typed input" : input.Kind == GatekeeperInputKind.Choice ? "choice"
            : input.Kind == GatekeeperInputKind.Start ? "start" : input.Kind == GatekeeperInputKind.Idle ? "idle taunt"
            : input.Kind == GatekeeperInputKind.Interrupt ? "interruption" : input.Kind.ToString());
        performing = input.Kind;
        Cancel(); SetState(Phase.Inactive);
        performance = StartCoroutine(Perform(input, revision));
    }
    private IEnumerator Perform(GatekeeperPlayerInput input, int version)
    {
        while (version == revision)
        {
            Task<GatekeeperScenario.Line> task;
            lineRequestedAt = GatekeeperLineTiming.Now;
            try
            {
                task = source.NextLineAsync(conversation, input, requestCancellation.Token);
                task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception error) { Halt(error.Message); yield break; }
            float started = Time.realtimeSinceStartup;
            if (!task.IsCompleted) SetState(Phase.PreparingLine);
            while (!task.IsCompleted && version == revision)
            {
                if (Time.realtimeSinceStartup - started > lineSourceTimeout) { Halt("Line source timed out."); yield break; }
                yield return null;
            }
            if (version != revision) yield break;
            if (task.IsCanceled || task.IsFaulted) { Halt(task.Exception?.GetBaseException().Message ?? "Line request cancelled."); yield break; }
            var line = task.Result;
            lineReadyAt = GatekeeperLineTiming.Now;
            if (line == null)
            {
                if (Prompt.WaitForPrefetch && prefetch.Exists(p => !p.IsCompleted))
                {
                    // Prefetched lines that are not ready in time fall back on their own when spoken.
                    started = Time.realtimeSinceStartup;
                    float budget = liveSpeech.WaitBudget;
                    SetState(Phase.PreparingSpeech);
                    while (prefetch.Exists(p => !p.IsCompleted) && version == revision && Time.realtimeSinceStartup - started <= budget) yield return null;
                    if (version != revision) yield break;
                    if (prefetch.Exists(p => !p.IsCompleted))
                    {
                        liveSpeech.NoteTimeout();
                        Debug.LogWarning("[Gatekeeper voice] Upcoming lines are still synthesizing after " + budget.ToString("0.0") + " s; continuing without waiting.", this);
                    }
                }
                if (Prompt.AwaitGate)
                {
                    gateReady = false; SetState(Phase.GateOpening);
                    float deadline = Time.realtimeSinceStartup + 5;
                    while (!gateReady && version == revision)
                    {
                        if (Time.realtimeSinceStartup > deadline) { Halt("Gate animation did not complete."); yield break; }
                        yield return null;
                    }
                    if (version != revision) yield break;
                    MarkTrigger("gate opened");
                    input = new GatekeeperPlayerInput(GatekeeperInputKind.GateOpened); continue;
                }
                if (HasQueuedReply && !Prompt.Ended && Prompt.InputSlot == GatekeeperInputSlot.Reply)
                {
                    input = new GatekeeperPlayerInput(GatekeeperInputKind.Text, queuedReply);
                    queuedReply = null;
                    MarkTrigger("queued typed input");
                    SetState(Phase.Inactive);
                    continue;
                }
                queuedReply = null;
                performance = null;
                SetState(Prompt.Ended ? Phase.Ended : Prompt.InputSlot == GatekeeperInputSlot.Name ? Phase.AwaitingName
                    : Prompt.InputSlot == GatekeeperInputSlot.Business ? Phase.AwaitingBusiness : Phase.AwaitingInput);
                yield break;
            }
            try { GatekeeperLineSources.ValidateOutput(line); }
            catch (Exception error) { Halt(error.Message); yield break; }
            yield return Speak(line, version);
            if (version != revision || State == Phase.VoiceFailed) yield break;
            MarkTrigger("previous line");
            if (Prompt.LineGapSeconds > 0) yield return new WaitForSecondsRealtime(Prompt.LineGapSeconds);
            input = new GatekeeperPlayerInput(GatekeeperInputKind.Continue);
        }
    }
    private IEnumerator Speak(GatekeeperScenario.Line line, int version)
    {
        SetState(Phase.Inactive);
        CurrentText = line.text; CurrentLineId = line.id; CurrentLine = line;
        CurrentWasLive = true;
        revealed = 1; revealAlpha = 1; revealClauses = null; // Its first phrase until the voice starts.
        debugLine = new DebugLine { line = line, patienceBefore = Patience, patienceAfter = Patience };
        DebugLines.Add(debugLine);
        LastGenerated = null;
        CurrentFallback = VoiceFallback.None;
        float started = Time.realtimeSinceStartup;
        Task<GatekeeperLiveSpeech.Generated> current = null;
        string failure = null;
        long voiceAt = 0, clipAt = 0, playAt = 0;
        reactionFirstAt = 0; reactionId = "";
        if (CurrentWasLive)
        {
            try { current = liveSpeech.Prepare(line.text, true); }
            catch (Exception error) { failure = "synthesis failed: " + Clause(error.Message); }
        }
        if (current != null || IsPreparedReaction)
        {
            SetState(Phase.PreparingSpeech);
        }
        if (current != null)
        {
            float budget = liveSpeech.WaitBudget;
            while (!current.IsCompleted && version == revision && Time.realtimeSinceStartup - started <= budget)
            {
                ObserveReaction();
                yield return null;
            }
            ObserveReaction();
            if (version != revision) yield break;
            voiceAt = GatekeeperLineTiming.Now;
            if (!current.IsCompleted) { liveSpeech.NoteTimeout(); failure = "synthesis timed out after " + budget.ToString("0.0") + " s"; }
            else if (current.IsFaulted || current.IsCanceled) failure = "synthesis failed: " + Clause(current.Exception?.GetBaseException().Message ?? "cancelled");
            else
            {
                try { LastGenerated = liveSpeech.Materialize(current); }
                catch (Exception error) { failure = "synthesis failed: " + Clause(error.Message); }
                clipAt = GatekeeperLineTiming.Now;
            }
        }
        if (version != revision) yield break;
        IsPreparedReaction = false;
        Encounter.ChangePatience(line.patience_delta); debugLine.patienceAfter = Patience; debugLine.voice = LastGenerated; conversation.Record("Troll", line.text);
        if (failure == null)
        {
            var voice = scenario.Voice(Mood);
            float volume = line.delivery == "mutter" ? .52f : line.delivery == "quiet threat" ? .64f : line.delivery == "roar" ? 1 : voice.volume;
            character.SetAffect(voice.pitch, volume, voice.anger, voice.amusement);
            playAt = GatekeeperLineTiming.Now;
            try
            {
                if (CurrentWasLive) character.PlayLine(LastGenerated.clip, LastGenerated.track, LastGenerated.stream != null);
                else character.PlayLine(line.audio, line.mouthCues);
                if (CurrentWasLive && LastGenerated.cuesMaterialized && LastGenerated.track == null && LastGenerated.stream == null)
                    LipSyncFailed(line);
            }
            catch (Exception error) { failure = "playback failed: " + Clause(error.Message); }
        }
        if (failure != null)
        {
            var silent = current != null && current.Status == TaskStatus.RanToCompletion ? current.Result : null;
            RecordTiming(line, silent, voiceAt, clipAt, GatekeeperLineTiming.Now, VoiceFallback.SubtitleOnly);
            yield return HoldSubtitle(line, version, started, failure); yield break;
        }
        CurrentLineSeconds = LastGenerated?.stream != null ? LastGenerated.stream.DurationSeconds : character.CurrentClip.length;
        PlaybackPreparationSeconds = Time.realtimeSinceStartup - started;
        LineStarted?.Invoke(line);
        SetState(Phase.Speaking);
        var firstClause = LastGenerated;
        int clauseCount = firstClause?.clauses?.Length ?? 1;
        if (clauseCount > 1) revealClauses = ClauseReveal(line.text, clauseCount);
        for (int clauseIndex = 0; clauseIndex < clauseCount && version == revision; clauseIndex++)
        {
            if (clauseIndex > 0)
            {
                var next = firstClause.clauses[clauseIndex];
                SetState(Phase.PreparingSpeech);
                float clauseDeadline = Time.realtimeSinceStartup + liveSpeech.WaitBudget;
                while (!next.IsCompleted && version == revision && Time.realtimeSinceStartup < clauseDeadline) yield return null;
                if (version != revision) yield break;
                if (!next.IsCompleted || next.IsCanceled || next.IsFaulted) { ReportVoiceFailure(line, "clause synthesis failed"); break; }
                LastGenerated = liveSpeech.Materialize(next);
                playAt = GatekeeperLineTiming.Now;
                character.PlayLine(LastGenerated.clip, LastGenerated.track);
                CurrentLineSeconds += LastGenerated.clip.length;
                SetState(Phase.Speaking);
            }
        bool heard = false;
        GatekeeperLineTiming timing = null;
        // The AudioSource owns completion. A stale logical pause must not strand a
        // queued turn after a complete clause has stopped playing.
        while (version == revision)
        {
            if (HasQueuedReply && character.IsPlaybackPaused) character.ResumePlayback();
            if (!character.speech.isPlaying && !character.IsPlaybackPaused && !AudioListener.pause) break;
            Reveal(clauseIndex);
            if (LastGenerated?.stream != null)
            {
                CurrentLineSeconds = LastGenerated.stream.DurationSeconds;
                AudioSettings.GetDSPBufferSize(out int dspFrames, out int dspBuffers);
                double drain = (dspFrames * (double)dspBuffers / AudioSettings.outputSampleRate) + .05;
                if (LastGenerated.stream.Finished(drain))
                {
                    if (LastGenerated.stream.Failure != null) ReportVoiceFailure(line, "stream failed: " + LastGenerated.stream.Failure.Message);
                    else if (LastGenerated.stream.Outcome?.completion == SpeechCompletion.Limited) ReportVoiceFailure(line, "Kitten returned limited speech (partial audio)");
                    character.StopSpeech(); break;
                }
            }
            var output = character.speech;
            if (!heard && output != null && output.clip != null && output.timeSamples > 0)
            {
                // The mixer has already played timeSamples of the clip; date the first sample back by that much.
                heard = true;
                double played = output.timeSamples / (double)output.clip.frequency / Mathf.Max(.01f, Mathf.Abs(output.pitch));
                long firstAt = Math.Max(playAt, GatekeeperLineTiming.Now - (long)(played * System.Diagnostics.Stopwatch.Frequency));
                timing = RecordTiming(line, LastGenerated, voiceAt, clipAt, firstAt, CurrentFallback);
            }
            if (CurrentWasLive && LastGenerated != null && !LastGenerated.cuesMaterialized && LastGenerated.analysis != null && LastGenerated.analysis.IsCompleted)
            {
                if (liveSpeech.MaterializeCues(LastGenerated)) character.AttachCues(LastGenerated.clip, LastGenerated.track);
                else if (LastGenerated.analysis.Status != TaskStatus.Canceled) LipSyncFailed(line);
                if (timing != null) UpdateAnalysisTiming(timing, LastGenerated);
            }
            yield return null;
        }
        // Analysis finishing after a short clip updates debug measurements only; it must never
        // attach an old track to the next line. Playback never waits for this coroutine.
        if (timing != null && CurrentWasLive && !LastGenerated.cuesMaterialized)
            StartCoroutine(FinishAnalysisTiming(timing, LastGenerated));
        if (version == revision) character.StopSpeech();
        }
        if (version == revision) RevealWhole();
    }
    // Raises the reveal to what the clip playing now has reached: its phrase onsets (one clip per line) or its clause's
    // ClauseStarts. In clip samples, so a paused clip holds it and pitch does not matter. A clip without either (a streamed
    // line, or no onsets) shows the line whole.
    private void Reveal(int clause)
    {
        var output = character.speech; var voice = LastGenerated;
        if (revealed == int.MaxValue || output == null || voice == null || output.clip == null || output.clip != voice.clip) return;
        int rate = output.clip.frequency, played = output.timeSamples, fade = Mathf.RoundToInt(RevealFadeSeconds * rate);
        int count; float alpha;
        if (revealClauses != null) { count = revealClauses[Mathf.Min(clause, revealClauses.Length - 1)]; alpha = Mathf.Clamp01(played / (float)fade); }
        else if (voice.phraseOnsets != null && voice.stream == null)
            count = GatekeeperSubtitleTiming.Reached(voice.phraseOnsets, played, Mathf.RoundToInt(RevealLeadSeconds * rate), fade, out alpha);
        else { RevealWhole(); return; }
        if (count > revealed) { revealed = count; revealAlpha = alpha; }
        else if (count == revealed && alpha > revealAlpha) revealAlpha = alpha;
    }
    // The worker split this line with SplitClauses; null (the whole line) if that no longer gives one entry per clip.
    private static int[] ClauseReveal(string text, int clips)
    {
        try
        {
            var starts = GatekeeperSubtitleTiming.ClauseStarts(text, GatekeeperLiveSpeech.SplitClauses(text), GatekeeperSubtitleTiming.Phrases(text));
            return starts.Length == clips ? starts : null;
        }
        catch (ArgumentException) { return null; }
    }
    private void RevealWhole() { revealed = int.MaxValue; revealAlpha = 1; revealClauses = null; }
    private void ObserveReaction()
    {
        var output = character.speech;
        if (reactionFirstAt != 0 || output == null || !output.isPlaying || output.timeSamples <= 0) return;
        double played = output.timeSamples / (double)output.clip.frequency / Mathf.Max(.01f, Mathf.Abs(output.pitch));
        reactionFirstAt = Math.Max(reactionPlayAt, GatekeeperLineTiming.Now - (long)(played * System.Diagnostics.Stopwatch.Frequency))
            + (long)(GatekeeperLineTiming.FirstSoundOffset(output.clip) / Mathf.Max(.01f, Mathf.Abs(output.pitch)) * System.Diagnostics.Stopwatch.Frequency);
    }
    private void LipSyncFailed(GatekeeperScenario.Line line)
    {
        CurrentFallback = VoiceFallback.LoudnessJaw;
        ReportVoiceFailure(line, "lip sync failed: " + Clause(LastGenerated.lipSyncError) + ". Playing the audio with a loudness-driven jaw");
    }
    private IEnumerator FinishAnalysisTiming(GatekeeperLineTiming timing, GatekeeperLiveSpeech.Generated data)
    {
        while (!data.analysis.IsCompleted) yield return null;
        if (data.analysis.Status != TaskStatus.RanToCompletion) yield break;
        if (data.clip != null) liveSpeech.MaterializeCues(data);
        UpdateAnalysisTiming(timing, data);
    }
    private void UpdateAnalysisTiming(GatekeeperLineTiming timing, GatekeeperLiveSpeech.Generated data)
    {
        var analysis = data.analysis.Status == TaskStatus.RanToCompletion ? data.analysis.Result : null;
        if (analysis == null) return;
        timing.rawRhubarbMs = GatekeeperLineTiming.Ms(analysis.startedAt, analysis.endedAt);
        timing.cuesReadyMs = GatekeeperLineTiming.Ms(timing.triggerTimestamp, analysis.endedAt);
        timing.analysisComplete = true;
        timing.analysisError = analysis.error ?? "";
        timing.fallback = data.track == null ? "LoudnessJaw" : "None";
        TimingRecorded?.Invoke(timing);
    }
    // The line performs without audio: subtitle on screen, mood and body from the performer, mouth closed.
    private IEnumerator HoldSubtitle(GatekeeperScenario.Line line, int version, float started, string failure)
    {
        character.StopSpeech();
        LastGenerated = null; CurrentFallback = VoiceFallback.SubtitleOnly; RevealWhole();
        float hold = ReadableSeconds(line.text);
        ReportVoiceFailure(line, failure + ". Showing the subtitle for " + hold.ToString("0.0") + " s without audio");
        CurrentLineSeconds = hold; PlaybackPreparationSeconds = Time.realtimeSinceStartup - started;
        holdSkipped = false; IsSilentLine = true;
        LineStarted?.Invoke(line);
        SetState(Phase.Speaking);
        float until = Time.realtimeSinceStartup + hold;
        while (version == revision && !holdSkipped && Time.realtimeSinceStartup < until) yield return null;
        if (version == revision) IsSilentLine = false;
    }
    private void MarkTrigger(string kind) { triggerAt = GatekeeperLineTiming.Now; trigger = kind; }
    // The stages tile [trigger, first audio]: pause, line choice, then the voice wait (queue, synthesis,
    // Rhubarb, hand-off; only the part after the line was chosen), clip creation and playback start.
    private GatekeeperLineTiming RecordTiming(GatekeeperScenario.Line line, GatekeeperLiveSpeech.Generated voice, long voiceAt, long clipAt, long firstAt, VoiceFallback fallback)
    {
        float onsetMs = fallback == VoiceFallback.SubtitleOnly ? 0
            : (voice?.stream != null ? voice.stream.FirstAudibleSeconds : GatekeeperLineTiming.FirstSoundOffset(character.CurrentClip))
            * 1000 / Mathf.Max(.01f, Mathf.Abs(character.speech.pitch));
        var timing = new GatekeeperLineTiming
        {
            id = line.id, text = line.text, trigger = trigger, fallback = fallback.ToString(),
            // Compared with the clip's own text: with Kitten, voice is one clause of the line.
            speech = voice != null && voice.speechText != null && voice.speechText != voice.text ? voice.speechText : line.text,
            words = GatekeeperLineTiming.CountWords(line.text), characters = line.text.Length,
            audioSeconds = fallback == VoiceFallback.SubtitleOnly || character.CurrentClip == null ? 0 : voice?.stream != null ? voice.stream.DurationSeconds : character.CurrentClip.length,
            pacingMs = GatekeeperLineTiming.Ms(triggerAt, lineRequestedAt), lineSourceMs = GatekeeperLineTiming.Ms(lineRequestedAt, lineReadyAt),
            totalMs = GatekeeperLineTiming.Ms(triggerAt, firstAt), reaction = reactionId,
            firstSoundMs = GatekeeperLineTiming.Ms(triggerAt, reactionFirstAt != 0 ? reactionFirstAt : firstAt)
                + (reactionFirstAt != 0 ? 0 : onsetMs),
            firstWordMs = fallback == VoiceFallback.SubtitleOnly ? -1 : GatekeeperLineTiming.Ms(triggerAt, firstAt)
                + onsetMs,
            triggerTimestamp = triggerAt
        };
        if (!CurrentWasLive) timing.voice = "prepared clip";
        else if (voice == null) { timing.voice = "live, no voice"; timing.queueMs = GatekeeperLineTiming.Ms(lineReadyAt, voiceAt); }
        else
        {
            timing.voice = voice.requestedAt < lineRequestedAt ? "live, prefetched" : "live, on demand";
            long from = lineReadyAt;
            timing.queueMs = GatekeeperLineTiming.Overlap(Math.Min(voice.requestedAt, from), voice.synthesisStartedAt, from, firstAt);
            timing.synthesisMs = GatekeeperLineTiming.Overlap(voice.synthesisStartedAt, voice.synthesisEndedAt, from, firstAt);
            timing.rhubarbMs = 0; // Rhubarb runs in parallel; no audio waits for it.
            timing.handoffMs = GatekeeperLineTiming.Overlap(voice.synthesisEndedAt, voiceAt, from, firstAt);
            timing.rawSynthesisMs = GatekeeperLineTiming.Ms(voice.synthesisStartedAt, voice.synthesisEndedAt);
            timing.rawRhubarbMs = GatekeeperLineTiming.Ms(voice.analysisStartedAt, voice.analysisEndedAt);
        }
        if (voiceAt != 0 && clipAt != 0) timing.clipMs = GatekeeperLineTiming.Ms(voiceAt, clipAt);
        timing.playbackStartMs = GatekeeperLineTiming.Ms(clipAt != 0 ? clipAt : voiceAt != 0 ? voiceAt : lineReadyAt, firstAt);
        LastTiming = timing;
        if (voice != null && voice.cuesMaterialized && voice.analysis != null && voice.analysis.Status == TaskStatus.RanToCompletion)
            UpdateAnalysisTiming(timing, voice);
        else TimingRecorded?.Invoke(timing);
        return timing;
    }
    private void ReportVoiceFailure(GatekeeperScenario.Line line, string message)
    {
        if (debugLine != null) debugLine.voiceError = message;
        VoiceFailureCount++;
        LastVoiceFailure = "Line '" + line.id + "': " + message + ".";
        Debug.LogWarning("[Gatekeeper voice] " + LastVoiceFailure, this);
    }
    private static string Clause(string message) => string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Trim().TrimEnd('.');
    private void Cancel()
    {
        queuedReply = null;
        revision++; IsSilentLine = false; IsPreparedReaction = false;
        requestCancellation.Cancel(); requestCancellation.Dispose(); requestCancellation = new CancellationTokenSource();
        if (liveSpeech != null) liveSpeech.InterruptPending();
        if (performance != null) { StopCoroutine(performance); performance = null; }
        if (character != null) character.StopSpeech();
    }
    private void SetState(Phase phase)
    {
        State = phase; phaseStartedAt = Time.realtimeSinceStartup; if (phase == Phase.Speaking) SpeechStartedFrame = Time.frameCount;
        if (IsWaiting) NotifyActivity(); Changed?.Invoke();
    }
    // Stops at a Try again state. Only for errors that leave no line to show; voice failures never get here.
    private void Halt(string error)
    {
        requestCancellation.Cancel();
        if (character != null) character.StopSpeech();
        if (liveSpeech != null) liveSpeech.ResetSession();
        prefetch.Clear(); CurrentText = "Something went wrong at the gate. Try again."; RevealWhole(); ClearIdleHolds();
        SetState(Phase.VoiceFailed); Debug.LogError("[Gatekeeper] " + error, this);
    }
    private void OnDisable()
    {
        Cancel(); ClearIdleHolds(); if (Encounter != null) Encounter.PatienceChanged -= ForwardPatience; State = Phase.Inactive;
    }
}
