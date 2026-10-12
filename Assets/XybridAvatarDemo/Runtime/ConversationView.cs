using UnityEngine;
using UnityEngine.UI;

public sealed class ConversationView : MonoBehaviour
{
    public ConversationController conversation;
    public Text sentence;
    public RectTransform choices;
    public Button choiceTemplate;
    public Button action;
    public Text actionText;

    private void OnEnable()
    {
        if (conversation != null) conversation.Changed += Refresh;
        if (action != null) action.onClick.AddListener(Advance);
        Refresh();
    }

    private void OnDisable()
    {
        if (conversation != null) conversation.Changed -= Refresh;
        if (action != null) action.onClick.RemoveListener(Advance);
    }

    public void Refresh()
    {
        if (conversation == null || sentence == null || choices == null || choiceTemplate == null || action == null) return;
        sentence.text = conversation.CurrentText;
        // Deactivate immediately so a rapid click cannot select a stale branch.
        foreach (Transform child in choices)
        {
            if (child.gameObject == choiceTemplate.gameObject) continue;
            child.gameObject.SetActive(false); Destroy(child.gameObject);
        }
        bool showChoices = conversation.State == ConversationController.Phase.AwaitingChoice;
        if (showChoices)
        {
            var options = conversation.CurrentNode.choices;
            for (int i = 0; i < options.Count; i++)
            {
                int index = i;
                var button = Instantiate(choiceTemplate, choices);
                button.name = "Choice " + (index + 1);
                button.GetComponentInChildren<Text>(true).text = options[index].text;
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => conversation.Choose(index));
                button.gameObject.SetActive(true);
            }
        }
        var state = conversation.State;
        action.gameObject.SetActive(state == ConversationController.Phase.Speaking || state == ConversationController.Phase.AwaitingContinue || state == ConversationController.Phase.Ended);
        if (actionText != null) actionText.text = state == ConversationController.Phase.Ended ? "Start again" : state == ConversationController.Phase.Speaking ? "Skip" : "Continue";
    }

    private void Advance()
    {
        if (conversation.State == ConversationController.Phase.Ended) conversation.Restart();
        else conversation.Continue();
    }
}
