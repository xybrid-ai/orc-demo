// Shared spirit contracts: pure C# 9, compiled by Unity and tools/npc-quality. See docs/spirit.md.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public enum SpiritStage { None, Name, Business, Hub, Reckoning, FreeTalk, Ended }
public enum SpiritBand { Done, Furious, Annoyed, Wary, Listening, Warm }
public enum SpiritMomentKind { Whisper, Counsel }
public enum SpiritTrigger { Intro, NameAssigned, HubFirst, HubSecond, Reckoning, AfterTaunt, Ending, FreeTalk, Summon }
public enum SpiritVerdict { Best, Good, Costly, Fatal, Hopeless, Unavailable }
public enum SpiritScope { Unclear, InWorld, OutOfWorld, RoleOverride }
public enum SpiritIntent { Other, AskAdvice, Doubt, AskAboutTrola, Agree, Dismiss, StatePlan, AskAboutSpirit, AskSpiritToAct }
public enum SpiritPlan { None, Flatter, Bribe, Threaten, AskCamp, PayToll, ShareJoke, DemandGate }
public enum SpiritDecisionSource { D1, Keywords }
public enum SpiritWindowState { Closed, Speaking, Listening, Thinking, Answering, Closing }
public enum SpiritWindowEvent { None, DraftTooLong, Close }
public enum SpiritCloseReason { None, Finished, Budget, Replies, Silence, Agreed, Dismissed, PlayerActed, TrolaActive, Escape, Restart, Disabled, Timeout }
public enum SpiritSound { BreathIn, Exhale, Gasp, Shimmer, Clank, Hammer, Thud, Clatter, GateCreak, DrumHit }

public sealed class SpiritDecision
{
    public SpiritScope Scope = SpiritScope.Unclear; public SpiritIntent Intent = SpiritIntent.Other; public SpiritPlan Plan = SpiritPlan.None;
    public bool Reckless, ToTrola;
    public float ScopeP, IntentP, PlanP, RecklessP, ToTrolaP;
    public SpiritDecisionSource Source; public double Ms; public string Detail = "";
}
public sealed class SpiritOption
{
    public string Label = "", Target = ""; public int Delta, PatienceAfter;
    public GatekeeperOutcome BestOutcome; public SpiritVerdict Verdict; public bool Aggressive, Repeat;
}
public sealed class SpiritAdvice
{
    public string NodeId = ""; public int Patience;
    public IReadOnlyList<SpiritOption> Options = Array.Empty<SpiritOption>();
    public SpiritOption Best, Worst; public bool Hopeless, AlmostThere; public string Trace = "";
}
public sealed class SpiritCheckpoint
{
    public SpiritStage Stage; public string NodeId = ""; public int Patience, LastDelta, HubRoundsDone, Visit;
    public IReadOnlyList<string> Choices = Array.Empty<string>(), UsedTargets = Array.Empty<string>();
    public bool NameAssigned, Interrupted, Taunted, FirstSession;
    public GatekeeperOutcome Outcome; public string PlayerName = "", LastChoiceLabel = "", TrolaLastLineId = "";
    public string LastApproach = "";
    public bool PreviousApproachRepeated;
    public int TrolaLastDelta; public double Now;
    public string VisitKey => Stage + ":" + HubRoundsDone + ":" + Visit;
    // Identity of a real choice checkpoint, unchanged by neutral remarks, clarification or idle taunts.
    public string AdviceKey => Stage + ":" + HubRoundsDone + ":" + string.Join(",", UsedTargets);
}
public sealed class SpiritCue
{
    public string LineId = "", Family = "", Key = "", Text = "", Rule = ""; public bool CloseAfter, Alarm;
    public string WarnsAgainst = "";   // option target this line warns against; counts once the line starts (NoteSpoken)
}
public sealed class SpiritMomentPlan
{
    public SpiritTrigger Trigger; public SpiritMomentKind Kind;
    public IReadOnlyList<SpiritCue> Opening = Array.Empty<SpiritCue>();
    public int MaxReplies; public float ListenSeconds; public SpiritAdvice Advice;
}
[Serializable] public sealed class SpiritTurn { public string speaker, text; }
[Serializable] public sealed class SpiritRequest
{
    public string message, name, business, stage, mood, spirit_said;
    public string[] options; public SpiritTurn[] history;
}
public interface ISpiritDecider { Task<SpiritDecision> DecideAsync(SpiritRequest request, CancellationToken cancellation); }
public static class SpiritSignals
{
    public static volatile bool Enabled = true;      // F7
    public static float MomentWeight;                // 0..1, written by SpiritPresence
    public static bool SpiritAudible;                // written by SpiritVoice
    public static event Action<bool> Flinched;       // true = aggressive choice, false = interruption
    public static void Flinch(bool aggressive) { Flinched?.Invoke(aggressive); }
    public static Func<string> Describer;            // set by SpiritDirector
    public static string Describe() { try { return Describer?.Invoke() ?? "Spirit: not installed"; } catch (Exception e) { return "Spirit: " + e.Message; } }
    public static void Reset() { Enabled = true; MomentWeight = 0; SpiritAudible = false; Flinched = null; Describer = null; }
}
