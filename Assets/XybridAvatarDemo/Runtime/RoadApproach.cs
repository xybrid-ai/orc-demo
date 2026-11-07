using System;
using UnityEngine;

// The road approach's clock and camera path for ExperienceDirector (docs/approach.md). Pure: it reads no scene and no
// clock of its own, so the headless checks drive it. Stages:
//   Preparing  the distant view holds while speech is prepared (at least minimumHold, at most maximumHold);
//   Walking    the eased walk to the gate, with footfalls, Vesper's warning and, near the end, Trola's challenge
//              ("Stop there...": the walker slows to a halt as he speaks). The challenge waits until Vesper has
//              finished (quiet), but never past the halt;
//   Settling   the camera eases from the arrival pose into the close-up;
//   Done       the cinematic weight is 0 and the conversation owns the camera.
// Every cue is raised at most once per approach. Skip lands at Done from any stage and raises the challenge first if
// it had not been raised, so the encounter always starts exactly once.
public sealed class RoadApproach
{
    public enum Stage { Preparing, Walking, Settling, Done }
    [Flags] public enum Cue { None = 0, Footstep = 1, Warning = 2, Challenge = 4, Settled = 8 }

    [Serializable]
    public sealed class Settings
    {
        [Tooltip("The distant view holds at least this long, and until speech is prepared or maximumHold passes.")]
        public float minimumHold = 1.2f, maximumHold = 6;
        [Tooltip("The walk from the road to the gate, with eased start and stop.")]
        public float walkSeconds = 9.6f, accelerateSeconds = 1.4f, decelerateSeconds = 2.4f;
        [Tooltip("Camera move from the arrival pose into the close-up, once the walk halts.")]
        public float settleSeconds = 1.2f;
        [Tooltip("Seconds into the walk when Vesper's warning is cued.")]
        public float warningAt = .8f;
        [Tooltip("Trola's challenge is cued when this many metres of the walk remain and Vesper is quiet; at the latest when the walk ends.")]
        public float challengeMeters = 3;
        [Tooltip("Restrained walking motion: stride length, head bob and sway (metres) and roll (degrees) at full pace.")]
        public float strideMeters = .8f, bobMeters = .016f, swayMeters = .008f, rollDegrees = .2f; // The 60 mm lens magnifies roll.
    }

    // World-space route: a quadratic curve from start through control to arrival, looking from lookFrom to lookTo.
    public struct Route
    {
        public Vector3 start, control, arrival, lookFrom, lookTo;
        public float fieldOfView;
        public float Length
        {
            get
            {
                float length = 0; Vector3 previous = start;
                for (int i = 1; i <= 32; i++) { var next = Curve(this, i / 32f); length += Vector3.Distance(previous, next); previous = next; }
                return length;
            }
        }
    }
    public struct Pose { public Vector3 position; public Quaternion rotation; public float fieldOfView; }

    public Settings Timing { get; }
    public Route Path { get; }
    public Stage Current { get; private set; } = Stage.Preparing;
    public float StageSeconds { get; private set; }
    public bool Skipped { get; private set; }
    public int Footsteps { get; private set; }
    private Cue raised;
    private readonly float length;

    public RoadApproach(Settings settings, Route route)
    {
        Timing = settings ?? new Settings(); Path = route;
        length = Mathf.Max(.01f, route.Length);
    }

    public bool Raised(Cue cue) => (raised & cue) != 0;
    public float WalkSeconds => Mathf.Max(.1f, Timing.walkSeconds);
    public float SettleSeconds => Mathf.Max(.01f, Timing.settleSeconds);
    // 1 is the approach's own pose; 0 is TrolaAtmosphere's close-up. Smoothly eased through the settle.
    public float CinematicWeight => Current == Stage.Preparing || Current == Stage.Walking ? 1
        : Current == Stage.Settling ? 1 - SmoothStep(StageSeconds / SettleSeconds) : 0;
    // Fraction of the route covered, 0-1, eased at both ends; constant outside the walk.
    public float Progress => Current == Stage.Preparing ? 0 : Current == Stage.Walking ? Covered(StageSeconds) : 1;
    // Walking pace as a fraction of full pace: scales the bob, the sway and the footsteps' loudness.
    public float Pace => Current == Stage.Walking ? Speed(StageSeconds) : 0;

    // Advances the clock. ready: the opening's speech is prepared (or has failed); quiet: Vesper is not speaking. Returns
    // the cues raised by this step.
    public Cue Advance(float seconds, bool ready, bool quiet = true)
    {
        if (Current == Stage.Done) return Cue.None;
        Cue cues = Cue.None;
        StageSeconds += Mathf.Max(0, seconds);
        if (Current == Stage.Preparing)
        {
            if (StageSeconds < Timing.minimumHold || !ready && StageSeconds < Timing.maximumHold) return cues;
            Enter(Stage.Walking);
        }
        if (Current == Stage.Walking)
        {
            int steps = Mathf.FloorToInt(Covered(Mathf.Min(StageSeconds, WalkSeconds)) * length / Mathf.Max(.05f, Timing.strideMeters));
            if (steps > Footsteps) { Footsteps = steps; cues |= Cue.Footstep; }
            if (StageSeconds >= Timing.warningAt && !Raised(Cue.Challenge)) cues |= Raise(Cue.Warning); // Never after Trola speaks.
            if (quiet && (1 - Covered(StageSeconds)) * length <= Timing.challengeMeters) cues |= Raise(Cue.Challenge);
            if (StageSeconds < WalkSeconds) return cues;
            cues |= Raise(Cue.Challenge);
            Enter(Stage.Settling);
        }
        if (Current == Stage.Settling && StageSeconds >= SettleSeconds)
        {
            cues |= Raise(Cue.Settled);
            Enter(Stage.Done);
        }
        return cues;
    }

    // Space / Skip opening: straight to the close-up. The challenge is raised now if the walk had not reached it.
    public Cue Skip()
    {
        if (Current == Stage.Done) return Cue.None;
        Skipped = true;
        Cue cues = Raise(Cue.Challenge) | Raise(Cue.Settled);
        Enter(Stage.Done);
        return cues;
    }

    // The approach's own camera pose, with the walking motion at the current pace. The settle holds the arrival pose
    // (TrolaAtmosphere blends it into the close-up by CinematicWeight).
    public Pose Evaluate()
    {
        float progress = Progress, pace = Pace;
        Vector3 position = Curve(Path, progress);
        Vector3 ahead = Vector3.Lerp(Path.lookFrom, Path.lookTo, SmoothStep(progress));
        Vector3 forward = ahead - position;
        if (forward.sqrMagnitude < 1e-6f) forward = Path.arrival - Path.start;
        if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
        var rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        // One bob per footfall, one sway and roll cycle per two (left, right), all fading with the pace.
        float stride = Mathf.Max(.05f, Timing.strideMeters), phase = progress * length / stride * Mathf.PI;
        float bob = Timing.bobMeters * pace * (Mathf.Abs(Mathf.Sin(phase)) - .5f);
        float sway = Timing.swayMeters * pace * Mathf.Sin(phase);
        position += rotation * new Vector3(sway, bob, 0);
        rotation *= Quaternion.Euler(0, 0, Timing.rollDegrees * pace * Mathf.Sin(phase));
        return new Pose { position = position, rotation = rotation, fieldOfView = Path.fieldOfView };
    }

    private Cue Raise(Cue cue)
    {
        if ((raised & cue) != 0) return Cue.None;
        raised |= cue; return cue;
    }
    private void Enter(Stage stage)
    {
        // Time past the previous stage's end carries over, so a long frame never loses a stage's beginning.
        float carry = Current == Stage.Preparing ? 0 : Current == Stage.Walking ? StageSeconds - WalkSeconds
            : Current == Stage.Settling ? StageSeconds - SettleSeconds : 0;
        Current = stage; StageSeconds = Mathf.Max(0, carry);
    }

    // Eased distance: smoothstep velocity ramps up over accelerateSeconds and down over decelerateSeconds, cruising between.
    private float Ramps(out float up, out float down, out float cruise)
    {
        float total = WalkSeconds;
        up = Mathf.Clamp(Timing.accelerateSeconds, 0, total / 2); down = Mathf.Clamp(Timing.decelerateSeconds, 0, total / 2);
        cruise = 1 / Mathf.Max(.01f, total - up / 2 - down / 2); // full pace, in route fractions per second
        return total;
    }
    private float Covered(float t)
    {
        float total = Ramps(out float up, out float down, out float pace);
        t = Mathf.Clamp(t, 0, total);
        if (up > 0 && t < up) { float u = t / up; return pace * up * (u * u * u - u * u * u * u / 2); }
        float before = pace * up / 2;
        if (t <= total - down || down <= 0) return Mathf.Clamp01(before + pace * (t - up));
        float w = (total - t) / down;
        return Mathf.Clamp01(1 - pace * down * (w * w * w - w * w * w * w / 2));
    }
    private float Speed(float t)
    {
        float total = Ramps(out float up, out float down, out _);
        if (t <= 0 || t >= total) return 0;
        if (up > 0 && t < up) return SmoothStep(t / up);
        if (down > 0 && t > total - down) return SmoothStep((total - t) / down);
        return 1;
    }
    private static float SmoothStep(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }
    private static Vector3 Curve(Route route, float t)
    {
        float u = 1 - t;
        return u * u * route.start + 2 * u * t * route.control + t * t * route.arrival;
    }
}
