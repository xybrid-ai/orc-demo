using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// Read-only advice diagnostics (F6 / -xybridLens): voice, current cue, checkpoint and Trola's clock.
// Keeps the local session receipt without exposing a second player conversation or marking winning choices.
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
        var d = director; var c = d.Controller;
        var s = new StringBuilder(512);
        s.Append("Spirit: ").Append(d.Lines.spirit_name).Append(" · advice only · ")
            .AppendLine(d.Voice != null ? d.Voice.Describe() : "no voice");
        if (d.Status.Length > 0) s.AppendLine(d.Status);
        s.Append("Advice ").Append(d.IsAdvising ? "active" : "idle").Append(" · cues ").Append(d.AdviceCount)
            .Append(" · gain ").Append(F1(d.Voice != null ? d.Voice.gain : d.Lines.voice.gain))
            .Append(" · effects ").AppendLine(F1(d.Presence != null ? d.Presence.effectsStrength : d.Lines.visual.effects_strength));
        if (c == null) return s.ToString();
        s.Append("Trola: ").Append(TrolaVoice(c)).Append(" · clock ").Append(c.IsIdleHeld ? "held " : "running ")
            .Append(F1(c.IdleElapsed)).Append('/').Append(c.IdleTauntSeconds.ToString("0", CultureInfo.InvariantCulture)).AppendLine(" s");
        var at = d.Checkpoint;
        s.Append("Read: p").Append(c.Patience).Append(" · ").Append(SpiritAdvisor.Band(c.Patience).ToString().ToLowerInvariant());
        if (at != null) s.Append(" · checkpoint ").Append(at.AdviceKey).Append(" · previous ").Append(at.LastApproach.Length > 0 ? at.LastApproach : "none");
        s.AppendLine();
        if (d.CurrentCue != null) s.Append("Authored: ").Append(d.CurrentCue.LineId).Append(" · ").AppendLine(d.CurrentCue.Text);
        s.Append("Trola d1: ").AppendLine(LastTrolaDecision(c));
        return s.ToString();
    }

    // d1 receipts describe Trola's decisions only; Vesper's runtime does not classify player speech.
    private string Receipt()
    {
        var c = director.Controller;
        var times = new List<double>();
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
            + " · " + director.AdviceCount + " authored adviser cues · Gemma wrote " + gemma + " of Trola's lines · spirit lines written by an LLM: 0";
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
        // Beside Trola, below Vesper's subtitle band.
        float x = Mathf.Max(12, Screen.width - Width - 12), y = Mathf.Max(12, Screen.height * .31f);
        float height = Mathf.Min(Screen.height - y - 12, style.CalcHeight(content, Width - 16) + 14);
        GUI.Box(new Rect(x, y, Width, height), GUIContent.none);
        GUI.Label(new Rect(x + 8, y + 6, Width - 16, height - 12), content, style);
    }
}
