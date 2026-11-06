using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Hold Left Option to talk. The words are transcribed on this Mac by Whisper (Xybrid's whisper.cpp runtime, Metal) and
// go where typing would: Trola's name or business field (sent on their own after a beat, so the player can still
// correct them), or a free-form reply. Vesper is an adviser: all player speech addresses Trola. At his choice
// checkpoints d1 (POST /choose) reads the words: a suggestion lights up and is selected, or Trola reacts
// (GatekeeperSpokenChoice). Who the words are for is fixed when the key goes down. Staged by tools/setup_asr.py;
// without the bundle the game is unchanged.
public sealed class PlayerVoiceInput : MonoBehaviour
{
    public static string ModelKey = "base-en";
    public static string BundleDirectory => Path.Combine(Application.streamingAssetsPath, "XybridModels", "whisper-" + ModelKey);
    public float autoSendSeconds = 1, preRollSeconds = .3f, postRollSeconds = .25f, maxSeconds = 9;
    [Tooltip("How long a button picked by voice stays lit before it is clicked.")]
    public float choiceMarkSeconds = .45f;
    public enum Status { NotStaged, Loading, Ready, Listening, Transcribing, Failed }
    public Status State { get; private set; } = Status.NotStaged;
    public string LastTranscript { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public double LastTranscribeMs { get; private set; }
    public float LastSpeechSeconds { get; private set; }
    // The last answer spoken at the buttons and what it became, for F4.
    public string LastChoice { get; private set; } = "";

    private GatekeeperController controller;
    private GatekeeperView view;
    private Xybrid.Model model;
    private AudioClip mic;
    private int micRate, pressPosition;
    private float pressedAt, flashUntil;
    private bool holding;
    private string flash = "", autoSendText;
    private float autoSendAt;
    private Text status;
    // Who the words are for, fixed when the key goes down; for the buttons, also which buttons (labels and story turn).
    private enum Listener { Nobody, Trola, Buttons }
    private Listener listener;
    private string listenedAt;
    // One spoken answer at the buttons at a time, with its own idle hold (Hear releases the holds owned by this).
    private readonly object choiceHold = new object();
    private bool answering;
    private Coroutine answer;
    private CancellationTokenSource answerCancel;
    private int inputVersion;
    private bool captureActive, delivering;
    private Task<string> transcription;
    // d1 /choose is skipped for 5 s after a failure, 30 s after a 404 (a server from before /choose); keywords meanwhile.
    private float choiceRetryAt;
    private string choiceProblem = "";
    private GatekeeperStory.Document story;
    private bool storyRead;
    private IReadOnlyList<GatekeeperChoice> checkpointChoices;
    private GatekeeperStory.Node checkpointNode;
    private string[] checkpointTargets;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var gatekeeper = FindAnyObjectByType<GatekeeperController>();
        if (gatekeeper != null && gatekeeper.GetComponent<PlayerVoiceInput>() == null) gatekeeper.gameObject.AddComponent<PlayerVoiceInput>();
    }

    private void Start()
    {
        controller = GetComponent<GatekeeperController>();
        view = FindAnyObjectByType<GatekeeperView>();
        if (controller != null) { controller.Restarted += CancelInput; controller.PlayerActed += OnPlayerActed; }
        if (!File.Exists(Path.Combine(BundleDirectory, "model_metadata.json"))) { LastError = "not staged: python3 tools/setup_asr.py"; return; }
        State = Status.Loading;
        StartCoroutine(Load(BundleDirectory));
    }

    private IEnumerator Load(string directory)
    {
        yield return new WaitForSecondsRealtime(2); // Trola's voice and Gemma start first.
        var task = Task.Run(() =>
        {
            Xybrid.XybridClient.Initialize();
            Xybrid.Model loaded;
            using (var loader = Xybrid.ModelLoader.FromDirectory(directory)) loaded = loader.Load();
            // A second of silence, so the first spoken answer does not pay whisper.cpp's cold start.
            using (var envelope = Xybrid.Envelope.Audio(PlayerSpeechAudio.Wav(new float[PlayerSpeechAudio.Rate])))
            using (loaded.Run(envelope)) { }
            return loaded;
        });
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted || task.IsCanceled)
        {
            State = Status.Failed; LastError = task.Exception?.GetBaseException().Message ?? "cancelled";
            Debug.LogWarning("[Voice input] Whisper " + ModelKey + " did not load: " + LastError, this);
            yield break;
        }
        model = task.Result; State = Status.Ready;
        Debug.Log("[Voice input] Ready: Whisper " + ModelKey + ". Hold Left Option to talk.", this);
    }

    private void OnDestroy()
    {
        if (controller != null) { controller.Restarted -= CancelInput; controller.PlayerActed -= OnPlayerActed; }
        CancelInput();
        if (holding && controller != null) controller.ReleaseIdle(this);
        if (mic != null) Microphone.End(null);
        var loaded = model; model = null;
        if (transcription != null && !transcription.IsCompleted)
            transcription.ContinueWith(t => { var ignored = t.Exception; loaded?.Dispose(); }, TaskScheduler.Default);
        else loaded?.Dispose();
    }

    // Hint visibility and Vesper's advice never determine which answers are accepted.
    private bool Wanted => controller != null && (controller.AcceptsText || controller.CanSubmitText || ButtonsListen);
    private bool ButtonsListen => !answering && controller.AcceptsChoice && !controller.IsSuspended && Checkpoint(out _) != null;

    private void Update()
    {
        if (controller == null) return;
        RecoverCancelledTranscription();
        if (controller.IsSuspended && (captureActive || answering || autoSendText != null)) CancelInput();
        if (captureActive) controller.HoldIdle(this);
        if (answering) controller.HoldIdle(choiceHold);
        var keyboard = Keyboard.current;
        bool key = keyboard != null && keyboard.leftAltKey.isPressed;
        if (State == Status.Ready && key && !holding && keyboard.leftAltKey.wasPressedThisFrame)
        {
            if (Wanted) BeginListening();
            else Flash(answering ? "One answer at a time" : "Nothing to answer yet");
        }
        else if (holding && (!key || Time.unscaledTime - pressedAt > maxSeconds)) StartCoroutine(EndListening());
        if (autoSendText != null) AutoSend();
        RefreshStatus();
    }

    private bool EnsureMicrophone()
    {
        if (mic != null && Microphone.IsRecording(null)) return true;
        if (Microphone.devices.Length == 0) { Flash("No microphone"); return false; }
        Microphone.GetDeviceCaps(null, out int min, out int max);
        // 16 kHz when the device offers it, so nothing needs resampling; otherwise its best rate, filtered down.
        micRate = min == 0 && max == 0 || min <= PlayerSpeechAudio.Rate && PlayerSpeechAudio.Rate <= max ? PlayerSpeechAudio.Rate : max;
        // Kept open while the session lasts: a ring of ten seconds gives each press its pre-roll.
        mic = Microphone.Start(null, true, 10, micRate);
        if (mic == null) { Flash("Microphone blocked"); return false; }
        return true;
    }

    private void BeginListening()
    {
        // Fade her before microphone setup so the first captured answer cannot be routed to her.
        SpiritDirector.Instance?.InterruptAdvice();
        if (!EnsureMicrophone()) return;
        holding = true; pressedAt = Time.unscaledTime;
        pressPosition = Microphone.GetPosition(null);
        State = Status.Listening; autoSendText = null;
        Aim();
        captureActive = true;
        view?.SetChoiceInputBusy(true);
        controller.HoldIdle(this);
    }

    // The input slot and checkpoint are captured at key down, even if the hints are already gone.
    private void Aim()
    {
        listener = controller.AcceptsText || controller.CanSubmitText ? Listener.Trola : ButtonsListen ? Listener.Buttons : Listener.Nobody;
        listenedAt = listener == Listener.Buttons ? Signature() : null;
    }
    // The buttons on screen and the story turn: a round of the hub shows the same labels again on a later turn.
    private string Signature()
    {
        var text = new System.Text.StringBuilder().Append(controller.Turns);
        foreach (var choice in controller.CurrentChoices) text.Append('\n').Append(choice?.label);
        return text.ToString();
    }

    private IEnumerator EndListening()
    {
        holding = false; State = Status.Transcribing;
        int version = inputVersion;
        yield return new WaitForSecondsRealtime(postRollSeconds);
        if (version != inputVersion) { RecoverCancelledTranscription(); yield break; }
        yield return Hear(Read(pressPosition - Mathf.RoundToInt(preRollSeconds * micRate)), mic.channels, micRate, version);
    }

    // Recorded audio through the same path as a key press, for checks without a microphone (e.g. macOS `say` output).
    public bool DebugHear(float[] interleaved, int channels, int rate)
    {
        if (State != Status.Ready || controller == null || !Wanted) return false;
        SpiritDirector.Instance?.InterruptAdvice();
        Aim();
        State = Status.Transcribing; captureActive = true; controller.HoldIdle(this);
        view?.SetChoiceInputBusy(true);
        StartCoroutine(Hear(interleaved, channels, rate, inputVersion));
        return true;
    }

    private IEnumerator Hear(float[] raw, int channels, int rate, int version)
    {
        var reader = model;
        var task = transcription = Task.Run(() => Transcribe(raw, channels, rate, reader));
        while (!task.IsCompleted) yield return null;
        if (ReferenceEquals(transcription, task)) transcription = null;
        if (version != inputVersion) { RecoverCancelledTranscription(); yield break; }
        State = model != null ? Status.Ready : Status.Failed;
        captureActive = false;
        string heard = task.IsFaulted || task.IsCanceled ? null : task.Result;
        if (task.IsFaulted) { LastError = task.Exception.GetBaseException().Message; Debug.LogWarning("[Voice input] " + LastError, this); }
        if (heard == null)
        {
            controller.ReleaseIdle(this); view?.SetChoiceInputBusy(false);
            Flash("Didn't catch that");
            yield break;
        }
        LastTranscript = heard;
        Deliver(heard);
        controller.ReleaseIdle(this);
        if (!answering) view?.SetChoiceInputBusy(false);
    }

    // Everything the looping clip recorded from `start` up to now, wrapping around its end.
    private float[] Read(int start)
    {
        int length = mic.samples, channels = mic.channels, end = Microphone.GetPosition(null);
        start = ((start % length) + length) % length;
        int count = ((end - start) % length + length) % length;
        var data = new float[count * channels];
        if (count == 0) return data;
        int first = Math.Min(count, length - start);
        var head = new float[first * channels];
        mic.GetData(head, start);
        Array.Copy(head, data, head.Length);
        if (count > first)
        {
            var tail = new float[(count - first) * channels];
            mic.GetData(tail, 0);
            Array.Copy(tail, 0, data, head.Length, tail.Length);
        }
        return data;
    }

    private string Transcribe(float[] interleaved, int channels, int rate, Xybrid.Model reader)
    {
        var pcm = PlayerSpeechAudio.Resample(PlayerSpeechAudio.ToMono(interleaved, channels), rate);
        var gate = PlayerSpeechAudio.Analyze(pcm);
        LastSpeechSeconds = gate.VoicedSeconds;
        if (!gate.HasSpeech || reader == null) return null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string text;
        using (var envelope = Xybrid.Envelope.Audio(PlayerSpeechAudio.Wav(PlayerSpeechAudio.Slice(pcm, gate.Start, gate.End))))
        using (var result = reader.Run(envelope)) { result.ThrowIfFailed(); text = result.Text; }
        LastTranscribeMs = watch.Elapsed.TotalMilliseconds;
        return PlayerSpeechAudio.Clean(text, gate.PeakDb < -30);
    }

    private void Deliver(string text)
    {
        if (listener == Listener.Buttons)
        {
            if (!answering && Signature() == listenedAt && controller.AcceptsChoice && !controller.IsSuspended)
            {
                answering = true;
                answer = StartCoroutine(AnswerButtons(text, listenedAt));
            }
            else Flash("Trola moved on");
            return;
        }
        if (listener == Listener.Trola && view != null && (controller.AcceptsText || controller.CanSubmitText))
        {
            view.SetDraft(text);
            autoSendText = view.Draft; autoSendAt = Time.unscaledTime + autoSendSeconds;
            return;
        }
        Flash("Heard \"" + text + "\", but nobody asked");
    }

    // Sent unless the player changed the words or the field closed meanwhile.
    private void AutoSend()
    {
        if (view == null || view.Draft != autoSendText || !(controller.AcceptsText || controller.CanSubmitText)) { autoSendText = null; return; }
        if (Time.unscaledTime < autoSendAt) return;
        autoSendText = null;
        view.Submit();
    }

    // Words spoken at the buttons: d1 (or keywords) reads them, then a button lights up and is clicked, or Trola reacts.
    // Only while the buttons the key went down at are still live: a click, a restart or the next turn always wins.
    private IEnumerator AnswerButtons(string text, string at)
    {
        controller.HoldIdle(choiceHold);
        try
        {
            Flash("Heard \"" + text + "\"");
            var node = Checkpoint(out var targets);
            if (node == null) { Flash("Trola moved on"); yield break; }
            var service = controller.GetComponent<GatekeeperDecisionService>(); // Added at the first restart, so not cached.
            SpokenChoice result = null;
            if (service != null && service.IsAvailable && Time.unscaledTime >= choiceRetryAt)
            {
                Task<GatekeeperDecisionClient.ChoiceReply> asked = null;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                answerCancel = new CancellationTokenSource();
                try { asked = service.ChooseAsync(controller.Conversation, node.id, targets, text, answerCancel.Token); }
                catch (Exception error) { ChoiceFailed(error); }
                if (asked != null)
                {
                    asked.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    while (!asked.IsCompleted) yield return null;
                    if (asked.Status == TaskStatus.RanToCompletion) { choiceProblem = ""; result = GatekeeperSpokenChoice.FromD1(asked.Result, node, targets, timer.Elapsed.TotalMilliseconds, text); }
                    else ChoiceFailed(asked.Exception?.GetBaseException());
                }
                answerCancel?.Dispose(); answerCancel = null;
            }
            if (result == null)
                result = GatekeeperSpokenChoice.FromKeywords(text, node, targets, service == null ? "no d1 service" : !GatekeeperDecisionService.UseD1 ? "d1 off (F4)"
                    : !service.IsAvailable ? "d1 offline" : choiceProblem);
            if (Signature() != at || !controller.AcceptsChoice || controller.IsSuspended) { LastChoice = Said(text, "Trola moved on", result); Flash("Trola moved on"); yield break; }
            if (result.Kind == SpokenChoiceKind.Choose)
            {
                string label = controller.CurrentChoices[result.Index].label;
                LastChoice = Said(text, label, result);
                Flash("\"" + text + "\" → " + label);
                if (view != null) view.MarkSpokenChoice(label);
                yield return new WaitForSecondsRealtime(choiceMarkSeconds);
                if (view != null) view.ClearSpokenChoice();
                if (Signature() == at && controller.AcceptsChoice && !controller.IsSuspended)
                {
                    delivering = true;
                    try { controller.ChooseSpoken(result.Index, text); }
                    finally { delivering = false; }
                }
                else Flash("Trola moved on");
            }
            else
            {
                LastChoice = Said(text, result.Kind.ToString(), result);
                delivering = true;
                try { controller.AnswerSpoken(result, text); }
                finally { delivering = false; }
            }
        }
        finally
        {
            answering = false; answer = null;
            answerCancel?.Dispose(); answerCancel = null; controller.ReleaseIdle(choiceHold);
            view?.ClearSpokenChoice(); view?.SetChoiceInputBusy(false);
        }
    }
    private static string Said(string text, string outcome, SpokenChoice result) => "\"" + text + "\" → " + outcome + " (" + result.Detail + ")";

    private void ChoiceFailed(Exception error)
    {
        string message = error?.Message ?? "no answer";
        bool old = message.Contains(" 404"); // A server from before /choose: "d1 server returned 404: ..."
        choiceRetryAt = Time.unscaledTime + (old ? 30 : 5);
        choiceProblem = old ? "restart serve_decisions.py for spoken choices" : "d1 failed: " + message;
        Debug.LogWarning("[Voice input] Spoken choice: " + choiceProblem + "; keywords for " + (old ? 30 : 5) + " s.", this);
    }

    // A restart or teardown drops a pending spoken answer: no late click, idle hold or lit button survives it.
    private void OnPlayerActed(GatekeeperInputKind kind, string words) { if (!delivering) CancelInput(); }
    private void OnDisable() { CancelInput(); }
    private void RecoverCancelledTranscription()
    {
        // A deactivated GameObject stops coroutines, but its native task can still be running.
        // Re-enable only after that task completes, so Whisper never receives concurrent runs.
        if (captureActive || State != Status.Transcribing || transcription != null && !transcription.IsCompleted) return;
        if (transcription != null) { var ignored = transcription.Exception; transcription = null; }
        State = model != null ? Status.Ready : Status.NotStaged;
    }
    private void CancelInput()
    {
        inputVersion++; holding = false; captureActive = false; autoSendText = null;
        if (State == Status.Listening) State = model != null ? Status.Ready : Status.NotStaged;
        RecoverCancelledTranscription();
        answerCancel?.Cancel();
        if (answer != null) { StopCoroutine(answer); answer = null; }
        answerCancel?.Dispose(); answerCancel = null;
        answering = false;
        if (controller != null) { controller.ReleaseIdle(choiceHold); controller.ReleaseIdle(this); }
        if (view != null) { view.ClearSpokenChoice(); view.SetChoiceInputBusy(false); }
    }

    // The story node behind the buttons on screen (and its targets in screen order), cached per set of buttons; null when
    // they are not the authored story's (then nothing is spoken to them).
    private GatekeeperStory.Node Checkpoint(out string[] targets)
    {
        var shown = controller.CurrentChoices;
        if (!ReferenceEquals(shown, checkpointChoices))
        {
            checkpointChoices = shown;
            if (!storyRead)
            {
                storyRead = true;
                var scenario = controller.scenario;
                try { if (scenario != null && scenario.authoredConversation && scenario.authoredSource != null) story = scenario.ReadStory(); }
                catch (Exception error) { Debug.LogWarning("[Voice input] No story for spoken choices: " + error.Message, this); }
            }
            checkpointNode = GatekeeperSpokenChoice.Checkpoint(story, shown, out checkpointTargets);
        }
        targets = checkpointTargets;
        return checkpointNode;
    }

    private void Flash(string text) { flash = text; flashUntil = Time.unscaledTime + 2.5f; }

    private void RefreshStatus()
    {
        string text = State == Status.Listening ? "Listening..." : State == Status.Transcribing ? "..."
            : Time.unscaledTime < flashUntil ? flash
            : answering ? "..."
            : State == Status.Ready && Wanted && autoSendText == null ? Hint() : "";
        if (text.Length == 0 && status == null) return;
        var label = StatusText();
        if (label != null) label.text = text;
    }

    private string Hint() => controller.AcceptsChoice ? "Hold Option to answer Trola · Tab for hints" : "Hold Option to speak";

    // A small line at the bottom left of the dialogue canvas, built the first time it has something to say.
    private Text StatusText()
    {
        if (status != null || view == null || view.sentence == null) return status;
        var line = new GameObject("Voice status", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Shadow));
        line.layer = view.sentence.gameObject.layer;
        var rect = (RectTransform)line.transform;
        rect.SetParent(view.sentence.transform.parent, false);
        rect.anchorMin = new Vector2(.02f, .02f); rect.anchorMax = new Vector2(.4f, .02f);
        rect.pivot = new Vector2(0, 0); rect.sizeDelta = new Vector2(0, 28);
        status = line.GetComponent<Text>();
        status.font = view.sentence.font; status.fontSize = 18; status.alignment = TextAnchor.LowerLeft;
        status.horizontalOverflow = HorizontalWrapMode.Wrap; status.verticalOverflow = VerticalWrapMode.Overflow;
        status.supportRichText = false; status.raycastTarget = false; status.color = new Color(.8f, .76f, .68f, .75f);
        line.GetComponent<Shadow>().effectColor = new Color(0, 0, 0, .9f);
        return status;
    }

    // F4 panel line.
    public string Describe() => "Voice input: " + State + " (Whisper " + ModelKey + ", hold Left Option)"
        + (LastTranscript.Length > 0 ? "; last \"" + LastTranscript + "\" in " + LastTranscribeMs.ToString("0") + " ms" : "")
        + (LastError.Length > 0 && State != Status.Ready ? "; " + LastError : "")
        + (LastChoice.Length > 0 ? "\nSpoken choice: " + LastChoice : "")
        + (choiceProblem.Length > 0 && Time.unscaledTime < choiceRetryAt ? "\nSpoken choice: keywords until d1 answers (" + choiceProblem + ")" : "");
}
