using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// The presenter's view of Vesper (F6, or -xybridLens): voice and d1 status, the open moment and Trola's clock, the
// patience read, the story graph, the last thought with its labels, rule and timings, and Trola's last d1 decision.
// At Ended it adds the run receipt for 6 s. The same text (plus the run receipt and a short journal) is kept in
// persistentDataPath/SpiritSpeechDebug/spirit-session.txt. Read-only: it never changes the game or the spirit.
public sealed class SpiritLens : MonoBehaviour
{
    public const string SessionFile = "spirit-session.txt";
    private const float Width = 440, ReceiptSeconds = 6, ComposeEvery = .2f, WriteEvery = 1;
    private static readonly Regex TrolaD1 = new Regex(@"^d1 .*; (\d+) ms", RegexOptions.CultureInvariant);

    public SpiritDirector director;
    public bool Visible { get; set; }

    private string text = "", receipt = "", written = "";
    private float nextCompose, nextWrite;
    private bool showReceipt;
    private GUIStyle style;
    private readonly GUIContent content = new GUIContent();

    private void Update()
    {
        if (director == null) return;
        float now = Time.realtimeSinceStartup;
        if (now < nextCompose) return;
        nextCompose = now + ComposeEvery;
        try
        {
            text = Compose();
            receipt = Receipt();
            showReceipt = director.EndedAt >= 0 && now - director.EndedAt < ReceiptSeconds;
        }
        catch (Exception error) { text = "Spirit lens: " + error.Message; receipt = ""; showReceipt = false; }
        if (now >= nextWrite) { nextWrite = now + WriteEvery; Write(); }
    }

    private string Compose()
    {
        var d = director; var c = d.Controller; var w = d.Window; var t = d.Lines.timing;
        var s = new StringBuilder(768);
        s.Append("Spirit: ").Append(d.Lines.spirit_name).Append(" · ").Append(d.Voice != null ? d.Voice.Describe() : "no voice")
            .Append("  (Trola: ").Append(TrolaVoice(c)).Append(')').AppendLine();
        if (d.Status.Length > 0) s.AppendLine(d.Status);
        s.AppendLine(d.DecisionStatus);
        if (c == null) return s.ToString();

        var moment = d.Moment;
        s.Append("Moment ").Append(d.Policy.Scheduler.Appearances).Append('/').Append(t.appearances_per_encounter)
            .Append(" · counsels ").Append(d.Policy.Scheduler.Counsels).Append('/').Append(t.counsels_per_encounter);
        if (moment != null) s.Append(" · ").Append(d.MomentLabel);
        s.Append(" · ").Append(w.State);
        if (w.IsOpen && moment != null && moment.Kind == SpiritMomentKind.Counsel)
            s.Append(' ').Append(F1(w.ListenUsed)).Append('/').Append(F1(w.ListenSeconds)).Append(" s · replies ").Append(w.RepliesUsed).Append('/').Append(moment.MaxReplies);
        s.Append(" · Trola clock ").Append(c.IsIdleHeld ? "held " : "running ").Append(F1(c.IdleElapsed)).Append('/').Append(c.IdleTauntSeconds.ToString("0", CultureInfo.InvariantCulture)).Append(" s");
        s.AppendLine();

        var at = d.Checkpoint;
        int patience = c.Patience;
        s.Append("Read: p").Append(patience).Append(" (").Append(Signed(at != null ? at.LastDelta : 0)).Append(") → ")
            .Append(SpiritAdvisor.Band(patience).ToString().ToLowerInvariant()).Append(" · used: ")
            .Append(at != null && at.UsedTargets.Count > 0 ? string.Join(", ", at.UsedTargets) : "none");
        if (at != null) s.Append(" · ").Append(at.Stage).Append(" visit ").Append(at.Visit);
        s.AppendLine();
        if (d.Advice != null) s.Append("Graph: ").AppendLine(d.Advice.Trace);

        var thought = d.LastThought;
        if (thought != null)
        {
            s.Append('#').Append(thought.Number).Append(" \"").Append(thought.Text).Append("\" → ")
                .AppendLine(thought.Decision != null ? thought.Decision.Detail : "thinking…");
            if (thought.Cue != null)
            {
                s.Append("   → ").Append(thought.Cue.Rule);
                // d1's own time only for a d1 label (keyword decisions carry no time); `shown` includes the dissolve.
                if (thought.Decision != null && thought.Decision.Source == SpiritDecisionSource.D1) s.Append(" · d1 ").Append(Ms(thought.Decision.Ms));
                else s.Append(" · keywords");
                s.Append(" · shown ").Append(Ms(thought.LabelMs));
                s.Append(thought.Silent ? " · subtitle only" : thought.VoiceMs >= 0 ? " · voice " + Ms(thought.VoiceMs) : "");
                if (thought.FirstAudioMs >= 0) s.Append(" · first ").Append(thought.Silent ? "line " : "audio ").Append(Ms(thought.FirstAudioMs));
                s.AppendLine();
            }
        }
        s.Append("Trola d1: ").AppendLine(LastTrolaDecision(c));
        return s.ToString();
    }

    // "All on this laptop": every d1 call this run (hers and Trola's), the rules that fired, and who wrote what.
    private string Receipt()
    {
        var c = director.Controller;
        var times = new List<double>(director.D1Milliseconds);
        int gemma = 0;
        if (c != null)
            foreach (var entry in c.DebugLines)
            {
                var line = entry?.line;
                if (line == null) continue;
                if (line.debugSource == "LLM") gemma++;
                var match = TrolaD1.Match(line.debugDecision ?? "");
                if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double ms)) times.Add(ms);
            }
        times.Sort();
        double median = times.Count == 0 ? 0 : times.Count % 2 == 1 ? times[times.Count / 2] : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
        return "All on this laptop, no cloud calls: " + times.Count + " d1 decision" + (times.Count == 1 ? "" : "s")
            + (times.Count > 0 ? " (median " + median.ToString("0", CultureInfo.InvariantCulture) + " ms)" : "")
            + " · " + director.Policy.RuleFirings + " rule firings · Gemma wrote " + gemma + " of Trola's lines · spirit lines written by an LLM: 0";
    }

    private static string TrolaVoice(GatekeeperController c)
    {
        var speech = c != null ? c.liveSpeech : null;
        if (speech == null) return "no voice";
        return speech.provider == GatekeeperLiveSpeech.Provider.Phonon
            ? (speech.phononEngine == PhononEngine.Zzz ? "zzz rc.9" : "xn-ptts 0.6.0") + "; " + speech.phononThreads + " threads"
            : speech.provider + "; " + speech.kittenThreads + " threads";
    }

    private static string LastTrolaDecision(GatekeeperController c)
    {
        for (int i = c.DebugLines.Count - 1; i >= 0; i--)
        {
            string decision = c.DebugLines[i]?.line?.debugDecision;
            if (!string.IsNullOrEmpty(decision)) return decision;
        }
        return "none yet";
    }

    private static string F1(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Ms(double value) => value < 0 ? "–" : value.ToString("0", CultureInfo.InvariantCulture) + " ms";
    private static string Signed(int value) => value > 0 ? "+" + value : value.ToString(CultureInfo.InvariantCulture);

    private void Write()
    {
        var body = new StringBuilder(text.Length + 2048).Append(text).AppendLine(receipt).AppendLine("Journal:");
        foreach (string entry in director.Journal) body.AppendLine(entry);
        string next = body.ToString();
        if (next == written) return;
        written = next;
        try
        {
            string folder = Path.Combine(Application.persistentDataPath, SpiritVoice.DebugFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, SessionFile), "UTC: " + DateTime.UtcNow.ToString("O") + "\n" + next);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { } // A debug file must never disturb play.
    }

    private void OnGUI()
    {
        if (!Visible || director == null) return;
        if (style == null) style = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false, fontSize = 12 };
        content.text = showReceipt && receipt.Length > 0 ? text + "\n" + receipt : text;
        // Below her band (her line at .17 from the top; thought, mind field and hint down to about .30), beside Trola.
        float x = Mathf.Max(12, Screen.width - Width - 12), y = Mathf.Max(12, Screen.height * .31f);
        float height = Mathf.Min(Screen.height - y - 12, style.CalcHeight(content, Width - 16) + 14);
        GUI.Box(new Rect(x, y, Width, height), GUIContent.none);
        GUI.Label(new Rect(x + 8, y + 6, Width - 16, height - 12), content, style);
    }
}
