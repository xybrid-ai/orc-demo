using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public enum PhononEngine { XnPtts, Zzz }

// Selectable CPU Phonon engines; each uses a distinct native library.
// Worker-owned; the distinct library name cannot bind to the Kitten zzz plugin.
public sealed class PhononNativeSpeechProvider : IGatekeeperSpeechProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public uint sampleRate, channels, chunks, limitedChunks;
        public ulong samples;
        public double firstPacketSeconds, totalSeconds;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool AudioCallback(IntPtr samples, UIntPtr count, IntPtr user);
    private static readonly AudioCallback Callback = Receive;
    private const string Library = "orc_phonon";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint orc_phonon_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr orc_phonon_open(IntPtr directory, IntPtr voice, uint threads, [Out] byte[] error, UIntPtr capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int orc_phonon_synthesize(IntPtr handle, IntPtr text, ulong call, AudioCallback callback, IntPtr user, out Result result, [Out] byte[] error, UIntPtr capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void orc_phonon_cancel(IntPtr handle, ulong call);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void orc_phonon_close(IntPtr handle);
    private static class ZzzNative
    {
        [DllImport("orc_phonon_zzz", CallingConvention = CallingConvention.Cdecl, EntryPoint = "orc_phonon_zzz_abi_version")] internal static extern uint orc_phonon_abi_version();
        [DllImport("orc_phonon_zzz", CallingConvention = CallingConvention.Cdecl, EntryPoint = "orc_phonon_zzz_open")] internal static extern IntPtr orc_phonon_open(IntPtr directory, IntPtr voice, uint threads, [Out] byte[] error, UIntPtr capacity);
        [DllImport("orc_phonon_zzz", CallingConvention = CallingConvention.Cdecl, EntryPoint = "orc_phonon_zzz_synthesize")] internal static extern int orc_phonon_synthesize(IntPtr handle, IntPtr text, ulong call, AudioCallback callback, IntPtr user, out Result result, [Out] byte[] error, UIntPtr capacity);
        [DllImport("orc_phonon_zzz", CallingConvention = CallingConvention.Cdecl, EntryPoint = "orc_phonon_zzz_cancel")] internal static extern void orc_phonon_cancel(IntPtr handle, ulong call);
        [DllImport("orc_phonon_zzz", CallingConvention = CallingConvention.Cdecl, EntryPoint = "orc_phonon_zzz_close")] internal static extern void orc_phonon_close(IntPtr handle);
    }
    private readonly bool zzz;

    private readonly object serial = new object(), handleGate = new object();
    private IntPtr handle;
    private bool active, disposing;
    private ulong callId;
    private int synthesisThread;
    public double OpenSeconds { get; }

    public PhononNativeSpeechProvider(string checkpoint, string clonedVoice, uint threads = 6, PhononEngine engine = PhononEngine.XnPtts)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("The installed Phonon plugin targets Apple Silicon macOS.");
        if (engine != PhononEngine.XnPtts && engine != PhononEngine.Zzz) throw new ArgumentOutOfRangeException(nameof(engine));
        zzz = engine == PhononEngine.Zzz;
        if (zzz)
        {
            if (!File.Exists(checkpoint)) throw new FileNotFoundException("Phonon zzz bundle missing.", checkpoint);
            if (string.IsNullOrWhiteSpace(clonedVoice)) throw new ArgumentException("Choose a bundled Phonon voice.");
        }
        else
        {
            foreach (string file in new[] { "config.json", "tokenizer.json", "model.q8.gguf" })
                if (!File.Exists(Path.Combine(checkpoint, file))) throw new FileNotFoundException("Phonon checkpoint file missing: " + file);
            if (!File.Exists(clonedVoice)) throw new FileNotFoundException("Phonon cloned voice missing.", clonedVoice);
        }
        if (threads < 1 || threads > 64) throw new ArgumentOutOfRangeException(nameof(threads));
        if (Marshal.SizeOf<Result>() != 40 || Marshal.OffsetOf<Result>(nameof(Result.samples)).ToInt32() != 16)
            throw new InvalidOperationException("Phonon native/managed result layout mismatch.");
        if ((zzz ? ZzzNative.orc_phonon_abi_version() : orc_phonon_abi_version()) != 1) throw new InvalidOperationException("Unsupported Orc Phonon bridge ABI.");
        var timer = Stopwatch.StartNew();
        using (var directory = new Utf8(checkpoint)) using (var voice = new Utf8(clonedVoice))
        {
            var error = new byte[512];
            handle = zzz ? ZzzNative.orc_phonon_open(directory.pointer, voice.pointer, threads, error, (UIntPtr)error.Length)
                : orc_phonon_open(directory.pointer, voice.pointer, threads, error, (UIntPtr)error.Length);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Phonon open: " + ErrorText(error));
        }
        OpenSeconds = timer.Elapsed.TotalSeconds;
    }

    private sealed class Delivery
    {
        public CancellationToken token;
        public Action<SpeechPacket> onAudio;
        public MemoryStream collected;
        public Exception failure;
        public ulong next;
    }

    [AOT.MonoPInvokeCallback(typeof(AudioCallback))]
    private static bool Receive(IntPtr samples, UIntPtr count, IntPtr user)
    {
        var delivery = (Delivery)GCHandle.FromIntPtr(user).Target;
        try
        {
            if (delivery.token.IsCancellationRequested) return false;
            ulong n = count.ToUInt64();
            if (samples == IntPtr.Zero || n == 0 || n > 1440000) throw new InvalidDataException("Invalid Phonon PCM packet.");
            var floats = new float[(int)n];
            Marshal.Copy(samples, floats, 0, floats.Length);
            var pcm = new byte[floats.Length * 2];
            for (int i = 0; i < floats.Length; i++)
            {
                if (float.IsNaN(floats[i]) || float.IsInfinity(floats[i])) throw new InvalidDataException("Non-finite Phonon PCM.");
                short value = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(floats[i] * 32768.0)));
                pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
            }
            ulong offset = delivery.next; delivery.next += n;
            delivery.collected?.Write(pcm, 0, pcm.Length);
            delivery.onAudio?.Invoke(new SpeechPacket { pcm = pcm, sampleRate = 24000, channels = 1, firstSample = offset });
            return !delivery.token.IsCancellationRequested;
        }
        catch (Exception exception) { delivery.failure = exception; return false; }
    }

    public SpeechOutcome Synthesize(string text, CancellationToken cancellation, Action<SpeechPacket> onAudio = null)
    {
        lock (serial)
        {
            cancellation.ThrowIfCancellationRequested();
            ulong thisCall;
            lock (handleGate)
            {
                if (disposing || handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(PhononNativeSpeechProvider));
                active = true; synthesisThread = Thread.CurrentThread.ManagedThreadId; thisCall = ++callId;
            }
            var delivery = new Delivery { token = cancellation, onAudio = onAudio, collected = onAudio == null ? new MemoryStream() : null };
            var root = GCHandle.Alloc(delivery);
            try
            {
                // Native cancellation is keyed by call ID, so a late callback cannot cancel the next line.
                using (var registration = cancellation.Register(() => CancelActive(thisCall)))
                using (var input = new Utf8(text))
                {
                    var error = new byte[512];
                    Result result;
                    int status = zzz ? ZzzNative.orc_phonon_synthesize(handle, input.pointer, thisCall, Callback, GCHandle.ToIntPtr(root), out result, error, (UIntPtr)error.Length)
                        : orc_phonon_synthesize(handle, input.pointer, thisCall, Callback, GCHandle.ToIntPtr(root), out result, error, (UIntPtr)error.Length);
                    if (delivery.failure != null) throw new InvalidOperationException("Phonon audio delivery failed.", delivery.failure);
                    if (status < 0 || status > 2) throw new InvalidOperationException("Phonon synthesis: " + ErrorText(error));
                    bool cancelled = cancellation.IsCancellationRequested || status == 1;
                    if (!cancelled && (result.sampleRate != 24000 || result.channels != 1 || result.samples == 0 || result.samples != delivery.next))
                        throw new InvalidDataException("Unexpected Phonon PCM result.");
                    return new SpeechOutcome { completion = cancelled ? SpeechCompletion.Cancelled : status == 2 ? SpeechCompletion.Limited : SpeechCompletion.Completed,
                        pcm = delivery.collected?.ToArray(), sampleRate = result.sampleRate, channels = result.channels, chunks = result.chunks, limitedChunks = result.limitedChunks,
                        synthesisSeconds = result.totalSeconds, firstPacketSeconds = result.firstPacketSeconds };
                }
            }
            finally
            {
                lock (handleGate) { active = false; synthesisThread = 0; }
                root.Free(); delivery.collected?.Dispose(); GC.KeepAlive(Callback);
            }
        }
    }

    private void CancelActive(ulong expectedCall)
    {
        lock (handleGate) if (active && callId == expectedCall && handle != IntPtr.Zero) CancelNative(expectedCall);
    }

    private void CancelNative(ulong call)
    {
        if (zzz) ZzzNative.orc_phonon_cancel(handle, call); else orc_phonon_cancel(handle, call);
    }

    public void Dispose()
    {
        lock (handleGate)
        {
            if (active && synthesisThread == Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Do not close Phonon from its callback.");
            disposing = true;
            if (active) CancelNative(callId);
        }
        // Native stream drop joins upstream workers; never unload a handle while its callback is active.
        lock (serial) lock (handleGate)
        {
            if (handle != IntPtr.Zero) { if (zzz) ZzzNative.orc_phonon_close(handle); else orc_phonon_close(handle); handle = IntPtr.Zero; }
        }
    }

    private static string ErrorText(byte[] message)
    {
        int end = Array.IndexOf(message, (byte)0);
        return Encoding.UTF8.GetString(message, 0, end < 0 ? message.Length : end);
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
