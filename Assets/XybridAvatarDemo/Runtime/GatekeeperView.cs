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
    [Tooltip("What the player just said (a choice's words, or a typed name or business), above Trola's subtitle. Built at runtime when unset.")]
    public Text playerLine;
    private string shownChoices; // The label set the current buttons were built for; null while the row is hidden.

    private void OnEnable()
    {
        if (gatekeeper != null) { gatekeeper.Changed += Refresh; gatekeeper.PlayerActed += ShowPlayerLine; gatekeeper.Restarted += ClearPlayerLine; }
        // A reply and its hand-back wrap to two lines; Truncate would silently drop the second one.
        if (sentence != null) sentence.verticalOverflow = VerticalWrapMode.Overflow;
        if (send != null) send.onClick.AddListener(Submit);
        if (action != null) action.onClick.AddListener(Advance);
        if (input != null) { input.onEndEdit.AddListener(SubmitOnEnter); input.onValueChanged.AddListener(InputActivity); }
        Refresh();
    }

    private void OnDisable()
    {
        if (gatekeeper != null) { gatekeeper.Changed -= Refresh; gatekeeper.PlayerActed -= ShowPlayerLine; gatekeeper.Restarted -= ClearPlayerLine; }
        if (send != null) send.onClick.RemoveListener(Submit);
        if (action != null) action.onClick.RemoveListener(Advance);
        if (input != null) { input.onEndEdit.RemoveListener(SubmitOnEnter); input.onValueChanged.RemoveListener(InputActivity); }
    }

    public void Refresh()
    {
        if (gatekeeper == null || sentence == null || inputRow == null) return;
        sentence.text = gatekeeper.CurrentText;
        var prompt = gatekeeper.Prompt;
        bool accepting = gatekeeper.AcceptsText;
        inputRow.SetActive(prompt.PersistentText || accepting);
        input.characterLimit = prompt.CharacterLimit;
        input.interactable = prompt.PersistentText || accepting;
        send.interactable = gatekeeper.CanSubmitText;
        RefreshSubmissionStatus();
        bool ended = gatekeeper.State == GatekeeperController.Phase.Ended || gatekeeper.State == GatekeeperController.Phase.VoiceFailed;
        action.gameObject.SetActive(gatekeeper.State == GatekeeperController.Phase.Speaking && prompt.ShowInterruptButton && !gatekeeper.IsInterruptionReaction || ended);
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

    public void Submit()
    {
        string text = input.text;
        if (gatekeeper.Submit(text)) input.SetTextWithoutNotify("");
        else if (gatekeeper.State == GatekeeperController.Phase.AwaitingInput || gatekeeper.State == GatekeeperController.Phase.AwaitingName) input.ActivateInputField();
    }

    private void InputActivity(string unused) { gatekeeper.NotifyActivity(); }
    private void Update()
    {
        RefreshSubmissionStatus();
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
        if (gatekeeper.State == GatekeeperController.Phase.Ended || gatekeeper.State == GatekeeperController.Phase.VoiceFailed) gatekeeper.Restart();
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
        var choices = gatekeeper.CurrentChoices;
        string shown = null;
        if (choosing) { shown = ""; foreach (var choice in choices) shown += choice.label + "\n"; }
        // An idle taunt keeps the same choices live: the buttons under the pointer survive its start and end,
        // so a press that began before the taunt still clicks when released.
        if (shown == shownChoices) return;
        shownChoices = shown;
        foreach (Transform child in choiceRow) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        if (!choosing) return;
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var button = MakeChoice(choices[i].label);
            button.onClick.AddListener(() => gatekeeper.Choose(index));
        }
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
            foreach (var choice in gatekeeper.CurrentChoices) if (choice != null && choice.label == label) said = choice.say;
        }
        else if (kind == GatekeeperInputKind.Text) said = Echo(label);
        if (string.IsNullOrWhiteSpace(said)) return;
        PlayerLine().text = "You: \"" + said.Trim() + "\"";
    }
    // Only what the game itself would repeat: a blocked or empty name or business shows nothing (GatekeeperNames.cs:5).
    // Free-form replies are not echoed; the reply field already shows them.
    private string Echo(string typed)
    {
        var blocklist = gatekeeper.scenario != null ? gatekeeper.scenario.nameBlocklist : null;
        switch (gatekeeper.Prompt.InputSlot)
        {
            case GatekeeperInputSlot.Name: return GatekeeperNames.TrySanitize(typed, blocklist, out var name) ? name : null;
            case GatekeeperInputSlot.Business: return GatekeeperNames.TrySanitize(typed, blocklist, GatekeeperNames.BusinessMaxLength, out var business) ? business : null;
            default: return null;
        }
    }
    // A sibling of the subtitle, not part of it: the self-test requires sentence.text == line.text while he speaks.
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
        if (text != null) { text.text = label; text.font = sentence.font; text.fontSize = 23; text.supportRichText = false; }
        button.interactable = true; button.gameObject.SetActive(true);
        return button;
    }
}
