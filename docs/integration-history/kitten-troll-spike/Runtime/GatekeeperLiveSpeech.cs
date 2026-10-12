using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Reuses the project's Xybrid API, PCM converter, Rhubarb cues, and avatar clock.
// One worker owns native synthesis; Rhubarb runs independently. Unity objects stay on main.
// Synthesis and lip sync fail separately: a line whose Rhubarb run fails still returns
// its audio (cues null, lipSyncError set), so the mouth can fall back to loudness.
// Rhubarb 1.14.0 crashes (SIGSEGV in its PocketSphinx log callback) when its folder path is longer
// than about 130 characters, which a built app's StreamingAssets path easily is. On macOS it therefore
// runs from a short per-user folder holding a copy of the shipped binary and a link to its res/ folder.
public sealed class GatekeeperLiveSpeech : MonoBehaviour
{
    public enum Provider { Kokoro, Kitten, KittenSdk }
    public Provider provider = Provider.Kokoro;
    public bool startWithKitten = true;
    public bool sentenceChunks = true;
    public bool streamKitten = true;
    public bool accelerate = true;
    public int kittenThreads = 6;
    public string kittenDirectory = "KittenTTS2";
    // Both voice filenames derive from one prefix; never mix different voice pairs.
    public string kittenVoice = "warchief";
    public string ResidentKittenVoice { get; private set; }
    public string LastProviderError { get; private set; }
    private Task retirement = Task.CompletedTask;
    public Task Shutdown => retirement;
    public volatile bool NativeCallRunning;
    public double ProviderOpenSeconds { get; private set; }
    public double ProviderLoadSeconds { get; private set; }
    public bool NativeOpenMeasured => provider == Provider.Kitten;
    // SDK model loading is lazy; the first call includes native open and synthesis together.
    public double ProviderFirstCallSeconds => warmup != null && warmup.Status == TaskStatus.RanToCompletion
        ? warmup.Result.synthesisSeconds : 0;
    // Testing only. Each fault goes through the same failure path as the real problem.
    public enum Fault { None, SynthesisFails, SynthesisHangs, LipSyncMissing, LipSyncFails }
    // Relative paths resolve against StreamingAssets, which Unity copies into every player build.
    public string modelBundle = "XybridModels/universal.xyb";
    public string voiceId = "am_fenrir";
    public string rhubarbExecutable = "Rhubarb/Rhubarb-Lip-Sync-1.14.0-macOS/rhubarb";
    [Tooltip("Seconds a line waits for its voice before it plays as a subtitle only.")]
    public float preparationTimeout = 15;
    [Tooltip("Wait used instead after a line has timed out, until a later line is prepared within the budget.")]
    public float stalledPreparationTimeout = 1.5f;
    [Tooltip("Seconds Rhubarb may take on one line before the line falls back to a loudness-driven jaw.")]
    public float lipSyncTimeout = 10;
    [Tooltip("Testing only: force one kind of voice failure. A built player reads -xybridVoiceFault <name>.")]
    public Fault simulatedFault;
    public float simulatedHangSeconds = 60;

    public sealed class Generated
    {
        public string text, cues, lipSyncError;
        public byte[] pcm;
        public double synthesisSeconds, analysisSeconds;
        public Task<Analysis> analysis;
        public bool cuesMaterialized;
        // GatekeeperLineTiming.Now stamps of each stage, for the timing overlay.
        public long requestedAt, synthesisStartedAt, synthesisEndedAt, analysisStartedAt, analysisEndedAt;
        public AudioClip clip;
        public GatekeeperPcmBuffer stream;
        public volatile SpeechOutcome outcome;
        public TextAsset track;
    }
    public sealed class Analysis
    {
        public string cues, error;
        public long startedAt, endedAt;
    }
    private sealed class Request
    {
        public string text, rhubarb;
        public Fault fault;
        public bool warmup;
        public float hangSeconds, lipSyncTimeout, budget;
        public long requestedAt;
        public CancellationToken session;
        public TaskCompletionSource<Generated> completion = new TaskCompletionSource<Generated>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Worker
    {
        public readonly object gate = new object();
        public readonly LinkedList<Request> queue = new LinkedList<Request>();
        public readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        public readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        public readonly SemaphoreSlim analysisSlots = new SemaphoreSlim(2);
        public readonly List<Task> analyses = new List<Task>();
        public Exception failure;
        public Task task;
    }
    private Worker worker;
    private CancellationTokenSource session = new CancellationTokenSource();
    private readonly Dictionary<string, Task<Generated>> requests = new Dictionary<string, Task<Generated>>(StringComparer.Ordinal);
    private readonly List<Generated> created = new List<Generated>();
    // Set when a line gives up waiting; cleared by the next line prepared within the budget.
    private volatile bool stalled;
    private Task<Generated> warmup;
    public bool WarmCompleted => warmup != null && warmup.Status == TaskStatus.RanToCompletion
        && warmup.Result.analysis != null && warmup.Result.analysis.Status == TaskStatus.RanToCompletion;
    public string WarmError => !WarmCompleted ? "Warm-up incomplete" : warmup.Result.analysis.Result.error ?? "";
    public int QueuedLines => requests.Count;
    public int ReadyLines
    {
        get { int n = 0; foreach (var task in requests.Values) if (task.Status == TaskStatus.RanToCompletion) n++; return n; }
    }
    public string RhubarbPath => Resolve(rhubarbExecutable);
    public string ModelPath => Resolve(modelBundle);
    public bool Stalled => stalled;
    public float WaitBudget => provider != Provider.Kokoro ? Math.Max(120, preparationTimeout)
        : stalled ? Mathf.Min(stalledPreparationTimeout, preparationTimeout) : preparationTimeout;
    public void NoteTimeout() { stalled = true; }
    private static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(Application.streamingAssetsPath, path);

    private void Awake()
    {
        if (startWithKitten) { provider = Provider.Kitten; streamKitten = true; }
        var args = Environment.GetCommandLineArgs();
        int speechAt = Array.IndexOf(args, "-xybridSpeech");
        if (speechAt >= 0 && speechAt + 1 < args.Length && Enum.TryParse(args[speechAt + 1], true, out Provider selected)) provider = selected;
        if (Array.IndexOf(args, "-xybridKittenSentenceChunks") >= 0) sentenceChunks = true;
        if (Array.IndexOf(args, "-xybridKittenBatch") >= 0) streamKitten = false;
        int at = Array.IndexOf(args, "-xybridVoiceFault");
        if (at >= 0 && at + 1 < args.Length && Enum.TryParse(args[at + 1], true, out Fault fault)) simulatedFault = fault;
        if (simulatedFault != Fault.None) UnityEngine.Debug.LogWarning("[Gatekeeper voice] Simulating voice fault: " + simulatedFault, this);
    }

    private void Start() { Warm(); }

    // A real throwaway waveform and phonetic analysis, never materialized or played. Its lifetime
    // is the voice worker's, so restarting the encounter during the intro cannot cancel the warm-up.
    public void Warm()
    {
        EnsureWorker();
        if (warmup != null) return;
        var request = new Request
        {
            text = "Hrm. Listen.", warmup = true, session = worker.lifetime.Token, rhubarb = RhubarbPath,
            fault = simulatedFault, hangSeconds = simulatedHangSeconds, lipSyncTimeout = lipSyncTimeout,
            budget = preparationTimeout, requestedAt = GatekeeperLineTiming.Now
        };
        warmup = request.completion.Task;
        Observe(warmup);
        lock (worker.gate)
        {
            if (worker.failure != null) request.completion.TrySetException(worker.failure);
            else { worker.queue.AddFirst(request); worker.signal.Release(); }
        }
    }
    private static void Observe(Task task) => task.ContinueWith(t => { var ignored = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    public void ResetSession()
    {
        session.Cancel(); session.Dispose(); session = new CancellationTokenSource();
        foreach (var task in requests.Values)
            if (task.Status == TaskStatus.RanToCompletion) task.Result.stream?.Cancel();
        requests.Clear();
        foreach (var data in created)
        {
            if (data.clip != null) Destroy(data.clip);
            if (data.track != null) Destroy(data.track);
        }
        created.Clear();
    }

    public Task<Generated> Prepare(string resolvedText, bool urgent = false)
    {
        if (string.IsNullOrWhiteSpace(resolvedText) || resolvedText.Contains("{") || resolvedText.Contains("}"))
            throw new ArgumentException("Resolve dialogue before synthesis.");
        string key = provider + "\n" + voiceId + "\n" + resolvedText;
        if (requests.TryGetValue(key, out var existing))
        {
            if (urgent && worker != null)
            {
                lock (worker.gate)
                {
                    var node = worker.queue.First;
                    while (node != null)
                    {
                        if (node.Value.completion.Task == existing)
                        {
                            worker.queue.Remove(node); worker.queue.AddFirst(node); break;
                        }
                        node = node.Next;
                    }
                }
            }
            return existing;
        }
        EnsureWorker();
        var request = new Request
        {
            text = resolvedText, session = session.Token, rhubarb = RhubarbPath,
            fault = simulatedFault, hangSeconds = simulatedHangSeconds, lipSyncTimeout = lipSyncTimeout, budget = preparationTimeout,
            requestedAt = GatekeeperLineTiming.Now
        };
        // Observe failures even when a restart abandons a prefetch.
        request.completion.Task.ContinueWith(t => { var ignored = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        lock (worker.gate)
        {
            if (worker.failure != null) request.completion.TrySetException(worker.failure);
            else { if (urgent) worker.queue.AddFirst(request); else worker.queue.AddLast(request); worker.signal.Release(); }
        }
        requests.Add(key, request.completion.Task);
        return request.completion.Task;
    }

    // Main thread. Throws only when there is no playable audio; unusable cues leave track null.
    public Generated Materialize(Task<Generated> task)
    {
        var data = task.GetAwaiter().GetResult();
        if (data.clip != null) return data;
        data.clip = data.stream == null ? Tavern.Dialogue.TTSAudioPlayer.PCMToAudioClip(data.pcm, "live_gatekeeper")
            : AudioClip.Create("kitten_gatekeeper_stream", GatekeeperPcmBuffer.Capacity, 1, GatekeeperPcmBuffer.Rate, true, data.stream.Read);
        if (data.clip == null) throw new InvalidOperationException("Synthesized PCM could not be converted to audio.");
        created.Add(data);
        MaterializeCues(data);
        // Retain timings and resolved text for checks, release redundant PCM.
        data.pcm = null;
        return data;
    }

    // Main thread, polled during playback. Task completion publishes the immutable analysis result;
    // pending analysis is normal, and a failed analysis leaves the loudness jaw running.
    public bool MaterializeCues(Generated data)
    {
        if (data.cuesMaterialized || data.analysis == null || !data.analysis.IsCompleted) return data.track != null;
        data.cuesMaterialized = true;
        if (data.analysis.Status != TaskStatus.RanToCompletion) return false;
        var result = data.analysis.Result;
        data.cues = result.cues; data.lipSyncError = result.error;
        data.analysisStartedAt = result.startedAt; data.analysisEndedAt = result.endedAt;
        data.analysisSeconds = GatekeeperLineTiming.Ms(result.startedAt, result.endedAt) / 1000.0;
        if (data.cues != null)
        {
            data.track = new TextAsset(data.cues) { name = "live_gatekeeper_cues" };
            if (!XybridAvatarPlayer.ValidateCues(data.track, data.clip, out var error))
            {
                Destroy(data.track); data.track = null;
                data.lipSyncError = "Rhubarb returned unusable cues (" + error + ")";
            }
        }
        return data.track != null;
    }

    private void EnsureWorker()
    {
        if (worker != null) return;
        string bundle = ModelPath;
        string temporaryRoot = Path.Combine(Application.temporaryCachePath, "TrolaSpeech");
        string voice = voiceId;
        Provider selected = provider;
        bool useStream = selected != Provider.Kokoro && streamKitten, sentences = sentenceChunks, useAccelerate = accelerate;
        uint threads = (uint)Mathf.Clamp(kittenThreads, 1, 64);
        string kitten = Resolve(kittenDirectory);
        string voicePair = kittenVoice;
        if (string.IsNullOrWhiteSpace(voicePair) || Path.GetFileName(voicePair) != voicePair)
            throw new ArgumentException("Choose a Kitten voice filename prefix, such as warchief.");
        Task previous = retirement;
        ProviderOpenSeconds = ProviderLoadSeconds = 0;
        ResidentKittenVoice = null;
        var owner = new Worker(); worker = owner;
        owner.task = Task.Run(async () =>
        {
            IGatekeeperSpeechProvider model = null;
            try
            {
                try { await previous.ConfigureAwait(false); }
                catch (OperationCanceledException) { } // Retired analysis may have been interrupted.
                owner.lifetime.Token.ThrowIfCancellationRequested();
                var openTimer = Stopwatch.StartNew();
                model = selected == Provider.Kokoro ? (IGatekeeperSpeechProvider)new KokoroSpeechProvider(bundle, voice)
                    : selected == Provider.KittenSdk ? new KittenSdkSpeechProvider(kitten)
                    : new KittenNativeSpeechProvider(Path.Combine(kitten, "kitten-tts-2-q2_0.gguf"),
                        Path.Combine(kitten, "kitten-s3-meanflow-f32.gguf"), Path.Combine(kitten, voicePair + ".lm.json"),
                        Path.Combine(kitten, voicePair + ".s3.json"), threads, sentences, useAccelerate);
                ProviderLoadSeconds = openTimer.Elapsed.TotalSeconds;
                ProviderOpenSeconds = model is KittenNativeSpeechProvider native ? native.OpenSeconds : 0;
                if (selected == Provider.Kitten) ResidentKittenVoice = voicePair;
                while (true)
                {
                    await owner.signal.WaitAsync(owner.lifetime.Token).ConfigureAwait(false);
                    Request request;
                    lock (owner.gate) { request = owner.queue.First.Value; owner.queue.RemoveFirst(); }
                    var cancel = CancellationTokenSource.CreateLinkedTokenSource(owner.lifetime.Token, request.session);
                    bool dispatched = false;
                    try
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        var timer = Stopwatch.StartNew();
                        long synthesisStarted = GatekeeperLineTiming.Now;
                        if (request.fault == Fault.SynthesisHangs) await Task.Delay(TimeSpan.FromSeconds(request.hangSeconds), cancel.Token).ConfigureAwait(false);
                        if (request.fault == Fault.SynthesisFails) throw new InvalidOperationException("Simulated synthesis failure.");
                        NativeCallRunning = true;
                        if (useStream && !request.warmup)
                        {
                            var stream = new GatekeeperPcmBuffer();
                            var streamData = new Generated { text = request.text, stream = stream, cuesMaterialized = true,
                                requestedAt = request.requestedAt, synthesisStartedAt = synthesisStarted };
                            try
                            {
                                using (cancel.Token.Register(stream.Cancel))
                                {
                                    streamData.outcome = model.Synthesize(request.text, cancel.Token, packet =>
                                    {
                                        cancel.Token.ThrowIfCancellationRequested();
                                        stream.Append(packet);
                                        if (streamData.synthesisEndedAt == 0)
                                        {
                                            // These tile the wait to FIRST packet; outcome retains full synthesis time.
                                            streamData.synthesisEndedAt = streamData.analysisStartedAt = streamData.analysisEndedAt = GatekeeperLineTiming.Now;
                                            streamData.synthesisSeconds = timer.Elapsed.TotalSeconds;
                                            request.completion.TrySetResult(streamData);
                                        }
                                    });
                                    if (streamData.outcome.completion == SpeechCompletion.Cancelled) throw new OperationCanceledException(cancel.Token);
                                    if (streamData.synthesisEndedAt == 0) throw new InvalidOperationException("Kitten produced no audio.");
                                    stream.Complete(streamData.outcome);
                                    stalled = false;
                                }
                            }
                            catch (Exception error) { stream.Complete(streamData.outcome, error); throw; }
                            continue;
                        }
                        var spoken = model.Synthesize(request.text, cancel.Token);
                        if (spoken.completion == SpeechCompletion.Cancelled) throw new OperationCanceledException(cancel.Token);
                        byte[] pcm = spoken.pcm;
                        double synth = timer.Elapsed.TotalSeconds;
                        long synthesisEnded = GatekeeperLineTiming.Now;
                        cancel.Token.ThrowIfCancellationRequested();
                        if (pcm == null || pcm.Length < 2 || pcm.Length % 2 != 0) throw new InvalidOperationException("TTS returned no valid PCM.");
                        var data = new Generated
                        {
                            text = request.text, pcm = pcm, synthesisSeconds = synth, requestedAt = request.requestedAt, outcome = spoken,
                            synthesisStartedAt = synthesisStarted, synthesisEndedAt = synthesisEnded
                        };
                        // Slot waiting belongs to analysis, never to synthesis or audio readiness.
                        data.analysis = Task.Run(async () =>
                        {
                            bool acquired = false;
                            var result = new Analysis();
                            try
                            {
                                await owner.analysisSlots.WaitAsync(cancel.Token).ConfigureAwait(false); acquired = true;
                                result.startedAt = GatekeeperLineTiming.Now;
                                result.cues = Analyze(pcm, request, temporaryRoot, cancel.Token);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception exception) { result.error = exception.Message; }
                            finally
                            {
                                result.endedAt = GatekeeperLineTiming.Now;
                                cancel.Dispose(); if (acquired) owner.analysisSlots.Release();
                            }
                            return result;
                        });
                        Observe(data.analysis); owner.analyses.Add(data.analysis);
                        dispatched = true;
                        if (synth <= request.budget) stalled = false;
                        request.completion.TrySetResult(data);
                        owner.analyses.RemoveAll(task => task.IsCompleted);
                    }
                    catch (OperationCanceledException) { request.completion.TrySetCanceled(); }
                    catch (Exception exception) { request.completion.TrySetException(exception); }
                    finally { NativeCallRunning = false; if (!dispatched) cancel.Dispose(); }
                }
            }
            catch (Exception exception)
            {
                if (!(exception is OperationCanceledException)) LastProviderError = exception.Message;
                lock (owner.gate)
                {
                    owner.failure = exception;
                    while (owner.queue.Count > 0)
                    {
                        var request = owner.queue.First.Value; owner.queue.RemoveFirst();
                        if (exception is OperationCanceledException) request.completion.TrySetCanceled();
                        else request.completion.TrySetException(exception);
                    }
                }
            }
            finally
            {
                // Each provider closes only after its synthesis call has returned.
                model?.Dispose();
                try { await Task.WhenAll(owner.analyses).ConfigureAwait(false); }
                catch (OperationCanceledException) { } // All children have finished; cancellation is expected at shutdown.
            }
        });
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string Analyze(byte[] pcm, Request request, string root, CancellationToken token)
    {
        // The simulated faults use the real lookup and the real binary: a path that does not exist, a rejected argument.
        string executable = request.fault == Fault.LipSyncMissing ? request.rhubarb + ".missing" : request.rhubarb;
        if (!File.Exists(executable)) throw new FileNotFoundException("Rhubarb is not installed at " + executable, executable);
        executable = Launcher(executable);
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string wav = Path.Combine(directory, "speech.wav"), cues = Path.Combine(directory, "mouth.json"), transcript = Path.Combine(directory, "script.txt");
        try
        {
            using (var writer = new BinaryWriter(File.Create(wav)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
            }
            File.WriteAllText(transcript, request.text, new UTF8Encoding(false));
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = (request.fault == Fault.LipSyncFails ? "--simulated-failure " : "")
                    + "-r phonetic -f json --quiet --threads 2 --dialogFile " + Quote(transcript) + " -o " + Quote(cues) + " " + Quote(wav),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = start })
            {
                process.Start();
                var stderr = process.StandardError.ReadToEndAsync();
                var timeout = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100))
                    {
                        token.ThrowIfCancellationRequested();
                        if (timeout.Elapsed.TotalSeconds > request.lipSyncTimeout) throw new TimeoutException("Rhubarb timed out after " + request.lipSyncTimeout + " s.");
                    }
                    if (process.ExitCode != 0) throw new InvalidOperationException("Rhubarb exited with code " + process.ExitCode + ": " + stderr.GetAwaiter().GetResult().Trim());
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                }
            }
            return File.ReadAllText(cues);
        }
        finally { Directory.Delete(directory, true); }
    }

    // The path to start Rhubarb from: on macOS a copy in $TMPDIR/xybrid-rhubarb-<hash of the shipped folder>/
    // (refreshed when the shipped binary changes) next to a link to the shipped res/; elsewhere the shipped binary.
    private static readonly object launcherGate = new object();
    private static string Launcher(string shipped)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return shipped;
        string source = Path.GetDirectoryName(shipped);
        uint hash = 2166136261;
        foreach (char c in source) hash = (hash ^ c) * 16777619;
        string folder = Path.Combine(Path.GetTempPath(), "xybrid-rhubarb-" + hash.ToString("x8"));
        string binary = Path.Combine(folder, Path.GetFileName(shipped)), resources = Path.Combine(folder, "res");
        lock (launcherGate)
        {
            var original = new FileInfo(shipped); var copy = new FileInfo(binary);
            if (copy.Exists && copy.Length == original.Length && copy.LastWriteTimeUtc == original.LastWriteTimeUtc && Directory.Exists(resources)) return binary;
            Directory.CreateDirectory(folder);
            File.Copy(shipped, binary, true);
            File.SetLastWriteTimeUtc(binary, original.LastWriteTimeUtc);
            Run("/bin/chmod", "755 " + Quote(binary));
            Run("/bin/ln", "-sfn " + Quote(Path.Combine(source, "res")) + " " + Quote(resources));
            return binary;
        }
    }
    private static void Run(string tool, string arguments)
    {
        using (var process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new InvalidOperationException("Could not prepare Rhubarb: " + tool + " " + arguments);
        }
    }

    public void SelectProvider(Provider next, bool sentences, bool streaming)
    {
        if (provider == next && sentenceChunks == sentences && streamKitten == streaming) return;
        OnDisable(); ResetSession();
        provider = next; sentenceChunks = sentences; streamKitten = streaming; LastProviderError = null;
    }
    public void InterruptPending()
    {
        session.Cancel(); session.Dispose(); session = new CancellationTokenSource();
        foreach (var task in requests.Values)
            if (task.Status == TaskStatus.RanToCompletion) task.Result.stream?.Cancel();
        requests.Clear();
    }

    private void OnDisable()
    {
        session.Cancel();
        foreach (var task in requests.Values)
            if (task.Status == TaskStatus.RanToCompletion) task.Result.stream?.Cancel();
        if (worker != null) { worker.lifetime.Cancel(); retirement = worker.task; worker = null; }
        warmup = null;
        foreach (var data in created)
        {
            if (data.clip != null) Destroy(data.clip);
            if (data.track != null) Destroy(data.track);
        }
        created.Clear(); requests.Clear(); warmup = null;
    }

    private void OnApplicationQuit()
    {
        // Callbacks contain managed code. Drain before the player tears down its runtime.
        OnDisable();
        try { retirement.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { UnityEngine.Debug.LogWarning("[Gatekeeper voice] Shutdown: " + error.Message); }
    }
}
