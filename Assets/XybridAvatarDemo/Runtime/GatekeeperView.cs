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

    private void OnEnable()
    {
        if (gatekeeper != null) gatekeeper.Changed += Refresh;
        if (send != null) send.onClick.AddListener(Submit);
        if (action != null) action.onClick.AddListener(Advance);
        if (input != null) { input.onEndEdit.AddListener(SubmitOnEnter); input.onValueChanged.AddListener(InputActivity); }
        Refresh();
    }

    private void OnDisable()
    {
        if (gatekeeper != null) gatekeeper.Changed -= Refresh;
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
        else gatekeeper.Skip();
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
        foreach (Transform child in choiceRow) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        bool choosing = gatekeeper.State == GatekeeperController.Phase.AwaitingInput && gatekeeper.CurrentChoices.Count > 0;
        choiceRow.gameObject.SetActive(choosing);
        if (!choosing) return;
        var choices = gatekeeper.CurrentChoices;
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var button = MakeChoice(choices[i].label);
            button.onClick.AddListener(() => gatekeeper.Choose(index));
        }
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
