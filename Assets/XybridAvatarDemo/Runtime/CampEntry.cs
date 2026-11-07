using System;
using UnityEngine;

// Entering the camp, for ExperienceDirector (docs/entry.md): the clock and camera path of the walk from the close-up,
// around Trola and through the open gate, then the fade and the end card. Pure, like RoadApproach: it reads no scene
// and no clock of its own. Stages:
//   Walking  an eased-in walk along a smooth path through the route's points (start, beside Trola, before the gate,
//            through it), turning from Trola's face to the camp; footfalls per stride; the fade to black starts
//            fadeSeconds before the walk ends, so the gate is crossed as the picture goes;
//   Black    the walk is over and the picture is black; the camera holds the last pose;
//   Card     the end card is up (Try again). It stays until the director replays or restarts.
// Each cue is raised at most once.
public sealed class CampEntry
{
    public enum Stage { Walking, Black, Card }
    [Flags] public enum Cue { None = 0, Footstep = 1, FadeOut = 2, Card = 4 }

    [Serializable]
    public sealed class Settings
    {
        [Tooltip("The walk from the close-up through the gate, and its eased start; the walk keeps its pace to the end.")]
        public float walkSeconds = 3.8f, accelerateSeconds = .9f;
        [Tooltip("The fade to black over the end of the walk, and the pause in black before the end card.")]
        public float fadeSeconds = 1.1f, cardDelay = .35f;
        [Tooltip("Fraction of the path over which the gaze turns from Trola's face to the camp ahead.")]
        public float lookTurn = .3f;
        [Tooltip("Restrained walking motion at full pace, as on the road: stride, bob and sway (metres) and roll (degrees).")]
        public float strideMeters = .8f, bobMeters = .016f, swayMeters = .008f, rollDegrees = .2f;
    }

    // World-space route through points[0..n-1] (at least two), looking from lookFrom to lookTo.
    public struct Route
    {
        public Vector3[] points;
        public Vector3 lookFrom, lookTo;
        public float fieldOfView;
    }
    // focusDistance: to the point the gaze is on, for depth of field (TrolaAtmosphere focuses there, not on Trola's face).
    public struct Pose { public Vector3 position; public Quaternion rotation; public float fieldOfView, focusDistance; }

    public Settings Timing { get; }
    public Route Path { get; }
    public Stage Current { get; private set; } = Stage.Walking;
    public float StageSeconds { get; private set; }
    public float Length { get; }
    public int Footsteps { get; private set; }
    private Cue raised;
    private const int Samples = 96;
    private readonly Vector3[] samples = new Vector3[Samples + 1];
    private readonly float[] distances = new float[Samples + 1];

    public CampEntry(Settings settings, Route route)
    {
        Timing = settings ?? new Settings();
        if (route.points == null || route.points.Length < 2) throw new ArgumentException("A camp entry needs at least two points.", nameof(route));
        Path = route;
        // Arc-length table, so the walk keeps an even pace however the points are spaced.
        for (int i = 0; i <= Samples; i++)
        {
            samples[i] = Spline(route.points, i / (float)Samples);
            distances[i] = i == 0 ? 0 : distances[i - 1] + Vector3.Distance(samples[i - 1], samples[i]);
        }
        Length = Mathf.Max(.01f, distances[Samples]);
    }

    public bool Raised(Cue cue) => (raised & cue) != 0;
    public float WalkSeconds => Mathf.Max(.5f, Timing.walkSeconds);
    public float FadeSeconds => Mathf.Clamp(Timing.fadeSeconds, .05f, WalkSeconds);
    // Fraction of the path covered, 0-1, eased in; 1 once the walk is over.
    public float Progress => Current == Stage.Walking ? Covered(StageSeconds) : 1;
    // Walking pace as a fraction of full pace: scales bob, sway and footfall loudness.
    public float Pace => Current == Stage.Walking ? Speed(StageSeconds) : 0;
    // Metres per second at full pace, for F4 and the checks.
    public float CruiseSpeed => Length / Mathf.Max(.01f, WalkSeconds - Ramp / 2);

    public Cue Advance(float seconds)
    {
        Cue cues = Cue.None;
        StageSeconds += Mathf.Max(0, seconds);
        if (Current == Stage.Walking)
        {
            int steps = Mathf.FloorToInt(Covered(Mathf.Min(StageSeconds, WalkSeconds)) * Length / Mathf.Max(.05f, Timing.strideMeters));
            if (steps > Footsteps) { Footsteps = steps; cues |= Cue.Footstep; }
            if (StageSeconds >= WalkSeconds - FadeSeconds) cues |= Raise(Cue.FadeOut);
            if (StageSeconds < WalkSeconds) return cues;
            Enter(Stage.Black, StageSeconds - WalkSeconds);
        }
        if (Current == Stage.Black && StageSeconds >= Mathf.Max(0, Timing.cardDelay))
        {
            cues |= Raise(Cue.Card);
            Enter(Stage.Card, 0);
        }
        return cues;
    }

    public Pose Evaluate()
    {
        float progress = Progress, pace = Pace, travelled = progress * Length;
        Vector3 position = At(travelled);
        Vector3 ahead = Vector3.Lerp(Path.lookFrom, Path.lookTo, Smooth(progress / Mathf.Max(.01f, Timing.lookTurn)));
        Vector3 forward = ahead - position;
        if (forward.sqrMagnitude < 1e-6f) forward = At(Mathf.Min(Length, travelled + .1f)) - position;
        if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
        var rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        // One bob per footfall, one sway and roll cycle per two, fading with the pace (as on the road).
        float phase = travelled / Mathf.Max(.05f, Timing.strideMeters) * Mathf.PI;
        position += rotation * new Vector3(Timing.swayMeters * pace * Mathf.Sin(phase), Timing.bobMeters * pace * (Mathf.Abs(Mathf.Sin(phase)) - .5f), 0);
        rotation *= Quaternion.Euler(0, 0, Timing.rollDegrees * pace * Mathf.Sin(phase));
        return new Pose { position = position, rotation = rotation, fieldOfView = Path.fieldOfView, focusDistance = Mathf.Max(.5f, Vector3.Distance(position, ahead)) };
    }

    // Closest approach of the walk to a point (horizontal), sampled: the checks use it to prove the path clears Trola.
    public float ClosestHorizontal(Vector3 point)
    {
        float best = float.MaxValue;
        foreach (var sample in samples)
        {
            var offset = sample - point; offset.y = 0;
            best = Mathf.Min(best, offset.magnitude);
        }
        return best;
    }

    private Cue Raise(Cue cue)
    {
        if ((raised & cue) != 0) return Cue.None;
        raised |= cue; return cue;
    }
    private void Enter(Stage stage, float carry) { Current = stage; StageSeconds = Mathf.Max(0, carry); }

    private float Ramp => Mathf.Clamp(Timing.accelerateSeconds, 0, WalkSeconds / 2);
    // Eased-in distance: smoothstep velocity up over the ramp, then full pace to the end (the fade covers the arrival).
    private float Covered(float t)
    {
        float up = Ramp, cruise = 1 / Mathf.Max(.01f, WalkSeconds - up / 2);
        t = Mathf.Clamp(t, 0, WalkSeconds);
        if (up > 0 && t < up) { float u = t / up; return cruise * up * (u * u * u - u * u * u * u / 2); }
        return Mathf.Clamp01(cruise * (up / 2 + t - up));
    }
    private float Speed(float t) => t <= 0 || t >= WalkSeconds ? 0 : Ramp > 0 && t < Ramp ? Smooth(t / Ramp) : 1;

    private Vector3 At(float distance)
    {
        distance = Mathf.Clamp(distance, 0, Length);
        int high = Array.BinarySearch(distances, distance);
        if (high >= 0) return samples[high];
        high = Mathf.Clamp(~high, 1, Samples);
        float span = distances[high] - distances[high - 1];
        return Vector3.Lerp(samples[high - 1], samples[high], span > 1e-6f ? (distance - distances[high - 1]) / span : 0);
    }
    // Catmull-Rom through the points (the ends repeated), t over the whole route.
    private static Vector3 Spline(Vector3[] points, float t)
    {
        int segments = points.Length - 1;
        float scaled = Mathf.Clamp01(t) * segments;
        int i = Mathf.Min(segments - 1, Mathf.FloorToInt(scaled));
        float u = scaled - i;
        Vector3 p0 = points[Mathf.Max(0, i - 1)], p1 = points[i], p2 = points[i + 1], p3 = points[Mathf.Min(segments, i + 2)];
        return .5f * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (3 * p1 - p0 - 3 * p2 + p3) * u * u * u);
    }
    private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }
}
