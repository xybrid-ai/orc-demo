using System;
using UnityEngine;

// The viewpoint's fall when Trola strikes (docs/defeat.md), for ExperienceDirector. Pure, like CampEntry. Stages:
//   Waiting  the camera is still the close-up while he winds up; ends when the blow lands (or, should the performer be
//            missing, after impactSeconds);
//   Recoil   a sharp jolt back, up and over from where the camera was;
//   Fall     down to the ground, rolling onto its side and looking up at him (gravity: the drop accelerates);
//   Ground   a small bounce on landing, then a dazed hold while Vesper reacts; at least groundSeconds, at most maxGround;
//   Black    the fade to black has finished; Card once cardDelay has passed.
// Each cue is raised at most once.
public sealed class Knockdown
{
    public enum Stage { Waiting, Recoil, Fall, Ground, Black, Card }
    [Flags] public enum Cue { None = 0, Impact = 1, Landed = 2, React = 4, FadeOut = 8, Card = 16 }

    [Serializable]
    public sealed class Settings
    {
        [Tooltip("Without word from the performer, the blow lands this long after the strike starts.")]
        public float impactSeconds = .78f;
        [Tooltip("The recoil and the fall, in seconds.")]
        public float recoilSeconds = .14f, fallSeconds = .72f;
        [Tooltip("Vesper reacts this long after landing; the ground holds at least groundSeconds and at most maxGround, waiting for her.")]
        public float reactAfter = .5f, groundSeconds = 2.2f, maxGround = 8;
        [Tooltip("The fade to black once she has finished, and the black before the card.")]
        public float fadeSeconds = 1.3f, cardDelay = .4f;
        [Tooltip("Recoil: metres back and up, degrees pitched up and rolled.")]
        public float recoilBack = .22f, recoilRise = .05f, recoilPitch = 9, recoilRoll = 8;
        [Tooltip("On the ground: eye height above his feet, metres back and to the side, and the roll onto the side, in degrees.")]
        public float groundHeight = .3f, groundBack = .55f, groundSide = .25f, groundRoll = 70;
        [Tooltip("Impact shake (metres and degrees, decaying over shakeSeconds), and the landing bounce in metres.")]
        public float shakeMeters = .035f, shakeDegrees = 2.5f, shakeSeconds = .4f, bounceMeters = .035f;
    }

    // Where the camera stood when the blow landed, and the face it looks up at from the ground.
    public struct Route { public Vector3 start, back, side, face; public Quaternion startRotation; public float floor, fieldOfView; }
    public struct Pose { public Vector3 position; public Quaternion rotation; public float fieldOfView, focusDistance; }

    public Settings Timing { get; }
    public Route Path { get; private set; }
    public Stage Current { get; private set; } = Stage.Waiting;
    public float StageSeconds { get; private set; }
    public bool HasRoute { get; private set; }
    private Cue raised;
    private float reactedAt = -1, fadeStartedAt = -1, clock;

    // impactFallback: when the blow lands without the performer's word (its event lost, or no performer); default
    // impactSeconds.
    public Knockdown(Settings settings, float impactFallback = -1)
    {
        Timing = settings ?? new Settings();
        fallback = impactFallback > 0 ? impactFallback : Timing.impactSeconds;
    }
    private readonly float fallback;

    public bool Raised(Cue cue) => (raised & cue) != 0;
    // The camera's pose replaces the close-up from the blow on.
    public float CinematicWeight => Current == Stage.Waiting || !HasRoute ? 0 : 1;

    // landed: the performer raised its impact this frame. quiet: Vesper is not speaking (or is absent). The route is
    // taken when the blow lands (the camera's pose at that moment).
    public Cue Advance(float seconds, bool landed, bool quiet, Func<Route> route)
    {
        Cue cues = Cue.None;
        float step = Mathf.Max(0, seconds);
        clock += step; StageSeconds += step;
        if (Current == Stage.Waiting)
        {
            if (!landed && StageSeconds < fallback) return cues;
            Path = route(); HasRoute = true;
            cues |= Raise(Cue.Impact);
            Enter(Stage.Recoil, 0);
        }
        if (Current == Stage.Recoil && StageSeconds >= Recoil) Enter(Stage.Fall, StageSeconds - Recoil);
        if (Current == Stage.Fall && StageSeconds >= Fall)
        {
            cues |= Raise(Cue.Landed);
            Enter(Stage.Ground, StageSeconds - Fall);
        }
        if (Current == Stage.Ground)
        {
            if (StageSeconds >= Timing.reactAfter && !Raised(Cue.React)) { cues |= Raise(Cue.React); reactedAt = clock; }
            // She has had a moment to start (her voice waits for synthesis), then the hold ends when she is quiet.
            bool done = Raised(Cue.React) && clock - reactedAt >= .6f && quiet && StageSeconds >= Timing.groundSeconds;
            if (done || StageSeconds >= Timing.maxGround) cues |= Raise(Cue.FadeOut);
            if (Raised(Cue.FadeOut) && StageSeconds >= FadeEnd) Enter(Stage.Black, 0);
        }
        if (Current == Stage.Black && StageSeconds >= Timing.cardDelay) { cues |= Raise(Cue.Card); Enter(Stage.Card, 0); }
        return cues;
    }
    private float FadeEnd => fadeStartedAt + Mathf.Max(.05f, Timing.fadeSeconds);

    public Pose Evaluate()
    {
        var p = Path;
        Quaternion grounded = Quaternion.LookRotation((p.face - Grounded).normalized, Vector3.up) * Quaternion.Euler(0, 0, Timing.groundRoll);
        Quaternion recoiled = p.startRotation * Quaternion.Euler(-Timing.recoilPitch, 0, Timing.recoilRoll);
        Vector3 recoilAt = p.start + p.back * Timing.recoilBack + Vector3.up * Timing.recoilRise;
        Vector3 position; Quaternion rotation;
        if (Current == Stage.Waiting) { position = p.start; rotation = p.startRotation; }
        else if (Current == Stage.Recoil)
        {
            float t = EaseOut(StageSeconds / Recoil);
            position = Vector3.Lerp(p.start, recoilAt, t); rotation = Quaternion.Slerp(p.startRotation, recoiled, t);
        }
        else if (Current == Stage.Fall)
        {
            float t = Mathf.Clamp01(StageSeconds / Fall);
            // Across with a quick start, down with gravity.
            Vector3 across = Vector3.Lerp(recoilAt, Grounded, EaseOut(t));
            position = new Vector3(across.x, Mathf.Lerp(recoilAt.y, Grounded.y, t * t), across.z);
            rotation = Quaternion.Slerp(recoiled, grounded, Smooth(t));
        }
        else
        {
            float since = Current == Stage.Ground ? StageSeconds : 99;
            float bounce = Timing.bounceMeters * Mathf.Sin(Mathf.Clamp01(since / .3f) * Mathf.PI) * Mathf.Exp(-since * 4);
            // Dazed: a slow sway of the head on the ground.
            float sway = Mathf.Sin(clock * 1.3f) * .6f, nod = Mathf.Sin(clock * .9f + 1) * .4f;
            position = Grounded + Vector3.up * bounce; rotation = grounded * Quaternion.Euler(nod, sway, 0);
        }
        // Impact shake on top, decaying from the blow.
        float shook = Current == Stage.Recoil ? StageSeconds : Current == Stage.Fall ? Recoil + StageSeconds : 99;
        float shake = Mathf.Exp(-shook / Mathf.Max(.01f, Timing.shakeSeconds / 3)) * (shook < Timing.shakeSeconds ? 1 : 0);
        if (shake > 0)
        {
            // Sines from zero: the first frame of the recoil is exactly where the camera stood.
            position += rotation * new Vector3(Mathf.Sin(shook * 71), Mathf.Sin(shook * 53), 0) * Timing.shakeMeters * shake;
            rotation *= Quaternion.Euler(Mathf.Sin(shook * 61) * Timing.shakeDegrees * shake, Mathf.Sin(shook * 47) * Timing.shakeDegrees * shake, 0);
        }
        return new Pose { position = position, rotation = rotation, fieldOfView = p.fieldOfView, focusDistance = Mathf.Max(.5f, Vector3.Distance(position, p.face)) };
    }

    private Vector3 Grounded
    {
        get { var p = Path; var at = p.start + p.back * Timing.groundBack + p.side * Timing.groundSide; at.y = p.floor + Timing.groundHeight; return at; }
    }
    private float Recoil => Mathf.Max(.01f, Timing.recoilSeconds);
    private float Fall => Mathf.Max(.01f, Timing.fallSeconds);
    private Cue Raise(Cue cue)
    {
        if ((raised & cue) != 0) return Cue.None;
        raised |= cue;
        if (cue == Cue.FadeOut) fadeStartedAt = StageSeconds;
        return cue;
    }
    private void Enter(Stage stage, float carry) { Current = stage; StageSeconds = Mathf.Max(0, carry); }
    private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }
    private static float EaseOut(float x) { x = Mathf.Clamp01(x); return 1 - (1 - x) * (1 - x); }
}
