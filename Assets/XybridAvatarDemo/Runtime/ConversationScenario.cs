using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Xybrid/Conversation Scenario")]
public sealed class ConversationScenario : ScriptableObject
{
    [Serializable] public sealed class Sentence
    {
        [TextArea(2, 5)] public string text;
        public AudioClip audio;
        public TextAsset mouthCues;
    }

    [Serializable] public sealed class Choice
    {
        public string text;
        public string targetNodeId;
    }

    [Serializable] public sealed class Node
    {
        public string id;
        public List<Sentence> sentences = new List<Sentence>();
        public List<Choice> choices = new List<Choice>();
    }

    public string startNodeId;
    public List<Node> nodes = new List<Node>();

    public Node FindNode(string id) => nodes?.Find(node => node != null && node.id == id);

    // Cycles are intentional: a choice can return to an earlier question.
    public bool Validate(out string error)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (nodes == null || nodes.Count == 0) { error = "The scenario has no nodes."; return false; }
        foreach (var node in nodes)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.id)) { error = "Every node needs an ID."; return false; }
            if (!ids.Add(node.id)) { error = "Duplicate node ID: " + node.id; return false; }
            if (node.sentences == null || node.choices == null) { error = "Missing sentence or choice list: " + node.id; return false; }
            foreach (var sentence in node.sentences)
            {
                if (sentence == null || string.IsNullOrWhiteSpace(sentence.text)) { error = "Empty sentence in node: " + node.id; return false; }
                if (sentence.audio == null && sentence.mouthCues != null) { error = "Mouth cues need matching audio: " + node.id; return false; }
                if (sentence.mouthCues != null && !XybridAvatarPlayer.ValidateCues(sentence.mouthCues, sentence.audio, out error)) { error = node.id + ": " + error; return false; }
            }
        }
        if (string.IsNullOrEmpty(startNodeId) || !ids.Contains(startNodeId)) { error = "The start node does not exist."; return false; }
        foreach (var node in nodes)
        {
            foreach (var choice in node.choices)
            {
                if (choice == null || string.IsNullOrWhiteSpace(choice.text)) { error = "Empty choice in node: " + node.id; return false; }
                if (string.IsNullOrEmpty(choice.targetNodeId) || !ids.Contains(choice.targetNodeId)) { error = "Unknown choice target in " + node.id + ": " + choice.targetNodeId; return false; }
            }
        }
        error = null;
        return true;
    }
}
