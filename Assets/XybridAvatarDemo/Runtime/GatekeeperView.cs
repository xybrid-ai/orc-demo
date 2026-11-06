using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public sealed class GatekeeperView : MonoBehaviour
{
    public GatekeeperController gatekeeper;
    public Text sentence;
    public GameObject inputRow;
    public InputField input;
    public Button send;
    public Button action;
    public Text actionText;
    public RectTransform choiceRow;
    [Tooltip("Suggestion fade, stagger and time the complete row remains visible, in seconds.")]
    public float hintFadeSeconds = .2f, hintStaggerSeconds = .15f, hintHoldSeconds = 2;
    public Button hintsButton;
    [Tooltip("What the player just said (a choice's words, or a typed name or business), above Trola's subtitle. Built at runtime when unset.")]
    public Text playerLine;
    private string shownChoices; // The label set the current buttons were built for; null while the row is hidden.
    private readonly HashSet<string> previewedCheckpoints = new HashSet<string>();
    private readonly List<Button> choiceButtons = new List<Button>();
    private readonly List<CanvasGroup> choiceGroups = new List<CanvasGroup>();
    private readonly List<int> suggestionIndices = new List<int>();
    private string choiceCheckpoint;
    private bool advising, hintsPinned, choiceInputBusy;
    private int previewFrame = -1, markedIndex = -1;
    private float previewAt = -1;
    private bool tabHeld => Keyboard.current != null && Keyboard.current.tabKey.isPressed;
    // Subtitles reveal phrase by phrase as he says them (default) or show whole lines: F4 toggles and remembers it
    // ("phrases" or "whole"; an older "sentences" reads as by phrase), -xybridSubtitles phrases|whole overrides it for a
    // run ("sentences" is an alias). Display only, applied at once; Vesper's line follows it too. True is by phrase
    // (the name predates phrases).
    private const string RevealPreference = "Trola.subtitleReveal";
    private static bool? byPhrase;
    public static bool RevealBySentence
    {
        get
        {
            if (byPhrase == null)
            {
                byPhrase = PlayerPrefs.GetString(RevealPreference, "phrases") != "whole";
                var args = System.Environment.GetCommandLineArgs(); int at = System.Array.IndexOf(args, "-xybridSubtitles");
                string forced = at >= 0 && at + 1 < args.Length ? args[at + 1].ToLowerInvariant() : "";
                if (forced == "phrases" || forced == "sentences" || forced == "whole") byPhrase = forced != "whole";
            }
            return byPhrase.Value;
        }
        set { byPhrase = value; PlayerPrefs.SetString(RevealPreference, value ? "phrases" : "whole"); PlayerPrefs.Save(); }
    }
    // The line the subtitle presents, as plain text, while the subtitle holds what this view wrote; else whatever it holds.
    public string ShownLine => sentence == null ? "" : shownText != null && sentence.text == shownText ? lineText : sentence.text;
    private string lineText, shownText;
    private List<(int start, int length)> linePhrases;
    private int shownVisible = -1;
    private float shownAlpha = -1;

    private void OnEnable()
    {
        if (gatekeeper != null) { gatekeeper.Changed += Refresh; gatekeeper.PlayerActed += ShowPlayerLine; gatekeeper.Restarted += ResetChoices; }
        // A reply and its hand-back wrap to two lines; Truncate would silently drop the second one.
        if (sentence != null) sentence.verticalOverflow = VerticalWrapMode.Overflow;
        if (send != null) send.onClick.AddListener(Submit);
        if (action != null) action.onClick.AddListener(Advance);
        if (hintsButton != null) hintsButton.onClick.AddListener(ToggleHints);
        if (input != null) { input.onEndEdit.AddListener(SubmitOnEnter); input.onValueChanged.AddListener(InputActivity); }
        Refresh();
    }

    private void OnDisable()
    {
        if (gatekeeper != null)
        {
            gatekeeper.Changed -= Refresh; gatekeeper.PlayerActed -= ShowPlayerLine; gatekeeper.Restarted -= ResetChoices;
            gatekeeper.ReleaseIdle(this);
        }
        InterruptChoicePresentation();
        if (send != null) send.onClick.RemoveListener(Submit);
        if (action != null) action.onClick.RemoveListener(Advance);
        if (hintsButton != null) hintsButton.onClick.RemoveListener(ToggleHints);
        if (input != null) { input.onEndEdit.RemoveListener(SubmitOnEnter); input.onValueChanged.RemoveListener(InputActivity); }
    }

    public void Refresh()
    {
        if (gatekeeper == null || sentence == null || inputRow == null) return;
        ShowSubtitle();
        var prompt = gatekeeper.Prompt;
        bool accepting = gatekeeper.AcceptsText;
        inputRow.SetActive(prompt.PersistentText || accepting);
        input.characterLimit = prompt.CharacterLimit;
        input.interactable = prompt.PersistentText || accepting;
        send.interactable = gatekeeper.CanSubmitText;
        RefreshSubmissionStatus();
        bool ended = gatekeeper.State == GatekeeperController.Phase.Ended || gatekeeper.State == GatekeeperController.Phase.VoiceFailed;
        action.gameObject.SetActive(gatekeeper.State == GatekeeperController.Phase.Speaking && prompt.ShowInterruptButton && !gatekeeper.IsInterruptionReaction
            && !gatekeeper.IsSettling || ended);
        actionText.text = ended ? "Try again" : prompt.InterruptLabel;
        // The player's line stays up while Trola answers it and clears when the turn is theirs again.
        if (playerLine != null && (gatekeeper.IsWaitingForPlayer || gatekeeper.IsWaitingForGate || gatekeeper.IsSuspended || ended)) playerLine.text = "";
        RefreshChoices();
        if (accepting) input.ActivateInputField();
    }

    private void RefreshSubmissionStatus()
    {
        if (gatekeeper == null || input == null || send == null) return;
        string elapsed = gatekeeper.PhaseElapsed.ToString("0") + " s";
        if (input.placeholder is Text placeholder)
            placeholder.text = gatekeeper.HasQueuedReply
                ? gatekeeper.State == GatekeeperController.Phase.PreparingSpeech
                    ? "Queued — current voice preparing, " + elapsed : "Queued — waiting for current speech"
                : gatekeeper.State == GatekeeperController.Phase.PreparingLine ? "Thinking… " + elapsed
                : gatekeeper.State == GatekeeperController.Phase.PreparingSpeech ? "Preparing voice… " + elapsed
                : gatekeeper.Prompt.Placeholder;
        var label = send.GetComponentInChildren<Text>(true);
        if (label != null) label.text = gatekeeper.HasQueuedReply ? "Queued"
            : gatekeeper.State == GatekeeperController.Phase.PreparingLine ? "Thinking" : "Send";
    }

    // Speech input (PlayerVoiceInput) writes the words where typing would, so the player can still edit them.
    public string Draft => input != null ? input.text ?? "" : "";
    public void SetDraft(string text)
    {
        if (input == null) return;
        input.text = text ?? "";
        if (!input.isFocused) input.ActivateInputField();
        caretToEnd = true; // Activation selects all; typing would replace the words instead of correcting them.
    }
    private bool caretToEnd;

    public void Submit()
    {
        string text = input.text;
        if (gatekeeper.Submit(text)) input.SetTextWithoutNotify("");
        else if (gatekeeper.State == GatekeeperController.Phase.AwaitingInput || gatekeeper.State == GatekeeperController.Phase.AwaitingName) input.ActivateInputField();
    }

    private void InputActivity(string unused) { gatekeeper.NotifyActivity(); }
    private void Update()
    {
        ShowSubtitle();
        RefreshSubmissionStatus();
        AnimateChoices();
        if (caretToEnd && input != null && input.isFocused)
        {
            caretToEnd = false;
            input.selectionAnchorPosition = input.selectionFocusPosition = input.caretPosition = input.text.Length;
        }
        // A draft is activity even while the player pauses to read or think.
        if (gatekeeper != null && input != null && inputRow.activeInHierarchy && !string.IsNullOrWhiteSpace(input.text))
            gatekeeper.NotifyActivity();
        if (gatekeeper == null || !gatekeeper.Prompt.PointerInterrupts || gatekeeper.State != GatekeeperController.Phase.Speaking || gatekeeper.IsInterruptionReaction) return;
        // The click that selected a choice can still be pressed this frame.
        if (Time.frameCount <= gatekeeper.SpeechStartedFrame) return;
        bool mouse = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
        bool touch = Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame;
        if (mouse || touch) gatekeeper.Interrupt();
    }

    private void SubmitOnEnter(string unused)
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)) Submit();
    }

    private void Advance()
    {
        // Try again replays the road approach when the experience director runs it (docs/approach.md).
        if (gatekeeper.State == GatekeeperController.Phase.Ended || gatekeeper.State == GatekeeperController.Phase.VoiceFailed)
        {
            var director = ExperienceDirector.Instance;
            if (director == null || !director.Replay()) gatekeeper.Restart();
        }
        // The button appears where the choices were: the second click of a double-click is not an interruption.
        else if (gatekeeper.PhaseElapsed >= .5f) gatekeeper.Skip();
    }

    private void RefreshChoices()
    {
        if (!Application.isPlaying) return;
        if (choiceRow == null)
        {
            var row = new GameObject("Gatekeeper choices", typeof(RectTransform));
            row.layer = gameObject.layer;
            choiceRow = (RectTransform)row.transform;
            choiceRow.SetParent(transform, false);
            choiceRow.anchorMin = new Vector2(.10f, .035f); choiceRow.anchorMax = new Vector2(.90f, .035f);
            choiceRow.pivot = new Vector2(.5f, 0); choiceRow.sizeDelta = new Vector2(0, 52);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10; layout.childControlHeight = layout.childControlWidth = true;
            layout.childForceExpandHeight = layout.childForceExpandWidth = true;
        }
        bool choosing = gatekeeper.AcceptsChoice;
        choiceRow.gameObject.SetActive(choosing);
        EnsureHintsButton();
        if (hintsButton != null) hintsButton.gameObject.SetActive(choosing);
        var choices = gatekeeper.CurrentChoices;
        string shown = null;
        if (choosing) { shown = ""; foreach (var choice in choices) shown += choice.label + "\n"; }
        // An idle taunt keeps the same choices live: the buttons under the pointer survive its start and end,
        // so a press that began before the taunt still clicks when released.
        if (shown == shownChoices) return;
        ClearSpokenChoice();
        shownChoices = shown;
        foreach (Transform child in choiceRow) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        choiceButtons.Clear(); choiceGroups.Clear(); suggestionIndices.Clear();
        if (!choosing)
        {
            InterruptChoicePresentation();
            return;
        }
        string checkpoint = gatekeeper.Turns + "\n" + shown;
        if (choiceCheckpoint != checkpoint)
        {
            choiceCheckpoint = checkpoint;
            InterruptChoicePresentation();
        }
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var button = MakeChoice(choices[i].label);
            button.onClick.AddListener(() => SelectHint(index));
            choiceButtons.Add(button);
            var visibility = button.GetComponent<CanvasGroup>();
            if (visibility == null) visibility = button.gameObject.AddComponent<CanvasGroup>();
            visibility.alpha = 0; visibility.interactable = visibility.blocksRaycasts = false;
            choiceGroups.Add(visibility);
            bool aggressive = choices[i].label == "Threaten" || choices[i].label == "Demand the gate";
            var layout = button.GetComponent<LayoutElement>();
            if (layout == null) layout = button.gameObject.AddComponent<LayoutElement>();
            // Aggressive actions stay available to speech. They only appear when acknowledged, in their story slot.
            layout.ignoreLayout = aggressive;
            if (!aggressive) suggestionIndices.Add(i);
        }
        // Give Vesper time to claim the checkpoint before starting the preview. Without her, hints still preview.
        if (previewedCheckpoints.Add(checkpoint)) previewFrame = Time.frameCount + 2;
    }

    public void BeginChoiceAdvice()
    {
        if (gatekeeper == null || !gatekeeper.AcceptsChoice) return;
        // The UI may have subscribed after the spirit (for example after re-enabling the view).
        // Initialize this checkpoint before claiming it so Refresh cannot erase the advice hold.
        RefreshChoices();
        advising = true; previewFrame = -1; previewAt = -1;
        gatekeeper.HoldIdle(this);
    }
    public void EndChoiceAdvice()
    {
        if (!advising) return;
        advising = false;
        if (!choiceInputBusy && gatekeeper != null && gatekeeper.AcceptsChoice) previewAt = Time.unscaledTime;
    }
    public void InterruptChoicePresentation()
    {
        advising = false; hintsPinned = false; previewAt = -1; previewFrame = -1;
        if (gatekeeper != null) gatekeeper.ReleaseIdle(this);
        foreach (var group in choiceGroups)
            if (group != null) { group.alpha = 0; group.interactable = group.blocksRaycasts = false; }
    }
    public void SetChoiceInputBusy(bool busy)
    {
        choiceInputBusy = busy;
        if (busy) InterruptChoicePresentation();
    }
    private void ResetChoices()
    {
        ClearPlayerLine(); ClearSpokenChoice(); InterruptChoicePresentation();
        if (input != null) input.SetTextWithoutNotify(""); // A draft from the last encounter must not reach the new name prompt.
        choiceInputBusy = false; choiceCheckpoint = null; previewedCheckpoints.Clear();
    }
    private void SelectHint(int index)
    {
        // A second click during the spoken acknowledgement must not execute another action.
        if (markedIndex >= 0 || gatekeeper == null || !gatekeeper.AcceptsChoice) return;
        SpiritDirector.Instance?.InterruptAdvice();
        InterruptChoicePresentation();
        gatekeeper.Choose(index);
    }
    private void ToggleHints()
    {
        if (markedIndex >= 0 || choiceInputBusy || gatekeeper == null || !gatekeeper.AcceptsChoice) return;
        hintsPinned = !hintsPinned;
        if (hintsPinned) { previewAt = -1; previewFrame = -1; }
    }
    private void EnsureHintsButton()
    {
        if (hintsButton != null || send == null) return;
        hintsButton = Instantiate(send, choiceRow.parent);
        hintsButton.name = "Hints";
        hintsButton.onClick.RemoveAllListeners(); hintsButton.onClick.AddListener(ToggleHints);
        var rect = hintsButton.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(.94f, .035f); rect.pivot = new Vector2(.5f, 0);
        rect.anchoredPosition = Vector2.zero; rect.sizeDelta = new Vector2(88, 36);
        var text = hintsButton.GetComponentInChildren<Text>(true);
        if (text != null) { text.text = "Hints"; text.font = sentence.font; text.fontSize = 18; text.supportRichText = false; }
    }
    private void AnimateChoices()
    {
        if (gatekeeper == null) return;
        bool choosing = gatekeeper.AcceptsChoice && isActiveAndEnabled;
        if (!choosing)
        {
            gatekeeper.ReleaseIdle(this);
            return;
        }
        float now = Time.unscaledTime;
        if (previewFrame >= 0 && Time.frameCount >= previewFrame && !advising && !choiceInputBusy)
        { previewFrame = -1; previewAt = now; }
        float fade = Mathf.Max(.01f, hintFadeSeconds), stagger = Mathf.Max(0, hintStaggerSeconds);
        float completeAt = Mathf.Max(0, suggestionIndices.Count - 1) * stagger + fade;
        float endAt = completeAt + Mathf.Max(0, hintHoldSeconds) + fade;
        bool preview = previewAt >= 0 && now - previewAt < endAt;
        if (!preview) previewAt = -1;
        bool recalling = !choiceInputBusy && (hintsPinned || tabHeld);
        if (advising || previewFrame >= 0 || preview || recalling || choiceInputBusy || markedIndex >= 0) gatekeeper.HoldIdle(this);
        else gatekeeper.ReleaseIdle(this);
        if (hintsButton != null)
        {
            hintsButton.interactable = !choiceInputBusy && markedIndex < 0;
            var text = hintsButton.GetComponentInChildren<Text>(true);
            if (text != null) text.text = hintsPinned ? "Hide hints" : "Hints";
        }
        for (int i = 0; i < choiceGroups.Count; i++)
        {
            var group = choiceGroups[i];
            if (group == null) continue;
            int order = suggestionIndices.IndexOf(i);
            float target = 0;
            if (markedIndex >= 0) target = markedIndex == i ? 1 : 0;
            else if (order >= 0 && !choiceInputBusy)
            {
                if (recalling) target = 1;
                else if (preview)
                {
                    float elapsed = now - previewAt;
                    target = Mathf.Min(Mathf.Clamp01((elapsed - order * stagger) / fade), Mathf.Clamp01((endAt - elapsed) / fade));
                }
            }
            // Preview already contains its fade envelope; recall fades at the same rate.
            group.alpha = preview || markedIndex >= 0 || choiceInputBusy ? target : Mathf.MoveTowards(group.alpha, target, Time.unscaledDeltaTime / fade);
            group.interactable = group.blocksRaycasts = target > 0 && group.alpha > .01f && !choiceInputBusy && markedIndex < 0;
            if (i == markedIndex && order < 0)
            {
                var rect = choiceButtons[i].GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2((i + .5f) / choiceGroups.Count, .5f);
                rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2((choiceRow.rect.width - 10 * (choiceGroups.Count - 1)) / choiceGroups.Count, 52);
            }
        }
    }
    // The current line whole, or its phrases the voice has reached with the rest laid out but transparent
    // (GatekeeperSubtitleTiming.Markup), so nothing reflows as they appear. Rich text is on only while markup shows:
    // player names and businesses are inserted into lines, and a line with '<' or '>' always shows whole and literal.
    private void ShowSubtitle()
    {
        if (gatekeeper == null || sentence == null) return;
        string text = gatekeeper.CurrentText ?? "";
        int visible = gatekeeper.RevealedPhrases(out float alpha);
        if (!RevealBySentence) { visible = int.MaxValue; alpha = 1; }
        if (text != lineText) { lineText = text; linePhrases = GatekeeperSubtitleTiming.Phrases(text); shownVisible = -1; }
        if (visible == shownVisible && alpha == shownAlpha && sentence.text == shownText) return;
        shownVisible = visible; shownAlpha = alpha;
        string shown = visible >= linePhrases.Count && alpha >= 1 ? text
            : GatekeeperSubtitleTiming.Markup(text, linePhrases, visible, alpha, sentence.color, out _);
        bool markup = shown != text;
        if (sentence.supportRichText != markup) sentence.supportRichText = markup;
        if (sentence.text != shown) sentence.text = shown;
        shownText = shown;
    }
    // A restart or debug jump starts a new conversation: nothing the player said belongs to it.
    private void ClearPlayerLine() { if (playerLine != null) playerLine.text = ""; }
    // Raised before the controller acts, so CurrentChoices and Prompt still describe the checkpoint the player answered.
    private void ShowPlayerLine(GatekeeperInputKind kind, string label)
    {
        if (!Application.isPlaying || sentence == null || gatekeeper == null) return;
        string said = null;
        if (kind == GatekeeperInputKind.Choice)
        {
            // A button picked by voice shows the player's own words; a click shows the choice's.
            said = Safe(gatekeeper.SpokenWords);
            if (said == null) foreach (var choice in gatekeeper.CurrentChoices) if (choice != null && choice.label == label) said = choice.say;
        }
        else if (kind == GatekeeperInputKind.Text) said = Echo(label);
        else if (kind == GatekeeperInputKind.OffTopic || kind == GatekeeperInputKind.OutOfWorld || kind == GatekeeperInputKind.Unclear || kind == GatekeeperInputKind.Neutral) said = Safe(label);
        if (string.IsNullOrWhiteSpace(said)) return;
        PlayerLine().text = "You: \"" + said.Trim() + "\"";
    }
    // The player's own words ("I go by the name Malik"); Trola's reply shows the name he read out of them.
    // A blocked or empty answer shows nothing. Free-form replies are not echoed; the reply field already shows them.
    private string Echo(string typed)
    {
        var slot = gatekeeper.Prompt.InputSlot;
        if (slot != GatekeeperInputSlot.Name && slot != GatekeeperInputSlot.Business) return null;
        return Safe(typed);
    }
    private string Safe(string words)
    {
        if (string.IsNullOrWhiteSpace(words)) return null;
        var blocklist = gatekeeper.scenario != null ? gatekeeper.scenario.nameBlocklist : null;
        return GatekeeperNames.TryReadSaid(words, blocklist, out var said) ? said : null;
    }

    // A button picked by voice lights up for a moment before PlayerVoiceInput clicks it. Found by name, like Vesper's marks
    // and the self-test; its name, listeners and interactable state never change. The buttons are rebuilt whenever the
    // choices change, which also drops the light.
    private static readonly Color SpokenTint = new Color(1f, .8f, .42f);
    private Graphic spokenGraphic;
    private Transform spokenButton;
    private Color spokenColor;
    private Vector3 spokenScale;
    public void MarkSpokenChoice(string label)
    {
        ClearSpokenChoice();
        if (choiceRow == null) return;
        for (int index = 0; index < choiceButtons.Count; index++)
        {
            var child = choiceButtons[index].transform;
            if (!child.gameObject.activeInHierarchy || child.name != "Choice " + label) continue;
            var button = choiceButtons[index];
            if (button == null || button.targetGraphic == null) return;
            spokenGraphic = button.targetGraphic; spokenButton = child; spokenColor = spokenGraphic.color; spokenScale = child.localScale;
            markedIndex = index;
            spokenGraphic.color = SpokenTint; child.localScale = spokenScale * 1.06f;
            AnimateChoices();
            return;
        }
    }
    public void ClearSpokenChoice()
    {
        if (spokenGraphic != null) spokenGraphic.color = spokenColor;
        if (spokenButton != null) spokenButton.localScale = spokenScale;
        spokenGraphic = null; spokenButton = null;
        markedIndex = -1;
    }
    // A sibling of the subtitle, not part of it: the self-test requires ShownLine == line.text while he speaks.
    private Text PlayerLine()
    {
        if (playerLine != null) return playerLine;
        var line = new GameObject("Player line", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Shadow));
        line.layer = sentence.gameObject.layer;
        var rect = (RectTransform)line.transform;
        rect.SetParent(sentence.transform.parent, false);
        rect.anchorMin = new Vector2(.12f, .225f); rect.anchorMax = new Vector2(.88f, .225f);
        rect.pivot = new Vector2(.5f, 0); rect.sizeDelta = new Vector2(0, 36);
        playerLine = line.GetComponent<Text>();
        playerLine.font = sentence.font; playerLine.fontSize = 24; // Upright and warm grey: italic pale blue is Vesper's voice.
        playerLine.alignment = TextAnchor.LowerCenter; playerLine.horizontalOverflow = HorizontalWrapMode.Wrap;
        playerLine.verticalOverflow = VerticalWrapMode.Overflow; playerLine.supportRichText = false; playerLine.raycastTarget = false;
        playerLine.color = new Color(.80f, .76f, .68f, .9f);
        line.GetComponent<Shadow>().effectColor = new Color(0, 0, 0, .9f);
        return playerLine;
    }
    private Button MakeChoice(string label)
    {
        var button = Instantiate(send, choiceRow);
        button.name = "Choice " + label;
        button.onClick.RemoveAllListeners();
        var size = button.GetComponent<LayoutElement>();
        if (size != null) { size.preferredWidth = -1; size.flexibleWidth = 1; }
        var text = button.GetComponentInChildren<Text>(true);
        if (text != null) { text.text = label == "Ask about the camp" ? "Talk about camp" : label; text.font = sentence.font; text.fontSize = 23; text.supportRichText = false; }
        button.interactable = true; button.gameObject.SetActive(true);
        return button;
    }
}
