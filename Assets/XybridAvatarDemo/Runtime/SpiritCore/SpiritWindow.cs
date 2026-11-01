using System;

// One mind moment as a state machine; the director injects listening seconds, so tests need no clock.
// Speaking -> Listening <-> Thinking -> Answering -> Closing -> Closed; Abort snaps from anywhere.
public sealed class SpiritWindow
{
    private readonly SpiritLines.Timing timing;
    private int maxReplies;
    private float listenSeconds, silence;
    private bool closeAfter, draftWarned, closingLine;
    public SpiritWindowState State { get; private set; }
    public SpiritMomentKind Kind { get; private set; }
    // Bumped on Open, Closed and Abort: async results (d1, voice) carrying an older token are stale.
    public int Token { get; private set; }
    public int RepliesUsed { get; private set; }
    public int RepliesLeft => Math.Max(0, maxReplies - RepliesUsed);
    public float ListenUsed { get; private set; }
    public float ListenSeconds => listenSeconds;
    public float ListenRemaining01 => listenSeconds > 0 ? Math.Max(0, Math.Min(1, 1 - ListenUsed / listenSeconds)) : 0;
    // Trola's idle clock is frozen while she talks or thinks, and runs while the player reads or types.
    public bool HoldsIdleClock => State == SpiritWindowState.Speaking || State == SpiritWindowState.Thinking || State == SpiritWindowState.Answering
        || State == SpiritWindowState.Closing && closingLine;
    public bool InputOpen => State == SpiritWindowState.Listening && RepliesLeft > 0;
    public bool IsOpen => State != SpiritWindowState.Closed;
    public bool DraftWarned => draftWarned;
    public SpiritCloseReason CloseReason { get; private set; }

    public SpiritWindow(SpiritLines.Timing timing) { this.timing = timing ?? new SpiritLines.Timing(); }

    public void Open(SpiritMomentKind kind, int maxReplies, float listenSeconds)
    {
        Token++;
        State = SpiritWindowState.Speaking; Kind = kind; CloseReason = SpiritCloseReason.None;
        this.maxReplies = kind == SpiritMomentKind.Counsel ? Math.Max(0, maxReplies) : 0;
        this.listenSeconds = kind != SpiritMomentKind.Counsel ? 0 : listenSeconds > 0 && !float.IsInfinity(listenSeconds) ? listenSeconds : timing.listen_seconds;
        RepliesUsed = 0; ListenUsed = silence = 0; closeAfter = draftWarned = closingLine = false;
    }

    public void OpeningFinished()
    {
        if (State != SpiritWindowState.Speaking) return;
        if (Kind == SpiritMomentKind.Counsel && RepliesLeft > 0 && listenSeconds > 0) { State = SpiritWindowState.Listening; silence = 0; }
        else Close(SpiritCloseReason.Finished);
    }

    // Enter in the mind field. False (and nothing changes) unless the field is open and the length fits.
    public bool Submit(int length)
    {
        if (!InputOpen || length <= 0 || length > timing.reply_chars) return false;
        RepliesUsed++; silence = 0; State = SpiritWindowState.Thinking;
        return true;
    }

    public void AnswerReady(bool closeAfter)
    {
        if (State != SpiritWindowState.Thinking) return;
        this.closeAfter = closeAfter; State = SpiritWindowState.Answering;
    }

    // closeAfter answers (agree, dismiss, plan.best) close quietly; the last reply asks for close.replies.
    public void AnswerFinished()
    {
        if (State != SpiritWindowState.Answering) return;
        if (closeAfter) Close(SpiritCloseReason.Finished);
        else if (RepliesLeft == 0) Close(SpiritCloseReason.Replies);
        else if (ListenUsed >= listenSeconds) Close(SpiritCloseReason.Budget);
        else { State = SpiritWindowState.Listening; silence = 0; }
    }

    // Listening time only accrues in Listening. Close is returned once, on the transition into Closing.
    public SpiritWindowEvent Tick(float listeningDt, int draftLength)
    {
        if (State != SpiritWindowState.Listening) return SpiritWindowEvent.None;
        float dt = float.IsNaN(listeningDt) || float.IsInfinity(listeningDt) ? 0 : Math.Max(0, listeningDt);
        ListenUsed += dt;
        silence = draftLength <= 0 && RepliesUsed == 0 ? silence + dt : 0;
        if (ListenUsed >= listenSeconds) { Close(SpiritCloseReason.Budget); return SpiritWindowEvent.Close; }
        if (silence >= timing.silence_seconds) { Close(SpiritCloseReason.Silence); return SpiritWindowEvent.Close; }
        if (!draftWarned && draftLength >= timing.long_draft) { draftWarned = true; return SpiritWindowEvent.DraftTooLong; }
        return SpiritWindowEvent.None;
    }

    // A spoken cut-off (close.budget / close.replies) keeps Trola's clock held until Closed().
    public void BeginClosingLine() { if (State == SpiritWindowState.Closing) closingLine = true; }

    public void Closed()
    {
        if (State == SpiritWindowState.Closed) return;
        if (CloseReason == SpiritCloseReason.None) CloseReason = SpiritCloseReason.Finished;
        State = SpiritWindowState.Closed; closingLine = false; Token++;
    }

    // Snap: a choice, Trola speaking, Esc, Restart, F7, self-test. Always invalidates in-flight results.
    public void Abort(SpiritCloseReason reason)
    {
        Token++;
        if (State == SpiritWindowState.Closed) return;
        CloseReason = reason == SpiritCloseReason.None ? SpiritCloseReason.Disabled : reason;
        State = SpiritWindowState.Closed; closingLine = false;
    }

    private void Close(SpiritCloseReason reason) { State = SpiritWindowState.Closing; CloseReason = reason; closingLine = false; }
}
