using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Editor: add at runtime or call Begin(controller, path). Player: -xybridSpeechTest
// -xybridReport PATH. Exercises the actual NPC controller, avatar and AudioSource.
public sealed class GatekeeperSpeechValidation : MonoBehaviour
{
    public GatekeeperController controller;
    public string reportPath;
    public bool Done { get; private set; }
    [Serializable] public sealed class LineReport
    {
        public string provider, text, completion;
        public bool sentences, streaming, warm;
        public float firstAudioMs, synthesisMs, firstPacketMs, audioSeconds, underrunMs, maxFrameMs, p95FrameMs, setupMs;
        public long processMemoryBytes;
    }
    [Serializable] public sealed class Report
    {
        public bool passed;
        public string environment, unityVersion;
        public List<string> failures = new List<string>();
        public List<LineReport> lines = new List<LineReport>();
        public float cancellationMs;
    }
    public static void Begin(GatekeeperController controller, string path)
    {
        var validation = controller.gameObject.AddComponent<GatekeeperSpeechValidation>();
        validation.controller = controller; validation.reportPath = path;
    }
    private IEnumerator Start()
    {
        if (controller == null) yield break;
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-xybridReport");
        if (string.IsNullOrEmpty(reportPath)) reportPath = at >= 0 && at + 1 < args.Length ? args[at + 1]
            : Path.Combine(Application.persistentDataPath, "kitten-speech-report.json");
        var report = new Report { environment = Application.isEditor ? "editor" : "player", unityVersion = Application.unityVersion };
        yield return new WaitForSecondsRealtime(1);
        string[] texts = { "Your name sounds like trouble.", "The village gate stays closed until you tell me your true business.",
            "Tell me your name. Then explain your business. The village gate stays closed until I know who you are and what you want here today." };
        foreach (int mode in new[] { 0, 1, 2, 3 })
        {
            var provider = mode == 0 ? GatekeeperLiveSpeech.Provider.Kokoro : GatekeeperLiveSpeech.Provider.Kitten;
            bool sentences = mode == 2, streaming = mode != 3;
            controller.Suspend(); controller.liveSpeech.SelectProvider(provider, sentences, streaming); controller.Resume();
            // Kitten can take longer than Kokoro; record actual wait instead of forcing subtitle fallback.
            controller.liveSpeech.preparationTimeout = 120;
            controller.liveSpeech.stalledPreparationTimeout = 120;
            foreach (string text in texts)
                for (int run = 0; run < 4; run++)
                {
                    controller.UseLineSource(new ValidationLine(text), new GatekeeperConversationState(new GatekeeperEncounter(3, 5, true)));
                    var frames = new List<float>(); float start = Time.realtimeSinceStartup;
                    while (controller.State != GatekeeperController.Phase.AwaitingInput && Time.realtimeSinceStartup - start < 180)
                    { frames.Add(Time.unscaledDeltaTime * 1000); yield return null; }
                    var data = controller.LastGenerated;
                    if (data == null || controller.IsSilentLine || controller.LastTiming == null)
                    { report.failures.Add(provider + " failed to play: " + controller.LastVoiceFailure); break; }
                    float duration = data.stream == null ? data.clip.length : data.stream.DurationSeconds;
                    if (data.outcome?.completion != SpeechCompletion.Completed || data.stream?.Failure != null)
                        report.failures.Add(provider + " partial/failed speech: " + text);
                    if (text == texts[2] && duration <= 5.1f) report.failures.Add(provider + " long line did not exceed 5.1 seconds.");
                    frames.Sort();
                    report.lines.Add(new LineReport { provider = provider.ToString(), text = text, sentences = sentences, streaming = streaming,
                        warm = run > 0, completion = data.outcome?.completion.ToString(), firstAudioMs = controller.LastTiming.totalMs,
                        synthesisMs = (float)(data.outcome?.synthesisSeconds * 1000 ?? data.synthesisSeconds * 1000),
                        firstPacketMs = (float)(data.outcome?.firstPacketSeconds * 1000 ?? data.synthesisSeconds * 1000),
                        audioSeconds = duration, underrunMs = (float)(data.stream?.GapSeconds * 1000 ?? 0), setupMs = (float)(controller.liveSpeech.ProviderOpenSeconds * 1000),
                        maxFrameMs = frames.Count == 0 ? 0 : frames[frames.Count - 1],
                        p95FrameMs = frames.Count == 0 ? 0 : frames[(int)((frames.Count - 1) * .95)],
                        processMemoryBytes = Process.GetCurrentProcess().WorkingSet64 });
                    Write(report); yield return new WaitForSecondsRealtime(.3f);
                }
        }
        controller.Suspend(); controller.liveSpeech.SelectProvider(GatekeeperLiveSpeech.Provider.Kitten, true, true); controller.Resume();
        float waiting = Time.realtimeSinceStartup;
        while (!controller.liveSpeech.WarmCompleted && Time.realtimeSinceStartup - waiting < 120) yield return null;
        // Warm-up has worker lifetime, intentionally survives encounter interruption.
        controller.UseLineSource(new ValidationLine(texts[2]), new GatekeeperConversationState(new GatekeeperEncounter(3, 5, true)));
        waiting = Time.realtimeSinceStartup;
        while (!controller.liveSpeech.NativeCallRunning && Time.realtimeSinceStartup - waiting < 120) yield return null;
        bool entered = controller.liveSpeech.NativeCallRunning;
        long cancelAt = Stopwatch.GetTimestamp(); controller.Suspend();
        while (controller.liveSpeech.NativeCallRunning && GatekeeperLineTiming.Ms(cancelAt, Stopwatch.GetTimestamp()) < 10000) yield return null;
        report.cancellationMs = GatekeeperLineTiming.Ms(cancelAt, Stopwatch.GetTimestamp());
        if (!entered || controller.liveSpeech.NativeCallRunning) report.failures.Add("Native cancellation did not finish within 10 seconds.");
        controller.Resume();
        controller.UseLineSource(new ValidationLine(texts[0]), new GatekeeperConversationState(new GatekeeperEncounter(3, 5, true)));
        waiting = Time.realtimeSinceStartup;
        while (controller.State != GatekeeperController.Phase.AwaitingInput && Time.realtimeSinceStartup - waiting < 180) yield return null;
        if (controller.LastGenerated?.outcome?.completion != SpeechCompletion.Completed) report.failures.Add("Session reuse after cancellation failed.");
        controller.Suspend(); controller.liveSpeech.enabled = false;
        waiting = Time.realtimeSinceStartup;
        while (!controller.liveSpeech.Shutdown.IsCompleted && Time.realtimeSinceStartup - waiting < 15) yield return null;
        if (!controller.liveSpeech.Shutdown.IsCompleted) report.failures.Add("Worker did not shut down.");
        report.passed = report.failures.Count == 0; Write(report); Done = true;
        UnityEngine.Debug.Log("[Kitten speech validation] " + (report.passed ? "PASS " : "FAIL ") + reportPath);
        if (!Application.isEditor) Application.Quit(report.passed ? 0 : 1);
    }
    private void Write(Report report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
    }
    private sealed class ValidationLine : IGatekeeperLineSource
    {
        private readonly string text;
        private bool emitted;
        public ValidationLine(string text) { this.text = text; }
        public GatekeeperPresentation Presentation => new GatekeeperPresentation { InputSlot = GatekeeperInputSlot.Reply, IdleSeconds = 0, LineGapSeconds = 0 };
        public IEnumerable<GatekeeperScenario.Line> AnticipatedLines => Array.Empty<GatekeeperScenario.Line>();
        public Task<GatekeeperScenario.Line> NextLineAsync(GatekeeperConversationState state, GatekeeperPlayerInput input, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (emitted) return Task.FromResult<GatekeeperScenario.Line>(null);
            emitted = true;
            return Task.FromResult(new GatekeeperScenario.Line { id = "speech-validation", text = text, emotion = "neutral", intensity = .3f });
        }
    }
}
