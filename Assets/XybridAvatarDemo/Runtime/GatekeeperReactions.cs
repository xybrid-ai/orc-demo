using System;
using UnityEngine;

// Eight authored, baked one-syllable reactions. Loaded before the intro, never synthesized live.
public sealed class GatekeeperReactions
{
    public static readonly string[] Ids = { "hrm", "hmph", "ugh", "huh", "hmm", "grr", "heh", "mmhm" };
    public static readonly string[] Scripts = { "Hrm.", "Hmph.", "Ugh.", "Huh?", "Hmm?", "Grr.", "Heh.", "Mm-hm." };
    private readonly AudioClip[] clips = new AudioClip[Ids.Length];
    private readonly TextAsset[] tracks = new TextAsset[Ids.Length];
    private int last = -1, turn;
    public int Count { get; private set; }
    public GatekeeperReactions()
    {
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i] = Resources.Load<AudioClip>("GatekeeperReactions/" + Ids[i]);
            tracks[i] = Resources.Load<TextAsset>("GatekeeperReactions/" + Ids[i]);
            if (clips[i] != null) { clips[i].LoadAudioData(); Count++; }
        }
    }
    public AudioClip Next(string emotion, GatekeeperMood mood, out string id, out TextAsset track)
    {
        string tag = (emotion ?? "").Trim().ToLowerInvariant();
        int[] choices;
        switch (tag)
        {
            case "anger": case "menace": case "resentment": choices = new[] { 5, 1, 2 }; break;
            case "suspicion": case "distrust": case "interest": case "curiosity": choices = new[] { 3, 4 }; break;
            case "amusement": case "fondness": choices = new[] { 6, 7 }; break;
            case "contempt": case "pride": case "mockery": choices = new[] { 1, 6, 0 }; break;
            case "disgust": choices = new[] { 2, 1 }; break;
            case "irritation": case "annoyance": case "impatience": case "weariness": case "reluctance": choices = new[] { 1, 2, 0 }; break;
            default: choices = mood == GatekeeperMood.Furious ? new[] { 5, 2 } : new[] { 0, 4 }; break;
        }
        for (int n = 0; n < choices.Length; n++)
        {
            int at = choices[(turn + n) % choices.Length];
            if (at == last || clips[at] == null) continue;
            last = at; turn++; id = Ids[at]; track = tracks[at]; return clips[at];
        }
        id = ""; track = null; return null;
    }
}
