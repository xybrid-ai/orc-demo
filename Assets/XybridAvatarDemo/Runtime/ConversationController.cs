using System;
using System.Collections;
using UnityEngine;

public sealed class ConversationController : MonoBehaviour
{
    public enum Phase { Inactive, Speaking, AwaitingContinue, AwaitingChoice, Ended }
    public ConversationScenario scenario;
    public XybridAvatarPlayer character;
    public bool autoStart = true;
    public float leadIn = .5f;
    public event Action Changed;
    public Phase State { get; private set; }
    public ConversationScenario.Node CurrentNode { get; private set; }
    public int SentenceIndex { get; private set; } = -1;
    public string CurrentText { get; private set; } = "";
    private Coroutine sequence;
    private bool advanceRequested;
    private int revision;

    private IEnumerator Start()
    {
        if (autoStart) { yield return new WaitForSecondsRealtime(leadIn); Restart(); }
    }

    public bool Restart()
    {
        CancelSequence();
        if (character == null || scenario == null)
        {
            Debug.LogError("[Conversation] Assign a character and scenario.", this);
            SetState(Phase.Inactive); return false;
        }
        if (!scenario.Validate(out var error))
        {
            Debug.LogError("[Conversation] " + error, scenario);
            SetState(Phase.Inactive); return false;
        }
        Visit(scenario.startNodeId);
        return true;
    }

    public bool Choose(int index)
    {
        if (State != Phase.AwaitingChoice || CurrentNode == null || index < 0 || index >= CurrentNode.choices.Count) return false;
        string target = CurrentNode.choices[index].targetNodeId;
        Visit(target);
        return true;
    }

    // For voiced lines this skips the current sentence; unvoiced drafts wait here.
    public void Continue()
    {
        if (State != Phase.Speaking && State != Phase.AwaitingContinue) return;
        advanceRequested = true;
        if (State == Phase.Speaking) character.StopSpeech();
    }

    private void Visit(string nodeId)
    {
        CancelSequence();
        CurrentNode = scenario.FindNode(nodeId);
        if (CurrentNode == null) { Debug.LogError("[Conversation] Unknown node: " + nodeId, this); SetState(Phase.Inactive); return; }
        SetState(Phase.Inactive);
        int version = revision;
        sequence = StartCoroutine(Speak(CurrentNode, version));
    }

    private IEnumerator Speak(ConversationScenario.Node node, int version)
    {
        // Yield once so sequence assignment finishes even for empty/instant nodes.
        yield return null;
        for (int i = 0; i < node.sentences.Count && version == revision; i++)
        {
            var sentence = node.sentences[i];
            SentenceIndex = i;
            CurrentText = sentence.text;
            advanceRequested = false;
            if (sentence.audio != null)
            {
                character.PlayLine(sentence.audio, sentence.mouthCues);
                SetState(Phase.Speaking);
                while (!advanceRequested && character.IsSpeaking && version == revision) yield return null;
            }
            else
            {
                character.StopSpeech();
                SetState(Phase.AwaitingContinue);
                while (!advanceRequested && version == revision) yield return null;
            }
        }
        if (version != revision) yield break;
        character.StopSpeech();
        SentenceIndex = -1;
        SetState(node.choices.Count == 0 ? Phase.Ended : Phase.AwaitingChoice);
        sequence = null;
    }

    private void CancelSequence()
    {
        revision++;
        if (sequence != null) { StopCoroutine(sequence); sequence = null; }
        if (character != null) character.StopSpeech();
        advanceRequested = false;
        CurrentText = "";
        SentenceIndex = -1;
        CurrentNode = null;
    }

    private void SetState(Phase phase) { State = phase; Changed?.Invoke(); }
    private void OnDisable() { CancelSequence(); State = Phase.Inactive; }
}
