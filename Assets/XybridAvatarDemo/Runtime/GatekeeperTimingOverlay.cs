using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

// Debug overlay: where the wait before each line's first audio went (see GatekeeperLineTiming).
// F3 toggles it in the editor and in a built player; "-xybridTimingOverlay" starts a player with it shown.
// Read-only: it never writes to the scene or the conversation.
public sealed class GatekeeperTimingOverlay : MonoBehaviour
{
    public GatekeeperController gatekeeper;
    public Key toggleKey = Key.F3;
    public bool visible;
    [Range(1, 20)] public int history = 8;

    private readonly List<GatekeeperLineTiming> lines = new List<GatekeeperLineTiming>();
    private GatekeeperLineTiming lastInput;
    private GUIStyle style;
    private readonly StringBuilder text = new StringBuilder();
    private bool textStale = true;

    private void Awake()
    {
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-xybridTimingOverlay") >= 0) visible = true;
    }
    private void OnEnable() { if (gatekeeper != null) gatekeeper.TimingRecorded += Add; }
    private void OnDisable() { if (gatekeeper != null) gatekeeper.TimingRecorded -= Add; }
    private void OnDestroy() { if (style != null) Destroy(style.normal.background); }

    private void Add(GatekeeperLineTiming timing)
    {
        if (!lines.Contains(timing)) lines.Add(timing);
        while (lines.Count > history) lines.RemoveAt(0);
        if (timing.trigger == "typed input" || timing.trigger == "choice") lastInput = timing;
        textStale = true;
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard[toggleKey].wasPressedThisFrame) visible = !visible;
    }

    private void OnGUI()
    {
        if (!visible) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = false, wordWrap = false, padding = new RectOffset(10, 10, 8, 8) };
            style.font = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Courier New" }, 13);
            style.normal.textColor = Color.white;
            var background = new Texture2D(1, 1); background.SetPixel(0, 0, new Color(0, 0, 0, .78f)); background.Apply();
            style.normal.background = background;
        }
        if (textStale) { Compose(); textStale = false; }
        var content = new GUIContent(text.ToString());
        var size = style.CalcSize(content);
        GUI.Box(new Rect(12, 12, size.x, size.y), content, style);
    }

    private void Compose()
    {
        text.Clear();
        text.Append("Voice timing  (").Append(toggleKey).Append(" hides)  ").Append(Application.isEditor ? "editor" : "player").Append('\n');
        if (lines.Count == 0) { text.Append("\nNo line has played yet."); return; }
        if (lastInput != null) Breakdown("Last line after player input", lastInput);
        var latest = lines[lines.Count - 1];
        if (latest != lastInput) Breakdown("Latest line", latest);
        text.Append('\n').Append(Pad("Recent lines", 22)).Append(Left("words", 6)).Append("  ").Append(Pad("trigger", 14))
            .Append(Left("sound", 7)).Append(Left("word", 7)).Append(Left("synth", 7)).Append(Left("rhubarb", 9)).Append('\n');
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var t = lines[i];
            text.Append(Pad(Clip(t.id, 22), 22)).Append(Left(t.words.ToString(), 6)).Append("  ").Append(Pad(Clip(t.trigger, 14), 14))
                .Append(Left(Ms(t.firstSoundMs), 7)).Append(Left(Ms(t.firstWordMs), 7)).Append(Left(Ms(t.synthesisMs), 7)).Append(Left(Ms(t.rawRhubarbMs), 9)).Append('\n');
        }
        text.Append("ms; first word = first voiced reply onset; output buffering excluded.");
    }

    private void Breakdown(string title, GatekeeperLineTiming t)
    {
        text.Append('\n').Append(title).Append(": ").Append(t.id).Append(", ").Append(t.words).Append(" words, ")
            .Append(t.audioSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s audio\n")
            .Append("  ").Append(t.voice).Append(t.fallback != "None" ? ", fallback " + t.fallback : "").Append(", after ").Append(t.trigger).Append('\n');
        Row("First sound (reaction)", t.firstSoundMs);
        Row("First word (reply onset)", t.firstWordMs);
        if (t.reaction.Length > 0) text.Append("  Reaction: ").Append(t.reaction).Append('\n');
        Row("Pause before the line", t.pacingMs);
        Row("Choosing the line", t.lineSourceMs);
        Row("Waiting for the voice worker", t.queueMs);
        Row("Voice synthesis", t.synthesisMs, t.rawSynthesisMs);
        if (t.analysisComplete) Row("Rhubarb (parallel)", t.rawRhubarbMs);
        else text.Append("  Rhubarb: running in parallel\n");
        Row("Hand-off to Unity", t.handoffMs);
        Row("Audio clip creation", t.clipMs);
        Row("Playback start", t.playbackStartMs);
        text.Append("  ").Append(Pad(t.fallback == "SubtitleOnly" ? "Total to subtitle" : "Total to reply audio", 30)).Append(Left(Ms(t.totalMs), 7)).Append(" ms\n");
    }
    private void Row(string label, float ms, float raw = -1)
    {
        text.Append("  ").Append(Pad(label, 30)).Append(Left(Ms(ms), 7)).Append(" ms");
        if (raw >= 0 && Mathf.Abs(raw - ms) > 1) text.Append("  (ran ").Append(Ms(raw)).Append(" ms in all)");
        text.Append('\n');
    }
    private static string Ms(float ms) => Mathf.RoundToInt(ms).ToString(CultureInfo.InvariantCulture);
    private static string Pad(string s, int width) => s.Length >= width ? s : s + new string(' ', width - s.Length);
    private static string Left(string s, int width) => s.Length >= width ? s : new string(' ', width - s.Length) + s;
    private static string Clip(string s, int width) => s.Length <= width ? s : s.Substring(0, width - 1) + "~";
}
