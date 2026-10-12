using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

// Offline authoring only: all eight nonlexical openings are prepared before play/build.
public static class GatekeeperReactionBaker
{
    private const string Folder = "Assets/XybridAvatarDemo/Resources/GatekeeperReactions";
    private static GameObject owner;
    private static GatekeeperLiveSpeech voice;
    private static Task<GatekeeperLiveSpeech.Generated> pending;
    private static string report;
    private static int index;

    public static void Begin(string reportPath)
    {
        if (EditorApplication.isPlaying || owner != null) throw new InvalidOperationException("Bake once in Edit Mode.");
        var controller = UnityEngine.Object.FindFirstObjectByType<GatekeeperController>();
        if (controller == null) throw new InvalidOperationException("Open the gatekeeper scene.");
        owner = new GameObject("Prepared gatekeeper reactions") { hideFlags = HideFlags.HideAndDontSave };
        voice = owner.AddComponent<GatekeeperLiveSpeech>();
        voice.modelBundle = controller.liveSpeech.modelBundle; voice.voiceId = controller.liveSpeech.voiceId;
        voice.rhubarbExecutable = controller.liveSpeech.rhubarbExecutable;
        report = reportPath; index = 0; pending = null;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(report, "{\"complete\":false,\"processed\":0}");
        EditorApplication.update += Tick;
    }
    private static void Tick()
    {
        try
        {
            if (index == GatekeeperReactions.Ids.Length)
            {
                AssetDatabase.Refresh();
                File.WriteAllText(report, "{\"complete\":true,\"passed\":true,\"processed\":8}");
                Stop(); return;
            }
            if (pending == null) { pending = voice.Prepare(GatekeeperReactions.Scripts[index]); return; }
            if (!pending.IsCompleted) return;
            var data = pending.GetAwaiter().GetResult();
            if (!data.analysis.IsCompleted) return;
            var analysis = data.analysis.GetAwaiter().GetResult();
            if (analysis.cues == null) throw new InvalidOperationException(analysis.error);
            byte[] pcm = Trim(data.pcm, out float removed);
            float seconds = pcm.Length / 48000f;
            var parsed = JsonUtility.FromJson<XybridAvatarPlayer.CueFile>(analysis.cues);
            var cues = new List<XybridAvatarPlayer.Cue>();
            foreach (var cue in parsed.mouthCues)
            {
                float start = Mathf.Clamp(cue.start - removed, 0, seconds), end = Mathf.Clamp(cue.end - removed, 0, seconds);
                if (end > start) cues.Add(new XybridAvatarPlayer.Cue { start = start, end = end, value = cue.value });
            }
            string prefix = Folder + "/" + GatekeeperReactions.Ids[index];
            using (var w = new BinaryWriter(File.Create(prefix + ".wav")))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(24000); w.Write(48000); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
            }
            File.WriteAllText(prefix + ".json", JsonUtility.ToJson(new XybridAvatarPlayer.CueFile { mouthCues = cues.ToArray() }));
            AssetDatabase.ImportAsset(prefix + ".wav", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(prefix + ".json", ImportAssetOptions.ForceSynchronousImport);
            var audio = AssetDatabase.LoadAssetAtPath<AudioClip>(prefix + ".wav");
            var track = AssetDatabase.LoadAssetAtPath<TextAsset>(prefix + ".json");
            if (audio == null || track == null || !XybridAvatarPlayer.ValidateCues(track, audio, out var error))
                throw new InvalidOperationException("Invalid prepared reaction: " + GatekeeperReactions.Ids[index]);
            Debug.Log("[Gatekeeper reaction] " + GatekeeperReactions.Ids[index] + ": " + seconds.ToString("0.000") + " s, " + cues.Count + " cues");
            index++; pending = null;
            File.WriteAllText(report, "{\"complete\":false,\"processed\":" + index + "}");
        }
        catch (Exception e)
        {
            File.WriteAllText(report, JsonUtility.ToJson(new Failure { complete = true, error = e.Message }));
            Debug.LogException(e); Stop();
        }
    }
    // Trim only leading/trailing silence, preserving the full reaction and shifting its cue clock.
    private static byte[] Trim(byte[] pcm, out float removed)
    {
        int first = -1, last = 0;
        const int hop = 480; // 10 ms of 24 kHz, signed 16-bit mono PCM.
        for (int i = 0; i < pcm.Length; i += hop)
        {
            double sum = 0; int count = 0;
            for (int j = i; j + 1 < Math.Min(pcm.Length, i + hop); j += 2)
            { float sample = (short)(pcm[j] | pcm[j + 1] << 8) / 32768f; sum += sample * sample; count++; }
            if (count > 0 && Math.Sqrt(sum / count) > .005) { if (first < 0) first = i; last = i; }
        }
        if (first < 0) throw new InvalidOperationException("The prepared reaction is silent.");
        int from = Math.Max(0, first - hop), to = Math.Min(pcm.Length, last + hop * 5);
        var result = new byte[to - from]; Buffer.BlockCopy(pcm, from, result, 0, result.Length); removed = from / 48000f;
        return result;
    }
    private static void Stop()
    {
        EditorApplication.update -= Tick;
        if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
        owner = null; voice = null; pending = null;
    }
    [Serializable] private sealed class Failure { public bool complete; public string error; }
}
