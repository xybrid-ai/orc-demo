using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

// Vesper's authored catalogue and tuning (TextAsset Spirit/SpiritLines.json). Field names match the JSON.
[Serializable] public sealed class SpiritLines
{
    [Serializable] public sealed class Timing
    {
        public float halo_in = .6f, halo_out = .8f, snap_out = .2f, listen_seconds = 6, silence_seconds = 3.5f;
        public int max_replies = 2, reply_chars = 80, long_draft = 60;
        public float decision_timeout = 2, voice_wait = 2, whisper_cooldown = 8, free_cooldown = 20;
        public int counsels_per_encounter = 4, appearances_per_encounter = 8, free_whispers = 3, name_every = 3;
    }
    [Serializable] public sealed class Rules
    {
        public string reckoning_preference = "passage";
        public int distraction_patience_delta;
        public bool label_in_fiction = true;
    }
    [Serializable] public sealed class Voice
    {
        public string engine = "XnPtts", checkpoint = "Phonon", voice_file = "Phonon/spirit.safetensors", label = "Bryony";
        public int threads = 4;   // used only without Trola's live speech; otherwise she opens at his thread count (SpiritVoice)
        public float preparation_timeout = 4;
    }
    [Serializable] public sealed class Visual
    {
        public float whisper_weight = .55f, vignette = .44f, smoothness = .8f, chromatic = .55f, aperture = 1.6f;
        public float saturation = -28, exposure = -.12f, bloom = .85f, slow_motion = .6f, camera_push_mm = 3;
    }
    [Serializable] public sealed class Line
    {
        public string id, family, key = "", text;
        public int min_patience, max_patience = 5;
        public bool always_name;
    }
    public int version = 1;
    public string spirit_name = "Vesper";
    public Timing timing = new Timing();
    public Rules rules = new Rules();
    public Voice voice = new Voice();
    public Visual visual = new Visual();
    public List<Line> lines = new List<Line>();

    public const string Traveller = "traveller";
    public const int MaxWords = 16, MaxSentences = 2, MaxChars = 100;
    // Validation names: short, the anonymous fallback, and a 16-character worst case.
    public static readonly string[] TestNames = { "Mira", Traveller, "Bartholomew-Rook" };
    private static readonly Regex TrailingVocative = new Regex(@",\s*\{name\}(?=[.!?,;:])", RegexOptions.CultureInvariant);
    private static readonly Regex LeadingVocative = new Regex(@"(^|[.!?]\s+)\{name\},\s*", RegexOptions.CultureInvariant);
    private static readonly Regex DroppedLead = new Regex(@"(^|[.!?]\s+)\{name\},\s*(\S?)", RegexOptions.CultureInvariant);
    private static readonly Regex Sentences = new Regex(@"[^.!?]+[.!?]*", RegexOptions.CultureInvariant);

    // Missing blocks fall back to the field defaults, so tuning files may stay partial.
    public static SpiritLines Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Spirit lines JSON is empty.");
        var data = UnityEngine.JsonUtility.FromJson<SpiritLines>(json) ?? throw new ArgumentException("Spirit lines JSON is not an object.");
        data.timing ??= new Timing(); data.rules ??= new Rules(); data.voice ??= new Voice(); data.visual ??= new Visual();
        data.lines ??= new List<Line>();
        foreach (var line in data.lines) if (line != null) line.key ??= "";
        return data;
    }

    public bool Validate(IEnumerable<(string family, string key, int minPatience, int maxPatience)> required, out string error)
    {
        error = Check(required);
        return error == null;
    }

    private string Check(IEnumerable<(string family, string key, int minPatience, int maxPatience)> required)
    {
        if (version != 1) return "Unsupported spirit lines version: " + version;
        if (string.IsNullOrWhiteSpace(spirit_name)) return "Missing spirit_name.";
        if (timing == null || rules == null || voice == null || visual == null || lines == null) return "Missing timing, rules, voice, visual or lines.";
        var t = timing;
        if (!Positive(t.halo_in, t.halo_out, t.snap_out, t.listen_seconds, t.silence_seconds, t.decision_timeout, t.voice_wait) || !Finite(t.whisper_cooldown, t.free_cooldown)
            || t.whisper_cooldown < 0 || t.free_cooldown < 0 || t.max_replies < 0 || t.max_replies > 4 || t.reply_chars < 1 || t.reply_chars > 240
            || t.long_draft < 1 || t.long_draft > t.reply_chars || t.counsels_per_encounter < 0 || t.appearances_per_encounter < t.counsels_per_encounter
            || t.free_whispers < 0 || t.name_every < 1)
            return "Invalid timing block.";
        if (rules.reckoning_preference != "passage" && rules.reckoning_preference != "friendship") return "reckoning_preference must be passage or friendship.";
        if (rules.distraction_patience_delta > 0 || rules.distraction_patience_delta < -1) return "distraction_patience_delta must be 0 or -1.";
        if (string.IsNullOrWhiteSpace(voice.engine) || string.IsNullOrWhiteSpace(voice.checkpoint) || string.IsNullOrWhiteSpace(voice.voice_file)
            || voice.threads < 1 || voice.threads > 16 || !Positive(voice.preparation_timeout))
            return "Invalid voice block.";
        var v = visual;
        if (!Finite(v.whisper_weight, v.vignette, v.smoothness, v.chromatic, v.aperture, v.saturation, v.exposure, v.bloom, v.slow_motion, v.camera_push_mm)
            || v.whisper_weight < 0 || v.whisper_weight > 1 || v.slow_motion < 0 || v.slow_motion > 1)
            return "Invalid visual block.";
        var ids = new HashSet<string>();
        foreach (var line in lines)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.id) || !ids.Add(line.id)) return "Every line needs a unique id: " + line?.id;
            if (string.IsNullOrWhiteSpace(line.family) || string.IsNullOrWhiteSpace(line.text)) return "Line needs a family and text: " + line.id;
            if (line.min_patience < 0 || line.max_patience > 5 || line.min_patience > line.max_patience) return "Invalid patience range: " + line.id;
            string stripped = line.text.Replace("{name}", "").Replace("{assigned}", "");
            if (stripped.Contains("{") || stripped.Contains("}")) return "Unknown slot: " + line.id;
            if (!line.always_name && LeadingVocative.Replace(TrailingVocative.Replace(line.text, ""), "$1").Contains("{name}"))
                return "{name} must be a vocative unless always_name: " + line.id;
            foreach (string name in TestNames)
                foreach (bool useName in new[] { true, false })
                {
                    if (line.always_name && !useName) continue;
                    string problem = Problem(Resolve(line.text, name, name == Traveller ? "Turnip" : name, useName));
                    if (problem != null) return problem + ": " + line.id + " (" + name + (useName ? "" : ", no name") + ")";
                }
        }
        if (required == null) return null;
        foreach (var need in required)
            for (int patience = Math.Max(0, need.minPatience); patience <= Math.Min(5, need.maxPatience); patience++)
                if (!Has(need.family, need.key ?? "", patience))
                    return "Missing line: " + need.family + (string.IsNullOrEmpty(need.key) ? "" : "." + need.key) + " at patience " + patience;
        return null;
    }

    public bool Has(string family, string key, int patience)
    {
        foreach (var line in lines ?? new List<Line>())
            if (line != null && line.family == family && (line.key ?? "") == key && patience >= line.min_patience && patience <= line.max_patience) return true;
        return false;
    }

    // A spoken line must survive the sanitizer unchanged; null means it is fine.
    public static string Problem(string resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved)) return "Empty line";
        if (resolved.Contains("{") || resolved.Contains("}")) return "Unresolved slot";
        if (Sentences.Matches(resolved).Count > MaxSentences) return "More than 2 sentences";
        if (resolved.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > MaxWords) return "More than 16 words";
        if (resolved.Length > MaxChars) return "More than 100 characters";
        return GatekeeperNpcModel.SanitizeSpeech(resolved) != resolved ? "Changed by SanitizeSpeech" : null;
    }

    // useName=false drops the vocative (", {name}" or "{name}, "); an empty name reads "traveller".
    public static string Resolve(string text, string name, string assigned, bool useName)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string who = string.IsNullOrWhiteSpace(name) ? Traveller : name.Trim();
        if (!useName)
        {
            text = TrailingVocative.Replace(text, "");
            text = DroppedLead.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
        }
        text = text.Replace("{name}", who).Replace("{assigned}", string.IsNullOrWhiteSpace(assigned) ? who : assigned.Trim());
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool Finite(params float[] values) { foreach (float v in values) if (float.IsNaN(v) || float.IsInfinity(v)) return false; return true; }
    private static bool Positive(params float[] values) { foreach (float v in values) if (!(v > 0) || float.IsInfinity(v)) return false; return true; }
}
