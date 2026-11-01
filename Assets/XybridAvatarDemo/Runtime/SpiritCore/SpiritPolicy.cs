using System;
using System.Collections.Generic;

// Frequency limits for one encounter: one moment per checkpoint visit, counsel/appearance caps, whisper cooldowns.
public sealed class SpiritScheduler
{
    private readonly SpiritLines.Timing timing;
    private readonly HashSet<string> visits = new HashSet<string>();
    private double lastWhisper = double.NegativeInfinity, lastFree = double.NegativeInfinity;
    private SpiritTrigger openTrigger; private SpiritMomentKind openKind; private bool open;
    public int Counsels { get; private set; }
    public int Appearances { get; private set; }
    public int FreeWhispers { get; private set; }

    public SpiritScheduler(SpiritLines.Timing timing) { this.timing = timing ?? new SpiritLines.Timing(); }

    public void Reset()
    {
        visits.Clear(); lastWhisper = lastFree = double.NegativeInfinity; open = false;
        Counsels = Appearances = FreeWhispers = 0;
    }

    public bool CanBegin(SpiritTrigger trigger, SpiritMomentKind kind, string visitKey, double now)
    {
        if (Appearances >= timing.appearances_per_encounter) return false;
        if (kind == SpiritMomentKind.Counsel && Counsels >= timing.counsels_per_encounter) return false;
        // Tab is the player's own request: only the caps apply.
        if (trigger == SpiritTrigger.Summon) return kind == SpiritMomentKind.Counsel;
        if (visits.Contains(visitKey ?? "")) return false;
        // Counsels and endings are exempt from the whisper cooldown.
        if (kind == SpiritMomentKind.Whisper && trigger != SpiritTrigger.Ending && now - lastWhisper < timing.whisper_cooldown) return false;
        if (trigger == SpiritTrigger.FreeTalk && (FreeWhispers >= timing.free_whispers || now - lastFree < timing.free_cooldown)) return false;
        return true;
    }

    public void Began(SpiritTrigger trigger, SpiritMomentKind kind, string visitKey, double now)
    {
        if (trigger != SpiritTrigger.Summon) visits.Add(visitKey ?? "");
        Appearances++;
        if (kind == SpiritMomentKind.Counsel) Counsels++;
        if (kind == SpiritMomentKind.Whisper) lastWhisper = now;
        if (trigger == SpiritTrigger.FreeTalk) { FreeWhispers++; lastFree = now; }
        open = true; openTrigger = trigger; openKind = kind;
    }

    // Cooldowns run from the end of the last whisper.
    public void Ended(double now)
    {
        if (!open) return;
        open = false;
        if (openKind == SpiritMomentKind.Whisper) lastWhisper = Math.Max(lastWhisper, now);
        if (openTrigger == SpiritTrigger.FreeTalk) lastFree = Math.Max(lastFree, now);
    }
}

// Decision + state -> authored cue. Rules only; no line is ever generated: moments in AtCheckpoint, answers in Rule().
public sealed class SpiritPolicy
{
    private struct Pick
    {
        public string Family, Key, Warns; public bool Alarm;
        public Pick(string family, string key = "", bool alarm = false, string warns = "") { Family = family; Key = key ?? ""; Alarm = alarm; Warns = warns ?? ""; }
    }
    private sealed class Draft
    {
        public readonly HashSet<string> Used; public readonly Dictionary<string, int> LastUse; public int SinceName, Clock;
        public Draft(SpiritPolicy p) { Used = new HashSet<string>(p.used); LastUse = new Dictionary<string, int>(p.lastUse); SinceName = p.sinceName; Clock = p.clock; }
    }
    private static readonly SpiritPlan[] Plans = { SpiritPlan.Flatter, SpiritPlan.Bribe, SpiritPlan.Threaten, SpiritPlan.AskCamp, SpiritPlan.PayToll, SpiritPlan.ShareJoke, SpiritPlan.DemandGate };
    private const float PlanConfidence = .6f;

    private readonly SpiritLines lines;
    private readonly SpiritAdvisor advisor;
    private readonly int seed;
    public SpiritScheduler Scheduler { get; }
    public int RuleFirings { get; private set; }

    // Encounter memory (reset by ResetEncounter). teachDone lives for the app run.
    private readonly HashSet<string> used = new HashSet<string>();
    private readonly Dictionary<string, int> lastUse = new Dictionary<string, int>();
    private readonly HashSet<string> warnedAgainst = new HashSet<string>();   // targets of warnings she started saying
    private int sinceName = int.MaxValue / 2, clock, encounters;
    private bool firstSession = true, teachDone, interrupted, tauntDone, freeTalk, passedSaid, reckless, warned;
    private SpiritOption lastChoice, finalChoice;
    private int trolaLines, trolaDelta; private string trolaConsumed = "";

    public SpiritPolicy(SpiritLines lines, SpiritAdvisor advisor, int seed)
    {
        this.lines = lines ?? throw new ArgumentNullException(nameof(lines));
        this.advisor = advisor ?? throw new ArgumentNullException(nameof(advisor));
        this.seed = seed;
        Scheduler = new SpiritScheduler(lines.timing);
    }

    public void ResetEncounter(bool firstSession)
    {
        this.firstSession = firstSession; encounters++;
        Scheduler.Reset(); used.Clear(); lastUse.Clear(); warnedAgainst.Clear();
        sinceName = int.MaxValue / 2; interrupted = tauntDone = freeTalk = passedSaid = reckless = warned = false;
        lastChoice = finalChoice = null; trolaLines = trolaDelta = 0; trolaConsumed = "";
    }

    // null = she stays silent. commit=false predicts the same plan without touching any state (prefetch).
    public SpiritMomentPlan AtCheckpoint(SpiritCheckpoint c, bool commit)
    {
        if (c == null) return null;
        if (c.Stage == SpiritStage.FreeTalk && commit) freeTalk = true;
        var advice = c.Stage == SpiritStage.Hub || c.Stage == SpiritStage.Reckoning ? AdviceFor(c) : null;
        var picks = new List<Pick>();
        var trigger = SpiritTrigger.Intro; var kind = SpiritMomentKind.Whisper;
        string visitKey = c.VisitKey, trolaKey = null;
        bool reentry = c.Visit > 0 || c.Taunted, wasInterrupted = interrupted || c.Interrupted;
        switch (c.Stage)
        {
            case SpiritStage.Name:
                if (reentry) return null;
                picks.Add(new Pick(firstSession ? "intro" : "intro.again"));
                break;
            case SpiritStage.Business:
                if (reentry || !c.NameAssigned) return null;
                trigger = SpiritTrigger.NameAssigned; picks.Add(new Pick("name.assigned"));
                break;
            case SpiritStage.Hub:
            case SpiritStage.Reckoning:
                if (reentry)
                {
                    if (tauntDone) return null;
                    trigger = SpiritTrigger.AfterTaunt; picks.Add(new Pick("react.taunt"));
                    break;
                }
                if (c.Stage == SpiritStage.Reckoning)
                {
                    trigger = SpiritTrigger.Reckoning; kind = SpiritMomentKind.Counsel;
                    if (Aggressive(c)) picks.Add(new Pick("react.reckless", "", true));
                    else if (CostlyRepeat(lastChoice)) picks.Add(new Pick("react.repeat"));
                    picks.Add(Reckon(advice));
                }
                else if (c.HubRoundsDone <= 0)
                {
                    trigger = SpiritTrigger.HubFirst; kind = SpiritMomentKind.Counsel;
                    if (wasInterrupted) picks.Add(new Pick("react.interrupted"));
                    picks.Add(new Pick("hub.open"));
                    picks.Add(new Pick("advice", SpiritAdvisor.BestKey(advice)));
                    if (!teachDone) picks.Add(new Pick("teach"));
                }
                else
                {
                    trigger = SpiritTrigger.HubSecond;
                    var last = lastChoice;
                    bool aggressive = Aggressive(c), fatal = last != null && (last.Verdict == SpiritVerdict.Fatal || last.Verdict == SpiritVerdict.Hopeless);
                    bool repeat = CostlyRepeat(last), costly = last != null ? last.Verdict == SpiritVerdict.Costly : c.LastDelta < 0;
                    if (aggressive || fatal || repeat || costly || c.Patience <= 2)
                    {
                        kind = SpiritMomentKind.Counsel;
                        if (aggressive || fatal) picks.Add(new Pick("react.reckless", "", true));
                        else if (repeat) picks.Add(new Pick("react.repeat"));
                        else if (wasInterrupted) picks.Add(new Pick("react.interrupted"));
                        picks.Add(new Pick("recover", SpiritAdvisor.BestKey(advice)));
                    }
                    else if (wasInterrupted) picks.Add(new Pick("react.interrupted"));
                    else picks.Add(new Pick(last != null ? last.Verdict == SpiritVerdict.Best ? "react.followed" : "react.ignored_ok" : c.LastDelta > 0 ? "react.followed" : "react.ignored_ok"));
                }
                break;
            case SpiritStage.Ended:
                trigger = SpiritTrigger.Ending;
                if (c.Outcome == GatekeeperOutcome.Liked) picks.Add(new Pick("end.liked"));
                else if (c.Outcome == GatekeeperOutcome.Refused)
                    picks.Add(freeTalk ? new Pick("free.expelled") : new Pick("end.refused", RefusedKey()));
                else if (c.Outcome == GatekeeperOutcome.Passed && !passedSaid && !freeTalk) picks.Add(new Pick("end.passed", reckless ? "ignored" : ""));
                else return null;
                break;
            case SpiritStage.FreeTalk:
                if (!passedSaid) { trigger = SpiritTrigger.Ending; picks.Add(new Pick("end.passed", reckless ? "ignored" : "")); break; }
                trigger = SpiritTrigger.FreeTalk;
                int delta = trolaLines > 0 ? trolaDelta : c.TrolaLastDelta;
                trolaKey = trolaLines > 0 ? "#" + trolaLines : c.TrolaLastLineId + "@" + c.HubRoundsDone;
                if (trolaKey == trolaConsumed || delta == 0 || trolaLines == 0 && string.IsNullOrEmpty(c.TrolaLastLineId)) return null;
                // Each free-form line of his gets at most one whisper.
                visitKey += ":" + trolaKey;
                if (commit) trolaConsumed = trolaKey;
                // GatekeeperResponsePolicy expels an insult only at p<=1; above that it is another amused rebuke.
                picks.Add(new Pick(delta > 0 ? "free.praise" : c.Patience >= 2 ? "free.rebuke" : "free.last"));
                break;
            default:
                return null;
        }
        if (!Scheduler.CanBegin(trigger, kind, visitKey, c.Now)) return null;
        var draft = new Draft(this);
        var cues = new List<SpiritCue>();
        foreach (var pick in picks)
        {
            var cue = Choose(draft, c, pick, false, trigger.ToString());
            if (cue != null) cues.Add(cue);
        }
        // A counsel without its advice line would only scold; a whisper without a fresh line stays silent.
        if (cues.Count == 0) return null;
        var plan = new SpiritMomentPlan
        {
            Trigger = trigger, Kind = kind, Opening = cues, Advice = advice,
            MaxReplies = kind == SpiritMomentKind.Counsel ? (trigger == SpiritTrigger.HubFirst || trigger == SpiritTrigger.HubSecond ? lines.timing.max_replies : Math.Min(1, lines.timing.max_replies)) : 0,
            ListenSeconds = kind == SpiritMomentKind.Counsel ? lines.timing.listen_seconds : 0
        };
        if (!commit) return plan;
        Commit(draft, cues.Count);
        Scheduler.Began(trigger, kind, visitKey, c.Now);
        if (kind == SpiritMomentKind.Counsel) teachDone |= picks.Exists(p => p.Family == "teach");
        if (trigger == SpiritTrigger.AfterTaunt) tauntDone = true;
        if (c.Stage == SpiritStage.Hub || c.Stage == SpiritStage.Reckoning) interrupted = false;
        if (picks.Exists(p => p.Family == "end.passed")) passedSaid = true;
        return plan;
    }

    // Tab at hub or reckoning: a one-reply counsel with her current best advice.
    public SpiritMomentPlan Summon(SpiritCheckpoint c)
    {
        if (c == null || c.Stage != SpiritStage.Hub && c.Stage != SpiritStage.Reckoning) return null;
        if (!Scheduler.CanBegin(SpiritTrigger.Summon, SpiritMomentKind.Counsel, c.VisitKey, c.Now)) return null;
        var advice = AdviceFor(c);
        var draft = new Draft(this);
        var cues = new List<SpiritCue>();
        foreach (var pick in new[] { new Pick("summon"), new Pick("advice", SpiritAdvisor.BestKey(advice)) })
        {
            var cue = Choose(draft, c, pick, true, "Summon");
            if (cue != null) cues.Add(cue);
        }
        if (cues.Count == 0) return null;
        Commit(draft, cues.Count);
        Scheduler.Began(SpiritTrigger.Summon, SpiritMomentKind.Counsel, c.VisitKey, c.Now);
        return new SpiritMomentPlan { Trigger = SpiritTrigger.Summon, Kind = SpiritMomentKind.Counsel, Opening = cues, Advice = advice,
            MaxReplies = Math.Min(1, lines.timing.max_replies), ListenSeconds = lines.timing.listen_seconds };
    }

    // Rules 1-11 in Rule(); first match wins. Never null: an exhausted family falls back to answer.repeat.
    public SpiritCue Answer(SpiritCheckpoint c, SpiritDecision d, int replyIndex, int maxReplies)
    {
        if (c == null) throw new ArgumentNullException(nameof(c));
        d ??= new SpiritDecision();
        var advice = AdviceFor(c);
        var (rule, pick, close) = Rule(c, d, advice);
        var draft = new Draft(this);
        var cue = Choose(draft, c, pick, false, "") ?? Choose(draft, c, new Pick("answer.repeat", "", pick.Alarm), false, "")
            ?? Choose(draft, c, pick, true, "") ?? new SpiritCue { Family = pick.Family, Key = pick.Key };
        cue.Rule = "rule " + rule + " " + pick.Family + (pick.Key.Length > 0 ? "." + pick.Key : "") + " · reply " + (replyIndex + 1) + "/" + Math.Max(1, maxReplies);
        cue.CloseAfter = close; cue.Alarm = pick.Alarm;
        Commit(draft, 1);
        return cue;
    }

    // Spoken cut-off for the window's close reason; null = quiet close.
    public SpiritCue CutOff(SpiritCheckpoint c, SpiritCloseReason reason)
    {
        string family = reason == SpiritCloseReason.Budget ? "close.budget" : reason == SpiritCloseReason.Replies ? "close.replies" : null;
        return family == null || c == null ? null : Fixed(c, family, "CutOff " + reason);
    }

    public SpiritCue DraftWarning(SpiritCheckpoint c) => c == null ? null : Fixed(c, "close.draft", "DraftTooLong");

    // Call before Choose reaches the story: `before` is the checkpoint the label was clicked at.
    public void NoteChoice(SpiritCheckpoint before, string label)
    {
        if (before == null || before.Stage != SpiritStage.Hub && before.Stage != SpiritStage.Reckoning) return;
        var advice = AdviceFor(before);
        SpiritOption chosen = null;
        foreach (var option in advice.Options) if (option.Label == label) chosen = option;
        if (chosen == null) return;
        lastChoice = chosen;
        // "I did warn you" only if a warning against this very option had started playing (a click can cut her off first).
        if (before.Stage == SpiritStage.Reckoning) { finalChoice = chosen; warned = chosen.Verdict == SpiritVerdict.Fatal && warnedAgainst.Contains(chosen.Target); }
        if (chosen.Aggressive || chosen.Verdict == SpiritVerdict.Costly || chosen.Verdict == SpiritVerdict.Fatal) reckless = true;
    }

    public void NoteInterrupted() { interrupted = true; }

    // Every line Trola starts (LineStarted). Free talk reacts once to the latest line's patience delta.
    public void NoteTrolaLine(string lineId, int patienceDelta, bool isReaction) { trolaLines++; trolaDelta = patienceDelta; }

    public void NoteSpoken(SpiritCue cue)
    {
        if (cue == null) return;
        if (!string.IsNullOrEmpty(cue.LineId)) used.Add(cue.LineId);
        if (!string.IsNullOrEmpty(cue.WarnsAgainst)) warnedAgainst.Add(cue.WarnsAgainst);
    }

    // Every (family, key, patience) the rules can ask for, over every checkpoint the advisor can produce.
    public IEnumerable<(string family, string key, int minPatience, int maxPatience)> RequiredLines()
    {
        var need = new List<(string, string, int, int)>();
        var seen = new HashSet<string>();
        void Add(string family, string key, int min, int max)
        {
            for (int p = min; p <= max; p++)
            {
                string k = KeyFor(family, key, p);
                if (seen.Add(family + "|" + k + "|" + p)) need.Add((family, k, p, p));
            }
        }
        foreach (string family in new[] { "intro", "intro.again", "name.assigned", "teach", "summon", "hub.open",
            "react.reckless", "react.repeat", "react.interrupted", "react.taunt", "react.followed", "react.ignored_ok",
            "answer.almost", "answer.agree", "answer.dismiss", "answer.self", "answer.cannot_act", "answer.to_trola",
            "answer.offworld", "answer.override", "answer.repeat", "plan.reckless",
            "close.budget", "close.replies", "close.draft", "end.liked", "free.praise", "free.expelled" })
            Add(family, "", 0, 5);
        Add("end.passed", "", 0, 5); Add("end.passed", "ignored", 0, 5);
        foreach (string key in new[] { "", "warned", "demand" }) Add("end.refused", key, 0, 5);
        Add("free.rebuke", "", 2, 5); Add("free.last", "", 0, 1);
        for (int p = 0; p <= 5; p++) Add("answer.read", SpiritAdvisor.Band(p).ToString().ToLowerInvariant(), p, p);
        foreach (var state in advisor.Checkpoints())
        {
            var stage = state.node == SpiritAdvisor.ReckoningNode ? SpiritStage.Reckoning : SpiritStage.Hub;
            var advice = advisor.Advise(state.node, state.patience, state.hubRoundsDone, state.used);
            int p = advice.Patience;
            string best = SpiritAdvisor.BestKey(advice);
            if (stage == SpiritStage.Hub) Add(state.hubRoundsDone == 0 ? "advice" : "recover", best, p, p);
            else { var reckon = Reckon(advice); Add(reckon.Family, reckon.Key, p, p); }
            Add("advice", best, p, p);
            foreach (string family in new[] { "answer.doubt", "answer.advice", "answer.unclear" }) Add(family, best, p, p);
            foreach (var plan in Plans)
                foreach (bool recklessTalk in new[] { false, true })
                {
                    var pick = PlanPick(advice, plan, stage, recklessTalk).pick;
                    Add(pick.Family, pick.Key, p, p);
                }
        }
        return need;
    }

    // --- rules -------------------------------------------------------------------------------------------

    private (int rule, Pick pick, bool close) Rule(SpiritCheckpoint c, SpiritDecision d, SpiritAdvice advice)
    {
        string best = SpiritAdvisor.BestKey(advice);
        var plan = d.PlanP < PlanConfidence ? SpiritPlan.None : d.Plan;
        if (d.Scope == SpiritScope.OutOfWorld) return (1, new Pick("answer.offworld"), false);
        if (d.Scope == SpiritScope.RoleOverride) return (1, new Pick("answer.override"), false);
        if (d.Scope != SpiritScope.InWorld) return (1, new Pick("answer.unclear", best), false);
        // d1 hears "reckless" in rudeness to her ("shut up") and a gate plan in "can you open the gate for me":
        // dismiss/agree ignore the flag, and asking her to act is answered as such unless it is reckless.
        bool rash = d.Reckless && d.Intent != SpiritIntent.Dismiss && d.Intent != SpiritIntent.Agree;
        bool hostile = rash || plan == SpiritPlan.Threaten || plan == SpiritPlan.DemandGate;
        // Words for Trola get "he can't hear you", but a threat or demand aimed at him still raises the alarm (rule 3).
        if (d.ToTrola && !hostile) return (2, new Pick("answer.to_trola"), false);
        if (d.Intent == SpiritIntent.AskSpiritToAct && !rash && !d.ToTrola) return (4, new Pick("answer.cannot_act"), false);
        if (hostile)
        {
            var (pick, _) = PlanPick(advice, plan, c.Stage, rash);
            return (3, new Pick(pick.Family, pick.Key, true, pick.Warns), false);
        }
        if (plan != SpiritPlan.None) { var (pick, close) = PlanPick(advice, plan, c.Stage, false); return (5, pick, close); }
        if (d.Intent == SpiritIntent.Agree) return (6, new Pick("answer.agree"), true);
        if (d.Intent == SpiritIntent.Dismiss) return (6, new Pick("answer.dismiss"), true);
        if (d.Intent == SpiritIntent.Doubt) return (7, new Pick("answer.doubt", best), false);
        if (d.Intent == SpiritIntent.AskAdvice) return (8, new Pick("answer.advice", best), false);
        if (d.Intent == SpiritIntent.AskAboutTrola)
        {
            // "We're close" only while he is merely wary; annoyed, listening and warm are the truer answer (the last two imply progress).
            var band = SpiritAdvisor.Band(c.Patience);
            return (9, advice != null && advice.AlmostThere && band == SpiritBand.Wary ? new Pick("answer.almost") : new Pick("answer.read", band.ToString().ToLowerInvariant()), false);
        }
        if (d.Intent == SpiritIntent.AskAboutSpirit) return (10, new Pick("answer.self"), false);
        return (11, new Pick("answer.unclear", best), false);
    }

    // Verdict of the planned option -> plan.* family. reckless: talk of violence never earns a "yes".
    private (Pick pick, bool close) PlanPick(SpiritAdvice advice, SpiritPlan plan, SpiritStage stage, bool reckless)
    {
        if (plan == SpiritPlan.None) return (new Pick("plan.reckless"), false);
        // Nothing works any more: no plan, offered here or not, gets a hint.
        if (advice != null && advice.Hopeless) return (new Pick("plan.hopeless"), false);
        var option = advisor.Match(advice, plan, stage);
        // Not offered at the reckoning: name the option that wins there (the joke loses at p2).
        if (option == null) return (reckless ? new Pick("plan.reckless") : new Pick("plan.unavailable", stage == SpiritStage.Reckoning ? "reckoning." + SpiritAdvisor.BestKey(advice) : "hub"), false);
        switch (option.Verdict)
        {
            case SpiritVerdict.Hopeless: return (new Pick("plan.hopeless"), false);
            case SpiritVerdict.Fatal: return (new Pick("plan.fatal", option.Target, warns: option.Target), false);
            case SpiritVerdict.Costly: return (new Pick("plan.costly", option.Target), false);
            case SpiritVerdict.Best: return reckless ? (new Pick("plan.reckless"), false) : (new Pick("plan.best"), true);
            default: return (reckless ? new Pick("plan.reckless") : new Pick("plan.good", SpiritAdvisor.BestKey(advice)), false);
        }
    }

    // Reckoning opening: lost when hopeless, "safe" when a gentle option is a trap, else the preferred win.
    private static Pick Reckon(SpiritAdvice advice)
    {
        if (advice == null || advice.Hopeless || advice.Best == null) return new Pick("reckon.lost");
        foreach (var option in advice.Options)
            if (option.Verdict == SpiritVerdict.Fatal && !option.Aggressive) return new Pick("reckon.safe", advice.Best.Target, warns: option.Target);
        return new Pick("reckon.best", advice.Best.Target);
    }

    private bool Aggressive(SpiritCheckpoint c) => lastChoice != null ? lastChoice.Aggressive : c.LastDelta <= -2;
    // "Twice? He saw straight through that." only for a repeat that cost patience (a second camp question still raises it).
    private static bool CostlyRepeat(SpiritOption o) => o != null && o.Repeat && o.Delta < 0;

    private string RefusedKey()
    {
        if (finalChoice == null) return "";
        if (finalChoice.Target == "expelled") return "demand";
        return warned ? "warned" : "";
    }

    private SpiritAdvice AdviceFor(SpiritCheckpoint c)
    {
        string node = !string.IsNullOrEmpty(c.NodeId) ? c.NodeId : advisor.NodeFor(c.Choices);
        if (string.IsNullOrEmpty(node)) node = c.Stage == SpiritStage.Reckoning ? SpiritAdvisor.ReckoningNode : SpiritAdvisor.HubNode;
        return advisor.Advise(node, c.Patience, c.HubRoundsDone, c.UsedTargets);
    }

    // --- line selection --------------------------------------------------------------------------------

    private SpiritCue Fixed(SpiritCheckpoint c, string family, string rule)
    {
        var draft = new Draft(this);
        var cue = Choose(draft, c, new Pick(family), false, rule) ?? Choose(draft, c, new Pick(family), true, rule);
        if (cue == null) return null;
        Commit(draft, 1);
        return cue;
    }

    // Specific key if any line covers this patience, else the family's generic "" line.
    private string KeyFor(string family, string key, int patience) =>
        key.Length > 0 && !lines.Has(family, key, patience) && lines.Has(family, "", patience) ? "" : key;

    private SpiritCue Choose(Draft draft, SpiritCheckpoint c, Pick pick, bool allowRepeat, string rule)
    {
        int patience = Math.Max(0, Math.Min(5, c.Patience));
        string key = KeyFor(pick.Family, pick.Key, patience);
        var candidates = new List<SpiritLines.Line>();
        foreach (var line in lines.lines)
            if (line != null && line.family == pick.Family && (line.key ?? "") == key && patience >= line.min_patience && patience <= line.max_patience) candidates.Add(line);
        if (candidates.Count == 0) return null;
        var fresh = candidates.FindAll(l => !draft.Used.Contains(l.id));
        SpiritLines.Line chosen;
        // The first encounter keeps catalogue order (each family's first line is the scripted one); later ones rotate.
        if (fresh.Count > 0) chosen = fresh[encounters <= 1 ? 0 : (int)((Hash(pick.Family + "." + key) + (uint)encounters) % (uint)fresh.Count)];
        else if (allowRepeat)
        {
            chosen = candidates[0];
            foreach (var line in candidates) if (Use(draft, line.id) < Use(draft, chosen.id)) chosen = line;
        }
        else return null;
        bool hasName = chosen.text.Contains("{name}");
        bool useName = chosen.always_name || draft.SinceName >= lines.timing.name_every - 1;
        string who = c.NameAssigned || string.IsNullOrWhiteSpace(c.PlayerName) ? SpiritLines.Traveller : c.PlayerName;
        string text = SpiritLines.Resolve(chosen.text, who, c.PlayerName, useName);
        draft.Used.Add(chosen.id); draft.LastUse[chosen.id] = ++draft.Clock;
        draft.SinceName = hasName && useName ? 0 : draft.SinceName + 1;
        return new SpiritCue { LineId = chosen.id, Family = pick.Family, Key = key, Text = text, Rule = rule, Alarm = pick.Alarm, WarnsAgainst = pick.Warns ?? "" };
    }

    private static int Use(Draft draft, string id) => draft.LastUse.TryGetValue(id, out int at) ? at : 0;

    private void Commit(Draft draft, int firings)
    {
        used.Clear(); used.UnionWith(draft.Used);
        lastUse.Clear(); foreach (var pair in draft.LastUse) lastUse[pair.Key] = pair.Value;
        sinceName = draft.SinceName; clock = draft.Clock; RuleFirings += firings;
    }

    // FNV-1a over the seed and family: stable variant rotation without hidden random state.
    private uint Hash(string text)
    {
        uint h = 2166136261u ^ (uint)seed;
        foreach (char ch in text) { h ^= ch; h *= 16777619u; }
        return h;
    }
}
