using System;
using UnityEngine;

// Trola's strike on a provoked expulsion (docs/defeat.md): the clock and blend amounts GatekeeperPerformer turns into
// bone offsets on his right arm and torso. Pure, like RoadApproach and CampEntry. Stages:
//   Windup   anticipation: the arm rises and cocks back, the torso turns the striking shoulder away and leans back;
//   Swing    the blow, accelerating into the viewer; Impact is raised as it lands;
//   Follow   follow-through: the arm carries on down and the body stays committed for a moment;
//   Recover  back to the idle pose;
//   Done     nothing is applied.
public sealed class TrolaStrike
{
    public enum Stage { Windup, Swing, Follow, Recover, Done }
    [Flags] public enum Cue { None = 0, Impact = 1, Done = 2 }

    [Serializable]
    public sealed class Settings
    {
        [Tooltip("Anticipation, the swing into the viewer, the follow-through and the recovery, in seconds.")]
        public float windupSeconds = .62f, swingSeconds = .16f, followSeconds = .3f, recoverSeconds = .95f;
        [Tooltip("Torso turn about his up axis (positive takes the striking shoulder back) at the end of the wind-up and at impact, in degrees.")]
        public float windupTwist = 24, impactTwist = -20;
        [Tooltip("Torso lean towards the viewer at the end of the wind-up and at impact, in degrees.")]
        public float windupLean = -8, impactLean = 16;
        [Tooltip("How far the arm carries on down after impact, in degrees.")]
        public float followDrop = 22;
        [Tooltip("Fury on his face at full strike: brows down, eyes wide, snarl (0-1).")]
        public float browDown = .9f, eyeWide = .7f, snarl = 1;
    }

    public Settings Timing { get; }
    public Stage Current { get; private set; } = Stage.Windup;
    public float StageSeconds { get; private set; }
    public float Elapsed { get; private set; }
    private Cue raised;

    public TrolaStrike(Settings settings) { Timing = settings ?? new Settings(); }

    public bool Raised(Cue cue) => (raised & cue) != 0;
    public float ImpactSeconds => Length(Stage.Windup) + Length(Stage.Swing);
    public float TotalSeconds => ImpactSeconds + Length(Stage.Follow) + Length(Stage.Recover);

    // How much of the strike pose replaces the idle: in over the wind-up, held, out over the recovery.
    public float ArmWeight => Current == Stage.Windup ? Smooth(StageSeconds / Length(Stage.Windup))
        : Current == Stage.Recover ? 1 - Smooth(StageSeconds / Length(Stage.Recover)) : Current == Stage.Done ? 0 : 1;
    // Within the strike pose: 0 cocked back, 1 landed (the swing accelerates, as a blow does).
    public float Swing => Current == Stage.Windup ? 0 : Current == Stage.Swing ? EaseIn(StageSeconds / Length(Stage.Swing)) : 1;
    // After impact: 0 landed, 1 carried through.
    public float Follow => Current == Stage.Follow ? EaseOut(StageSeconds / Length(Stage.Follow)) : Current == Stage.Recover || Current == Stage.Done ? 1 : 0;
    // Torso turn and lean, in degrees: towards the wind-up's, then through to the impact's, faded by ArmWeight. His face's
    // fury follows ArmWeight too.
    public float Twist => Mathf.Lerp(Timing.windupTwist, Timing.impactTwist, Swing) * ArmWeight;
    public float Lean => Mathf.Lerp(Timing.windupLean, Timing.impactLean, Swing) * ArmWeight;

    public Cue Advance(float seconds)
    {
        if (Current == Stage.Done) return Cue.None;
        Cue cues = Cue.None;
        float step = Mathf.Max(0, seconds);
        Elapsed += step; StageSeconds += step;
        while (Current != Stage.Done && StageSeconds >= Length(Current))
        {
            float carry = StageSeconds - Length(Current);
            if (Current == Stage.Swing) cues |= Raise(Cue.Impact);
            Current = Current + 1; StageSeconds = carry;
            if (Current == Stage.Done) cues |= Raise(Cue.Done);
        }
        return cues;
    }

    private float Length(Stage stage) => Mathf.Max(.01f, stage == Stage.Windup ? Timing.windupSeconds : stage == Stage.Swing ? Timing.swingSeconds
        : stage == Stage.Follow ? Timing.followSeconds : stage == Stage.Recover ? Timing.recoverSeconds : 0);
    private Cue Raise(Cue cue)
    {
        if ((raised & cue) != 0) return Cue.None;
        raised |= cue; return cue;
    }
    private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }
    private static float EaseIn(float x) { x = Mathf.Clamp01(x); return x * x; }
    private static float EaseOut(float x) { x = Mathf.Clamp01(x); return 1 - (1 - x) * (1 - x); }
}
