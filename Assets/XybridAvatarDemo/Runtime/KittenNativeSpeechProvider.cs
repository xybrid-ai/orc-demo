using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// rc.4 / ABI 1. No Unity calls; construct, synthesize and dispose on a worker.
public sealed class KittenNativeSpeechProvider : IGatekeeperSpeechProvider
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Options
    {
        public uint structSize, threads, maxTokens, flags;
        public ulong seed;
        public IntPtr languageModel, decoderModel, languageVoice, decoderVoice;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        public uint sampleRate, channels, chunks, limitedChunks;
        public ulong samples;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Error
    {
        public int code;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 192)] public byte[] message;
        public override string ToString()
        {
            int length = message == null ? 0 : Array.IndexOf(message, (byte)0);
            return message == null ? "" : Encoding.UTF8.GetString(message, 0, length < 0 ? message.Length : length);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AudioCallback(IntPtr samples, UIntPtr count, uint rate, uint channels, ulong first, IntPtr user);
    private static readonly AudioCallback Callback = Receive;
    private const string Library = "zzz_embed";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint zzz_embed_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int zzz_embed_supports_model(IntPtr model);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int zzz_embed_open_kitten(ref Options options, out IntPtr session, ref Error error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int zzz_embed_synthesize(IntPtr session, IntPtr text, IntPtr language, AudioCallback callback, IntPtr user, out Result result, ref Error error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void zzz_embed_cancel(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void zzz_embed_close(IntPtr session);

    private readonly object serial = new object(), handleGate = new object();
    private IntPtr handle;
    private bool active, disposing;
    private long callId;
    private int synthesisThread;
    public double OpenSeconds { get; }
    public KittenNativeSpeechProvider(string lm, string decoder, string lmVoice, string decoderVoice,
        uint threads = 6, bool sentenceChunks = false, bool accelerate = true, uint maxTokens = 0)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Kitten rc.4 requires a supported 64-bit target.");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("Kitten rc.4 has no Windows archive.");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("Kitten rc.4 requires a native ARM64 Mac editor/player.");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            // Check before loading the v3 library: even a capability probe belongs
            // to the archive compiled for AVX2/FMA. Linux exposes OS-enabled flags.
            string cpu = File.ReadAllText("/proc/cpuinfo");
            int at = cpu.IndexOf("flags", StringComparison.Ordinal);
            int end = at < 0 ? -1 : cpu.IndexOf('\n', at);
            string flags = at < 0 ? "" : " " + cpu.Substring(at, (end < 0 ? cpu.Length : end) - at) + " ";
            foreach (var required in new[] { "avx2", "fma", "bmi1", "bmi2", "f16c", "movbe", "popcnt", "sse4_2", "xsave" })
                if (!flags.Contains(" " + required + " ")) throw new PlatformNotSupportedException("Kitten rc.4 requires Linux x86-64-v3.");
        }
        foreach (var path in new[] { lm, decoder, lmVoice, decoderVoice })
            if (!File.Exists(path)) throw new FileNotFoundException("Kitten asset missing: " + path, path);
        AssertLayout();
        var timer = Stopwatch.StartNew();
        using (var modelName = new Utf8("kitten-tts2"))
        using (var a = new Utf8(lm)) using (var b = new Utf8(decoder))
        using (var c = new Utf8(lmVoice)) using (var d = new Utf8(decoderVoice))
        {
            if (zzz_embed_abi_version() != 1 || zzz_embed_supports_model(modelName.pointer) != 1)
                throw new InvalidOperationException("Native library is not KittenTTS 2 ABI 1.");
            var options = new Options { structSize = 56, threads = threads, maxTokens = maxTokens, seed = 0,
                flags = (sentenceChunks ? 2u : 0u) | (accelerate && RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 1u : 0u),
                languageModel = a.pointer, decoderModel = b.pointer, languageVoice = c.pointer, decoderVoice = d.pointer };
            var error = NewError();
            int status = zzz_embed_open_kitten(ref options, out handle, ref error);
            if (status != 0) throw new InvalidOperationException("Kitten open (" + status + "): " + error);
        }
        OpenSeconds = timer.Elapsed.TotalSeconds;
    }
    public static void AssertLayout()
    {
        if (Marshal.SizeOf<Options>() != 56 || Marshal.OffsetOf<Options>(nameof(Options.languageModel)).ToInt32() != 24
            || Marshal.SizeOf<Result>() != 24 || Marshal.SizeOf<Error>() != 196)
            throw new InvalidOperationException("Kitten C/managed ABI layout mismatch.");
    }
    private static Error NewError() => new Error { message = new byte[192] };
    private sealed class Delivery
    {
        public CancellationToken token;
        public Action<SpeechPacket> onAudio;
        public MemoryStream collected;
        public Exception failure;
        public ulong next;
        public long started, first;
    }
    [AOT.MonoPInvokeCallback(typeof(AudioCallback))]
    private static int Receive(IntPtr samples, UIntPtr count, uint rate, uint channels, ulong first, IntPtr user)
    {
        var delivery = (Delivery)GCHandle.FromIntPtr(user).Target;
        try
        {
            if (delivery.token.IsCancellationRequested) return 1;
            ulong n = count.ToUInt64();
            if (rate != 24000 || channels != 1 || first != delivery.next || n == 0 || n > 1920)
                throw new InvalidDataException("Unexpected Kitten PCM format, offset or callback size.");
            if (delivery.first == 0) delivery.first = Stopwatch.GetTimestamp();
            var floats = new float[(int)n];
            Marshal.Copy(samples, floats, 0, floats.Length);
            var pcm = new byte[floats.Length * 2];
            for (int i = 0; i < floats.Length; i++)
            {
                if (float.IsNaN(floats[i]) || float.IsInfinity(floats[i])) throw new InvalidDataException("Non-finite Kitten PCM.");
                short value = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(floats[i] * 32768.0)));
                pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
            }
            delivery.next += n;
            delivery.collected?.Write(pcm, 0, pcm.Length);
            delivery.onAudio?.Invoke(new SpeechPacket { pcm = pcm, sampleRate = rate, channels = channels, firstSample = first });
            return delivery.token.IsCancellationRequested ? 1 : 0;
        }
        catch (Exception exception) { delivery.failure = exception; return 1; }
    }
    public SpeechOutcome Synthesize(string text, CancellationToken cancellation, Action<SpeechPacket> onAudio = null)
    {
        lock (serial)
        {
            cancellation.ThrowIfCancellationRequested();
            long thisCall;
            lock (handleGate)
            {
                if (disposing || handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(KittenNativeSpeechProvider));
                active = true; synthesisThread = Thread.CurrentThread.ManagedThreadId; thisCall = ++callId;
            }
            var delivery = new Delivery { token = cancellation, onAudio = onAudio,
                collected = onAudio == null ? new MemoryStream() : null, started = Stopwatch.GetTimestamp() };
            var root = GCHandle.Alloc(delivery);
            try
            {
                // Cancellation at native call entry can race its running flag. Retry while
                // cancelled, until this call returns; never carry a cancel to the next call.
                using (var registration = cancellation.Register(() => CancelActive(thisCall)))
                using (var retry = new Timer(_ => { if (cancellation.IsCancellationRequested) CancelActive(thisCall); }, null, 0, 5))
                using (var input = new Utf8(text))
                {
                    var error = NewError();
                    int status = zzz_embed_synthesize(handle, input.pointer, IntPtr.Zero, Callback, GCHandle.ToIntPtr(root), out var result, ref error);
                    if (delivery.failure != null) throw new InvalidOperationException("Kitten audio delivery failed.", delivery.failure);
                    if (status != 0 && status != -5 && status != -6)
                        throw new InvalidOperationException("Kitten synthesis (" + status + "): " + error);
                    return new SpeechOutcome { completion = cancellation.IsCancellationRequested || status == -5 ? SpeechCompletion.Cancelled
                            : status == -6 ? SpeechCompletion.Limited : SpeechCompletion.Completed,
                        pcm = delivery.collected?.ToArray(), sampleRate = result.sampleRate, channels = result.channels,
                        chunks = result.chunks, limitedChunks = result.limitedChunks,
                        synthesisSeconds = (Stopwatch.GetTimestamp() - delivery.started) / (double)Stopwatch.Frequency,
                        firstPacketSeconds = delivery.first == 0 ? 0 : (delivery.first - delivery.started) / (double)Stopwatch.Frequency };
                }
            }
            finally
            {
                lock (handleGate) { active = false; synthesisThread = 0; }
                root.Free(); delivery.collected?.Dispose(); GC.KeepAlive(Callback);
            }
        }
    }
    private void CancelActive(long expectedCall)
    {
        lock (handleGate) if (active && callId == expectedCall && handle != IntPtr.Zero) zzz_embed_cancel(handle);
    }
    public void Dispose()
    {
        lock (handleGate)
        {
            if (active && synthesisThread == Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Do not close Kitten from its callback.");
            disposing = true;
            if (active) zzz_embed_cancel(handle);
        }
        lock (serial) lock (handleGate)
        {
            if (handle != IntPtr.Zero) { zzz_embed_close(handle); handle = IntPtr.Zero; }
        }
    }
    private sealed class Utf8 : IDisposable
    {
        public readonly IntPtr pointer;
        public Utf8(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Expected NUL-free UTF-8 text.");
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
            pointer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        public void Dispose() => Marshal.FreeHGlobal(pointer);
    }
}
