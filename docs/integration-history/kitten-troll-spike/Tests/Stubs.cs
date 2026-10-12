using System;
// Only the unrelated Kokoro adapter and IL2CPP attribute need compile stubs.
// Kitten tests invoke the real rc.4 shared library, not these stubs.
namespace AOT { public sealed class MonoPInvokeCallbackAttribute : Attribute { public MonoPInvokeCallbackAttribute(Type type) { } } }
namespace Xybrid
{
    public static class XybridClient { public static void Initialize() => throw new NotSupportedException("Kokoro is tested in Unity."); }
    public sealed class Model : IDisposable
    {
        public byte[] RunTts(string text, string voice, double speed) => throw new NotSupportedException();
        public void Dispose() { }
    }
    public sealed class ModelLoader : IDisposable
    {
        public static ModelLoader FromBundle(string bundle) => throw new NotSupportedException();
        public Model Load() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
