using System;
using System.Diagnostics;
using UnityEngine;

// Where the time goes between a trigger (the player's input, or the end of the previous line) and the
// line's first audio. Every stage is stamped on one clock (Stopwatch), on the voice worker and on the
// main thread alike. The stages tile the wait, so they add up to the total. Voice work that ran before
// the line was needed (a prefetch) counts only for the part the player still waited for; its full
// durations are kept in the raw fields.
[Serializable]
public sealed class GatekeeperLineTiming
{
    public static long Now => Stopwatch.GetTimestamp();
    public static float Ms(long from, long to) => from == 0 || to == 0 ? 0 : (float)((to - from) * 1000.0 / Stopwatch.Frequency);
    // The part of [from, to] that falls inside [windowStart, windowEnd].
    public static float Overlap(long from, long to, long windowStart, long windowEnd) =>
        from == 0 || to == 0 ? 0 : Math.Max(0, Ms(Math.Max(from, windowStart), Math.Min(to, windowEnd)));

    // speech: the words the voice was given (GatekeeperSpeechText.Flow of text for a flowing Phonon line), else text.
    public string id = "", text = "", speech = "", trigger = "", voice = "", fallback = "None";
    public int words, characters;
    public float audioSeconds;
    // Counted milliseconds; they add up to totalMs.
    public float pacingMs, lineSourceMs, queueMs, synthesisMs, rhubarbMs, handoffMs, clipMs, playbackStartMs, totalMs;
    // Full durations of the voice work, including any part done before the trigger.
    public float rawSynthesisMs, rawRhubarbMs;
    public float firstSoundMs, firstWordMs = -1, cuesReadyMs;
    public string reaction = "", analysisError = "";
    public bool analysisComplete;
    [NonSerialized] public long triggerTimestamp;
    // First non-silent 10 ms window of a clip, in clip seconds. For a reply this is the word
    // onset; prepared nonlexical reactions count only toward firstSoundMs. Mixer buffering excluded.
    public static float FirstSoundOffset(AudioClip clip)
    {
        if (clip == null || clip.samples <= 0) return 0;
        // Read only until onset, in 100 ms blocks. Timing probes must not copy entire
        // clips on the speaking frame or increase GC pressure during a conversation.
        int block = Math.Min(clip.samples, Math.Max(1, clip.frequency / 10));
        var samples = new float[block * clip.channels];
        int hop = Math.Max(1, clip.frequency / 100);
        for (int offset = 0; offset < clip.samples; offset += block)
        {
            if (!clip.GetData(samples, offset)) return 0;
            int frames = Math.Min(block, clip.samples - offset);
            for (int frame = 0; frame < frames; frame += hop)
            {
                double sum = 0; int count = 0;
                for (int i = frame * clip.channels; i < Math.Min(frames * clip.channels, (frame + hop) * clip.channels); i++)
                { sum += samples[i] * samples[i]; count++; }
                if (count > 0 && Math.Sqrt(sum / count) > .005) return (offset + frame) / (float)clip.frequency;
            }
        }
        return clip.length;
    }
    public float OtherMs => totalMs - synthesisMs - rhubarbMs;

    public static int CountWords(string text)
    {
        int words = 0; bool inWord = false;
        foreach (char ch in text ?? "")
        {
            bool part = char.IsLetterOrDigit(ch) || ch == '\'';
            if (part && !inWord) words++;
            inWord = part;
        }
        return words;
    }
}
