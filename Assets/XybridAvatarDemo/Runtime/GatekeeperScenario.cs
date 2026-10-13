using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Xybrid/Gatekeeper Scenario")]
public sealed class GatekeeperScenario : ScriptableObject
{
    [Serializable] public sealed class Line
    {
        public string id;
        [NonSerialized] public string debugSource = "authored", debugRaw = "";
        [NonSerialized] public double debugModelSeconds;
        public GatekeeperMood recordedMood;
        [TextArea(2, 4)] public string text;
        public string emotion;
        [Range(0, 1)] public float intensity;
        public int patience_delta;
        public bool ends_scene;
        public string delivery;
        public AudioClip audio;
        public TextAsset mouthCues;
    }
    [Serializable] public sealed class VoiceProfile
    {
        public GatekeeperMood mood;
        [Range(.7f, 1.2f)] public float pitch = 1;
        [Range(.3f, 1)] public float volume = 1;
        [Range(0, 1)] public float anger;
        [Range(0, 1)] public float amusement;
    }
    [Range(1, 100)] public int initialPatience = 75;
    [Range(2, 12)] public int maxTurns = 5;
    public bool captureName;
    public bool authoredConversation;
    public bool debugStartInFreeForm;
    public bool debugUsePlaceholderReplies;
    public TextAsset authoredSource;
    public GatekeeperStory.Document ReadStory() => JsonUtility.FromJson<GatekeeperStory.Document>(authoredSource.text);
    public string[] fallbackNames = { "Worm", "Turnip", "Pebble" };
    public string[] nameBlocklist = GatekeeperNames.DefaultBlocklist;
    [Serializable] public sealed class Choice
    {
        public string label;
        public string reply;
    }
    public List<Choice> choices = new List<Choice>();
    public List<Line> lines = new List<Line>();
    public List<VoiceProfile> voices = new List<VoiceProfile>();
    public Line FindLine(string id) => lines.Find(line => line != null && line.id == id);
    public VoiceProfile Voice(GatekeeperMood mood) => voices.Find(voice => voice != null && voice.mood == mood);

    public bool Validate(out string error)
    {
        if (authoredConversation) return GatekeeperStory.Validate(this, out error);
        if (initialPatience < 1 || initialPatience > 100 || maxTurns < 2 || maxTurns > 12) { error = "Invalid encounter limits."; return false; }
        if (lines == null || voices == null) { error = "Missing line or voice list."; return false; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.id) || string.IsNullOrWhiteSpace(line.text) || !ids.Add(line.id)) { error = "Every line needs unique ID and text."; return false; }
            bool live = captureName && (GatekeeperNames.HasSlots(line.text) || line.id == "name-demand");
            if (!live && (line.audio == null || line.mouthCues == null || !XybridAvatarPlayer.ValidateCues(line.mouthCues, line.audio, out error))) { error = "Missing or invalid voice/cues for: " + line.id; return false; }
            string stripped = line.text.Replace("{name}", "").Replace("{name_mangled}", "");
            if (stripped.Contains("{") || stripped.Contains("}")) { error = "Unknown dialogue slot: " + line.id; return false; }
        }
        foreach (string id in new[] { "intro", "ask-bored", "ask-irritated", "ask-furious", "question", "polite", "flatter", "bribe", "bad-bribe", "repeat", "insult-irritated", "insult-furious", "apology", "win", "lose", "withdraw" })
            if (!ids.Contains(id)) { error = "Missing encounter reply: " + id; return false; }
        foreach (GatekeeperMood mood in Enum.GetValues(typeof(GatekeeperMood)))
        {
            var voice = Voice(mood);
            if (voice == null || voices.FindAll(v => v != null && v.mood == mood).Count != 1 || float.IsNaN(voice.pitch) || float.IsNaN(voice.volume) || float.IsNaN(voice.anger) || float.IsNaN(voice.amusement) || voice.pitch < .7f || voice.pitch > 1.2f || voice.volume < .3f || voice.volume > 1 || voice.anger < 0 || voice.anger > 1 || voice.amusement < 0 || voice.amusement > 1)
            { error = "Each mood needs a valid voice profile: " + mood; return false; }
        }
        if (captureName)
        {
            if (FindLine("name-demand") == null || GatekeeperNames.HasSlots(FindLine("name-demand").text) || FindLine("name-assigned") == null || choices == null || choices.Count == 0)
            { error = "Name opening needs a demand, assigned-name reaction, and authored choices."; return false; }
            if (fallbackNames == null || fallbackNames.Length == 0) { error = "Provide fallback names."; return false; }
            foreach (string fallback in fallbackNames)
                if (!GatekeeperNames.TrySanitize(fallback, nameBlocklist, out var safe) || safe != fallback)
                { error = "Unsafe authored fallback name."; return false; }
            foreach (var choice in choices)
                if (choice == null || string.IsNullOrWhiteSpace(choice.label) || !GatekeeperIntentRules.TryInterpret(choice.reply, out _))
                { error = "Each choice needs a label and an authored reply."; return false; }
        }
        error = null;
        return true;
    }
}
