using System;
using System.Collections.Generic;
using UnityEngine;

// Authored graph layered over the existing encounter, slots and voice playback.
public sealed class GatekeeperStory
{
    [Serializable] public sealed class Document
    {
        public int version = 1;
        public float idle_seconds = 8;
        public int hub_rounds = 2;
        public List<GatekeeperScenario.Line> lines;
        public List<Node> nodes;
        public string[] idle_lines;
        public string interruption_line;
        // Spoken answers at a choice checkpoint that pick no button: round-robin rebukes costing one patience each, the
        // warning spoken when that leaves none (the next one is thrown out), and free "say it again" lines.
        public string[] off_topic_lines;
        public string off_topic_last_line;
        public string[] unclear_lines;
    }
    [Serializable] public sealed class Node
    {
        public string id;
        public string input_slot;
        public string next;
        public List<Variant> variants;
        public List<Choice> choices;
    }
    [Serializable] public sealed class Variant
    {
        public int min_patience, max_patience = 5;
        public string[] line_ids;
        // Spoken instead of line_ids when the story comes back to this node: the hub after a round, an approach chosen twice.
        public string[] revisit_line_ids;
        public string outcome;
    }
    [Serializable] public sealed class Choice
    {
        public string label, target;
        // The player's words for this choice: shown on screen, never voiced. The label stays the button text and the key for Vesper and the tests.
        public string say;
        public int patience_delta;
        public int repeat_delta;
    }
    public Document Data { get; }
    public GatekeeperEncounter Encounter { get; }
    public Node CurrentNode { get; private set; }
    public Variant CurrentVariant { get; private set; }
    public int HubRounds { get; private set; }
    public bool BusinessCaptured { get; private set; }
    // True when the current node was already entered earlier in this encounter.
    public bool Revisit { get; private set; }
    public string[] CurrentLines => Revisit && CurrentVariant.revisit_line_ids != null && CurrentVariant.revisit_line_ids.Length > 0 ? CurrentVariant.revisit_line_ids : CurrentVariant.line_ids;
    public bool WaitsForPlayer => !string.IsNullOrEmpty(CurrentNode.input_slot) || CurrentNode.choices != null && CurrentNode.choices.Count > 0;
    private readonly HashSet<string> entered = new HashSet<string>();
    private readonly HashSet<string> approaches = new HashSet<string>();
    private int idleIndex = -1, offTopicIndex = -1, unclearIndex = -1;

    public GatekeeperStory(GatekeeperScenario scenario, GatekeeperEncounter encounter = null)
    {
        Data = scenario.ReadStory();
        Encounter = encounter ?? new GatekeeperEncounter(3, 5, true);
        Enter("name");
    }
    public void Enter(string id)
    {
        CurrentNode = Data.nodes.Find(n => n.id == id) ?? throw new InvalidOperationException("Missing story node: " + id);
        CurrentVariant = CurrentNode.variants.Find(v => Encounter.Patience >= v.min_patience && Encounter.Patience <= v.max_patience)
            ?? throw new InvalidOperationException("No patience variant: " + id);
        Revisit = !entered.Add(id);
    }
    public void CaptureName(string input, string[] blocklist, string fallback)
    {
        Encounter.CaptureName(input, blocklist, fallback);
        Enter(Encounter.NameWasAssigned ? "assigned" : "named");
    }
    public void CaptureBusiness(string input, string[] blocklist)
    {
        Encounter.CaptureBusiness(input, blocklist); BusinessCaptured = true;
        Enter(Encounter.BusinessWasAssigned ? "business-assigned" : "business-accepted");
    }
    public bool Choose(int index)
    {
        var options = CurrentNode.choices;
        if (Encounter.Outcome != GatekeeperOutcome.Playing || options == null || index < 0 || index >= options.Count) return false;
        var choice = options[index];
        bool hub = CurrentNode.id == "hub";
        int delta = hub && !approaches.Add(choice.target) ? choice.repeat_delta : choice.patience_delta;
        Encounter.ChangePatience(delta); Encounter.CountStoryTurn();
        if (hub) HubRounds++;
        Enter(choice.target);
        return true;
    }
    public bool Advance()
    {
        if (CurrentNode.input_slot != null && CurrentNode.input_slot.Length > 0 || CurrentNode.choices != null && CurrentNode.choices.Count > 0) return false;
        string next = CurrentNode.next;
        if (next == "hub" && HubRounds >= Data.hub_rounds) next = "reckoning";
        if (string.IsNullOrEmpty(next)) return false;
        Enter(next); return true;
    }
    public void Finish()
    {
        if (!string.IsNullOrEmpty(CurrentVariant.outcome))
            Encounter.FinishStory((GatekeeperOutcome)Enum.Parse(typeof(GatekeeperOutcome), CurrentVariant.outcome));
    }
    // Round-robin guarantees no adjacent repeat, including after interruptions.
    public string NextIdleLine() { idleIndex = (idleIndex + 1) % Data.idle_lines.Length; return Data.idle_lines[idleIndex]; }
    // At one patience the rebuke that spends it is the warning; above that, round-robin.
    public string NextOffTopicLine(int patience)
    {
        if (patience <= 1) return Data.off_topic_last_line;
        offTopicIndex = (offTopicIndex + 1) % Data.off_topic_lines.Length; return Data.off_topic_lines[offTopicIndex];
    }
    public string NextUnclearLine() { unclearIndex = (unclearIndex + 1) % Data.unclear_lines.Length; return Data.unclear_lines[unclearIndex]; }

    public static bool Validate(GatekeeperScenario scenario, out string error)
    {
        error = null;
        if (!scenario.captureName || scenario.initialPatience != 3 || scenario.authoredSource == null) { error = "Authored story needs name capture, patience 3, and a source."; return false; }
        Document data;
        try { data = scenario.ReadStory(); }
        catch (Exception ex) { error = "Invalid story JSON: " + ex.Message; return false; }
        if (data == null || data.nodes == null || data.lines == null || data.hub_rounds != 2 || data.idle_seconds < 7 || data.idle_seconds > 9 || data.idle_lines == null || data.idle_lines.Length < 3)
        { error = "Provide a two-round graph and several eight-second idle taunts."; return false; }
        if (scenario.lines == null || scenario.fallbackNames == null || scenario.fallbackNames.Length == 0) { error = "Missing lines or fallback names."; return false; }
        foreach (string fallback in scenario.fallbackNames)
            if (!GatekeeperNames.TrySanitize(fallback, scenario.nameBlocklist, out var safe) || safe != fallback) { error = "Unsafe fallback name."; return false; }
        var ids = new HashSet<string>();
        foreach (var line in scenario.lines)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.id) || !ids.Add(line.id) || string.IsNullOrWhiteSpace(line.text) || string.IsNullOrWhiteSpace(line.emotion) || float.IsNaN(line.intensity) || float.IsInfinity(line.intensity) || line.intensity < 0 || line.intensity > 1 || Math.Abs(line.patience_delta) > 5)
            { error = "Every line needs valid text, emotion, intensity, patience_delta and ends_scene."; return false; }
            string stripped = line.text.Replace("{name}", "").Replace("{name_mangled}", "").Replace("{business}", "");
            if (stripped.Contains("{") || stripped.Contains("}")) { error = "Unknown slot: " + line.id; return false; }
            if (line.audio != null && (GatekeeperNames.HasSlots(line.text) || line.mouthCues == null || !XybridAvatarPlayer.ValidateCues(line.mouthCues, line.audio, out error)))
            { error = "Invalid baked audio/cues: " + line.id; return false; }
        }
        if (!ids.Contains(data.interruption_line) || scenario.FindLine(data.interruption_line).patience_delta != -1) { error = "Interruption must cost one patience."; return false; }
        foreach (string id in data.idle_lines) if (!ids.Contains(id) || scenario.FindLine(id).ends_scene || scenario.FindLine(id).patience_delta != 0) { error = "Invalid idle taunt."; return false; }
        if (data.off_topic_lines == null || data.off_topic_lines.Length == 0 || data.unclear_lines == null || data.unclear_lines.Length == 0)
        { error = "Provide off-topic and unclear reactions for spoken answers."; return false; }
        foreach (string id in new List<string>(data.off_topic_lines) { data.off_topic_last_line })
            if (id == null || !ids.Contains(id) || scenario.FindLine(id).ends_scene || scenario.FindLine(id).patience_delta != -1) { error = "Off-topic reactions must cost one patience: " + id; return false; }
        foreach (string id in data.unclear_lines)
            if (!ids.Contains(id) || scenario.FindLine(id).ends_scene || scenario.FindLine(id).patience_delta != 0) { error = "Unclear reactions must be free: " + id; return false; }
        var nodes = new HashSet<string>();
        foreach (var node in data.nodes) if (node == null || string.IsNullOrEmpty(node.id) || !nodes.Add(node.id)) { error = "Duplicate or empty node."; return false; }
        foreach (string id in new[] { "name", "named", "assigned", "business", "business-accepted", "business-assigned", "hub", "flatter", "bribe", "threaten", "camp", "reckoning", "passage", "friendship", "expelled" })
            if (!nodes.Contains(id)) { error = "Missing story node: " + id; return false; }
        foreach (var node in data.nodes)
        {
            if (!string.IsNullOrEmpty(node.next) && !nodes.Contains(node.next) || node.variants == null || node.variants.Count == 0)
            { error = "Invalid next/variants: " + node.id; return false; }
            for (int patience = 0; patience <= 5; patience++)
                if (node.variants.FindAll(v => v != null && patience >= v.min_patience && patience <= v.max_patience).Count != 1)
                { error = "Variants must cover patience 0-5 exactly once: " + node.id; return false; }
            foreach (var variant in node.variants)
            {
                if (variant.line_ids == null || variant.line_ids.Length == 0 || variant.min_patience < 0 || variant.max_patience > 5 || variant.min_patience > variant.max_patience)
                { error = "Invalid variant: " + node.id; return false; }
                for (int i = 0; i < variant.line_ids.Length; i++)
                {
                    var line = scenario.FindLine(variant.line_ids[i]);
                    if (line == null || line.ends_scene != (i == variant.line_ids.Length - 1 && !string.IsNullOrEmpty(variant.outcome)))
                    { error = "Invalid line or terminal marker: " + node.id; return false; }
                    if (node.id == "name" && GatekeeperNames.HasSlots(line.text)) { error = "Name demand cannot use uncaptured slots."; return false; }
                }
                foreach (string id in variant.revisit_line_ids ?? Array.Empty<string>())
                    if (scenario.FindLine(id) == null || scenario.FindLine(id).ends_scene || !string.IsNullOrEmpty(variant.outcome)) { error = "Invalid revisit line: " + node.id; return false; }
                if (!string.IsNullOrEmpty(variant.outcome) && (!Enum.TryParse<GatekeeperOutcome>(variant.outcome, out var outcome) || outcome == GatekeeperOutcome.Playing || !string.IsNullOrEmpty(node.next) || node.choices != null && node.choices.Count > 0))
                { error = "Invalid ending: " + node.id; return false; }
            }
            foreach (var choice in node.choices ?? new List<Choice>())
                if (choice == null || string.IsNullOrWhiteSpace(choice.label) || string.IsNullOrWhiteSpace(choice.say) || choice.say.Contains("{") || choice.say.Contains("}") || !nodes.Contains(choice.target) || choice.patience_delta == 0 || Math.Abs(choice.patience_delta) > 5 || node.id == "hub" && choice.repeat_delta == 0)
                { error = "Every choice needs words, a target and patience movement: " + node.id; return false; }
        }
        foreach (GatekeeperMood mood in Enum.GetValues(typeof(GatekeeperMood)))
            if (scenario.Voice(mood) == null) { error = "Missing existing mood voice: " + mood; return false; }
        return true;
    }
}
