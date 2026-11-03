using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Hold Left Option to talk. The words are transcribed on this Mac by Whisper (Xybrid's whisper.cpp runtime, Metal) and
// go where typing would: Trola's name or business field (sent on their own after a beat, so the player can still
// correct them), a free-form reply, or a thought to Vesper while her field is open. Trola asking for the name or the
// business wins over Vesper: at those prompts she is telling the player to answer him. Staged by tools/setup_asr.py;
// without the bundle the game is unchanged.
public sealed class PlayerVoiceInput : MonoBehaviour
{
    public static string ModelKey = "base-en";
    public static string BundleDirectory => Path.Combine(Application.streamingAssetsPath, "XybridModels", "whisper-" + ModelKey);
    public float autoSendSeconds = 1, preRollSeconds = .3f, postRollSeconds = .25f, maxSeconds = 9;
    public enum Status { NotStaged, Loading, Ready, Listening, Transcribing, Failed }
    public Status State { get; private set; } = Status.NotStaged;
    public string LastTranscript { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public double LastTranscribeMs { get; private set; }
    public float LastSpeechSeconds { get; private set; }

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
        if (holding && controller != null) controller.ReleaseIdle(this);
        if (mic != null) Microphone.End(null);
        model?.Dispose(); model = null;
    }

    private bool TrolaAsks => controller.AcceptsText && (controller.Prompt.InputSlot == GatekeeperInputSlot.Name || controller.Prompt.InputSlot == GatekeeperInputSlot.Business);
    private static SpiritPresence Vesper => SpiritDirector.Instance != null ? SpiritDirector.Instance.Presence : null;
    // Someone is waiting for words: Trola's field, a free-form reply he will queue, or Vesper's field.
    private bool Wanted => controller != null && (controller.AcceptsText || controller.CanSubmitText || Vesper != null && Vesper.FieldOpen);

    private void Update()
    {
        if (controller == null) return;
        var keyboard = Keyboard.current;
        bool key = keyboard != null && keyboard.leftAltKey.isPressed;
        if (State == Status.Ready && key && !holding && keyboard.leftAltKey.wasPressedThisFrame)
        {
            if (Wanted) BeginListening();
            else Flash("Nothing to answer yet");
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
        if (!EnsureMicrophone()) return;
        holding = true; pressedAt = Time.unscaledTime;
        pressPosition = Microphone.GetPosition(null);
        State = Status.Listening; autoSendText = null;
        controller.NotifyActivity(); controller.HoldIdle(this);
        if (!TrolaAsks && Vesper != null && Vesper.FieldOpen) Vesper.ShowSpoken("...");
    }

    private IEnumerator EndListening()
    {
        holding = false; State = Status.Transcribing;
        yield return new WaitForSecondsRealtime(postRollSeconds);
        yield return Hear(Read(pressPosition - Mathf.RoundToInt(preRollSeconds * micRate)), mic.channels, micRate);
    }

    // Recorded audio through the same path as a key press, for checks without a microphone (e.g. macOS `say` output).
    public bool DebugHear(float[] interleaved, int channels, int rate)
    {
        if (State != Status.Ready || controller == null) return false;
        State = Status.Transcribing; controller.NotifyActivity(); controller.HoldIdle(this);
        StartCoroutine(Hear(interleaved, channels, rate));
        return true;
    }

    private IEnumerator Hear(float[] raw, int channels, int rate)
    {
        var task = Task.Run(() => Transcribe(raw, channels, rate));
        while (!task.IsCompleted) yield return null;
        controller.ReleaseIdle(this);
        State = model != null ? Status.Ready : Status.Failed;
        string heard = task.IsFaulted || task.IsCanceled ? null : task.Result;
        if (task.IsFaulted) { LastError = task.Exception.GetBaseException().Message; Debug.LogWarning("[Voice input] " + LastError, this); }
        if (heard == null)
        {
            if (Vesper != null && Vesper.FieldOpen && !TrolaAsks) Vesper.ShowSpoken("");
            Flash("Didn't catch that");
            yield break;
        }
        LastTranscript = heard;
        Deliver(heard);
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

    private string Transcribe(float[] interleaved, int channels, int rate)
    {
        var pcm = PlayerSpeechAudio.Resample(PlayerSpeechAudio.ToMono(interleaved, channels), rate);
        var gate = PlayerSpeechAudio.Analyze(pcm);
        LastSpeechSeconds = gate.VoicedSeconds;
        if (!gate.HasSpeech || model == null) return null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string text;
        using (var envelope = Xybrid.Envelope.Audio(PlayerSpeechAudio.Wav(PlayerSpeechAudio.Slice(pcm, gate.Start, gate.End))))
        using (var result = model.Run(envelope)) { result.ThrowIfFailed(); text = result.Text; }
        LastTranscribeMs = watch.Elapsed.TotalMilliseconds;
        return PlayerSpeechAudio.Clean(text, gate.PeakDb < -30);
    }

    private void Deliver(string text)
    {
        var vesper = Vesper;
        if (!TrolaAsks && vesper != null && vesper.FieldOpen) { vesper.SubmitSpoken(text); return; }
        if (view != null && (controller.AcceptsText || controller.CanSubmitText))
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

    private void Flash(string text) { flash = text; flashUntil = Time.unscaledTime + 2.5f; }

    private void RefreshStatus()
    {
        string text = State == Status.Listening ? "Listening..." : State == Status.Transcribing ? "..."
            : Time.unscaledTime < flashUntil ? flash
            : State == Status.Ready && Wanted && autoSendText == null ? "Hold Option to speak" : "";
        if (text.Length == 0 && status == null) return;
        var label = StatusText();
        if (label != null) label.text = text;
    }

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
        + (LastError.Length > 0 && State != Status.Ready ? "; " + LastError : "");
}
