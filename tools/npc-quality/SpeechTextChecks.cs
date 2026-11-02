// Headless checks for GatekeeperSpeechText.Flow, the words Phonon is given for a flowing line. Every row of
// speech-text-cases.json (every Trola turn, authored and Vesper line plus edge cases, as the rules' Python prototype
// rewrote them) must match exactly; then the rules' invariants must hold over every authored line, every reply+question
// join verify.py budgets (both slot sizes) and every SpiritLines line (with and without a name). No Unity, no model.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class SpeechTextChecks
{
    private const string CasesFile = "tools/npc-quality/speech-text-cases.json", LinesFile = "Assets/XybridAvatarDemo/Resources/Spirit/SpiritLines.json";
    private const string LongName = "Bartholomew Rook", LongBusiness = "delivering a cartload of pickled turnips to my old aunt Ysol";
    private static readonly Regex Words = new Regex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*");
    private static readonly Regex Marks = new Regex("[?!]+");
    private static readonly Regex Stop = new Regex(@"([\p{L}\p{N}_]+)\.\s+(?=[\p{L}\p{N}])");
    private static readonly Regex Leftover = new Regex("[\"“”«»—–…]|\\.\\.\\.|\\s--?\\s");
    private static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.Ordinal) { "mr", "mrs", "ms", "dr", "st", "jr", "sr", "mt", "vs" };
    private sealed class Case { public string text = "", speech = ""; }

    public static object Run(JsonNode request, GatekeeperScenario scenario, GatekeeperStory.Document story)
    {
        var failures = new List<string>();
        var counts = new Dictionary<string, int>();
        string section = "";
        void Check(bool ok, string what)
        {
            counts[section] = counts.TryGetValue(section, out int n) ? n + 1 : 1;
            if (!ok && failures.Count < 100) failures.Add(section + ": " + what);
        }
        void Section(string name, Action body)
        {
            section = name;
            try { body(); } catch (Exception e) { Check(false, "crashed: " + e); }
        }
        // A flowing line keeps its words (case aside), every ? and ! (a run counts once), and one closing mark.
        void Invariants(string where, string text)
        {
            string speech = GatekeeperSpeechText.Flow(text);
            string seen = where + ": '" + speech + "'";
            Check(GatekeeperSpeechText.Flow(speech) == speech, "not idempotent: " + seen);
            Check(WordsOf(speech) == WordsOf(text), "words changed: " + seen);
            Check(Marks.Matches(speech).Count == Marks.Matches(text).Count, "a ? or ! was lost: " + seen);
            Check(speech.Length > 0 && ".!?".IndexOf(speech[speech.Length - 1]) >= 0 && (speech.Length < 2 || ".!?".IndexOf(speech[speech.Length - 2]) < 0),
                "does not end in exactly one of . ! ?: " + seen);
            Check(!Leftover.IsMatch(speech), "a quote, dash or ellipsis is left: " + seen);
            Check(InternalStops(speech) == 0, "an internal full stop is left: " + seen);
        }

        int fixtures = 0, authored = 0, spirit = 0;
        var joins = new HashSet<(string reply, string prompt)>();
        Section("fixtures", () =>
        {
            string path = request?["cases"]?.GetValue<string>() ?? Find(CasesFile);
            var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true });
            Check(cases.Count > 0, "no cases in " + path);
            foreach (var c in cases)
            {
                string speech = GatekeeperSpeechText.Flow(c.text);
                Check(speech == c.speech, "'" + c.text + "' became '" + speech + "', expected '" + c.speech + "'");
                Check(GatekeeperSpeechText.Flow(speech) == speech, "not idempotent: '" + c.text + "'");
                fixtures++;
            }
            Check(GatekeeperSpeechText.Flow(null) == null && GatekeeperSpeechText.Flow("") == "" && GatekeeperSpeechText.Flow(" \n") == " \n", "blank text is returned unchanged");
            Check(GatekeeperSpeechText.Flow("\"\"") == "\"\"", "a line that flows to nothing is spoken as written");
        });
        Section("authored", () =>
        {
            var texts = story.lines.ToDictionary(l => l.id, l => l.text);
            var nodes = story.nodes.ToDictionary(n => n.id);
            GatekeeperStory.Variant At(string node, int p) => nodes[node].variants.First(v => v.min_patience <= p && p <= v.max_patience);
            IEnumerable<string> Ids(string[] ids) => ids ?? new string[0];
            // The same joins as verify.py: the replies that run into the business prompt, the hub or the reckoning.
            foreach (string reply in new[] { "named-1", "assigned-reply" }) joins.Add((reply, "business-ask"));
            for (int p = 0; p < 6; p++)
            {
                foreach (string n in new[] { "business-accepted", "business-assigned" })
                    foreach (string r in Ids(At(n, p).line_ids)) foreach (string q in Ids(At("hub", p).line_ids)) joins.Add((r, q));
                foreach (string n in new[] { "flatter", "bribe", "threaten", "camp" })
                    foreach (string r in Ids(At(n, p).line_ids).Concat(Ids(At(n, p).revisit_line_ids)))
                        foreach (string q in Ids(At("hub", p).revisit_line_ids).Concat(Ids(At("reckoning", p).line_ids))) joins.Add((r, q));
            }
            foreach (bool longSlots in new[] { false, true })
            {
                string name = longSlots ? LongName : "Mira", business = longSlots ? LongBusiness : "selling turnips", size = longSlots ? " (long slots)" : "";
                Func<string, string> fill = t => GatekeeperNames.Resolve(t, name, GatekeeperNames.Mangle(name), business);
                foreach (var line in story.lines) { Invariants(line.id + size, fill(line.text)); authored++; }
                foreach (var (r, q) in joins)
                    Invariants(r + "+" + q + size, GatekeeperLineSources.Join(new GatekeeperScenario.Line { text = fill(texts[r]) }, new GatekeeperScenario.Line { text = fill(texts[q]) }).text);
            }
        });
        Section("spirit", () =>
        {
            string path = request?["lines"]?.GetValue<string>() ?? Find(LinesFile);
            var lines = SpiritLines.Parse(File.ReadAllText(path));
            foreach (var line in lines.lines)
                foreach (string name in SpiritLines.TestNames)
                    foreach (bool useName in new[] { true, false })
                    {
                        if (line.always_name && !useName) continue;
                        Invariants(line.id + " (" + name + (useName ? "" : ", no name") + ")", SpiritLines.Resolve(line.text, name, name == SpiritLines.Traveller ? "Turnip" : name, useName));
                        spirit++;
                    }
        });
        return new
        {
            passed = failures.Count == 0,
            checks = fixtures + " fixtures exact, invariants over " + authored + " authored lines, " + 2 * joins.Count + " joins, " + spirit + " spirit lines; "
                + counts.Values.Sum() + " checks",
            joins = joins.Count, failures
        };
    }

    private static string WordsOf(string text) => string.Join(" ", Words.Matches(text.ToLowerInvariant()).Select(m => m.Value));

    // Full stops followed by a word, other than after an initial ("J. R.") or a short abbreviation ("Dr.").
    public static int InternalStops(string speech) =>
        Stop.Matches(speech).Count(m => !(m.Groups[1].Length == 1 && char.IsUpper(m.Groups[1].Value[0])) && !Abbreviations.Contains(m.Groups[1].Value.ToLowerInvariant()));

    private static string Find(string relative)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException("Not found from the working or adapter folder: " + relative);
    }
}
