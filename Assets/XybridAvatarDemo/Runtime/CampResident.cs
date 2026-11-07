using System;
using UnityEngine;

// Background life in the visible camp (docs/camp-activity.md): a short looping routine of authored clips on a restored
// humanoid rig. Lookouts stand their watch, a drinker and a storyteller share the fire, a resident crosses between the
// tents. Each beat stands on, or walks to, a mark in the parent's space; consecutive beats cross-fade. The routine is a
// pure function of the resident's clock, so one skipped off screen resumes where the camp has got to. No dialogue,
// navigation, root motion, colliders or camera ownership.
public sealed class CampResident : MonoBehaviour
{
    [Serializable]
    public struct Beat
    {
        public AnimationClip clip;
        [Tooltip("Seconds in the routine; 0 plays the clip once, from start, at this speed.")]
        public float seconds;
        [Tooltip("Clip playback rate (1 when 0), and where in the clip the beat begins.")]
        public float speed, start;
        [Tooltip("Repeat the clip for the whole beat; otherwise it holds its final frame.")]
        public bool loop;
        [Tooltip("Walk from the previous beat's mark to this one, facing the way; otherwise stand on this mark facing yaw.")]
        public bool walk;
        public Vector3 mark;
        public float yaw;
    }

    public Beat[] routine = Array.Empty<Beat>();
    [Tooltip("Seconds into the routine at start, so residents sharing clips are never in step.")]
    public float phase;
    [Tooltip("Cross-fade between beats, which is also a walk's ease in and out; and the time to turn onto a new heading.")]
    public float blend = .5f, turn = .8f;
    [Tooltip("Hidden behind the shut doors: animates once they start to open.")]
    public bool behindGate;
    public Camera viewer;
    public GatekeeperController gatekeeper;

    private Transform[] rig;
    private Quaternion[] heldRotations;
    private Vector3[] heldPositions;
    private Renderer[] renderers;
    private readonly Plane[] planes = new Plane[6];
    private Vector3 scale;
    private float clock;
    private bool ready, posed;

    public float Clock => clock;
    public float Cycle
    {
        get { float sum = 0; for (int i = 0; i < Count; i++) sum += Duration(i); return sum; }
    }
    private int Count => routine != null ? routine.Length : 0;

    private void Awake() => Initialize();

    private void Initialize()
    {
        if (ready) return;
        scale = transform.localScale;
        // Every transform a sample can write (the skeleton, and anything held in a hand), for the cross-fade.
        var all = GetComponentsInChildren<Transform>(true);
        rig = new Transform[all.Length - 1];
        for (int i = 1; i < all.Length; i++) rig[i - 1] = all[i];
        heldRotations = new Quaternion[rig.Length]; heldPositions = new Vector3[rig.Length];
        renderers = GetComponentsInChildren<Renderer>();
        ready = true;
    }

    private void LateUpdate()
    {
        if (Count == 0) return;
        clock += Mathf.Min(Time.deltaTime, .1f);
        // Off screen, or behind the shut gate, only the root keeps its place on the clock, so a walker can walk into view.
        if (Visible() || !posed) Pose(clock); else Place(clock);
    }

    private bool Visible()
    {
        if (behindGate && gatekeeper != null && !gatekeeper.GateOpenRequested) return false;
        var camera = viewer != null ? viewer : Camera.main;
        if (camera == null) return false;
        GeometryUtility.CalculateFrustumPlanes(camera, planes);
        foreach (var renderer in renderers)
            if (renderer != null && renderer.enabled && GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) return true;
        return false;
    }

    // The full pose at `seconds` on this resident's clock. Also used by the scene builder to save a natural pose.
    public void Pose(float seconds)
    {
        Initialize();
        if (Count == 0) return;
        int beat = Locate(seconds, out float local);
        int before = Previous(beat);
        float weight = Count > 1 && blend > 0 ? Mathf.SmoothStep(0, 1, local / blend) : 1;
        if (weight < 1)
        {
            Sample(before, Duration(before) + local);
            for (int i = 0; i < rig.Length; i++) { heldRotations[i] = rig[i].localRotation; heldPositions[i] = rig[i].localPosition; }
            Sample(beat, local);
            for (int i = 0; i < rig.Length; i++)
            {
                rig[i].localRotation = Quaternion.Slerp(heldRotations[i], rig[i].localRotation, weight);
                rig[i].localPosition = Vector3.Lerp(heldPositions[i], rig[i].localPosition, weight);
            }
        }
        else Sample(beat, local);
        posed = true;
        Place(beat, local);
    }

    // Where the routine stands this resident at `seconds`, in the parent's space.
    public Vector3 MarkAt(float seconds)
    {
        if (Count == 0) return transform.localPosition;
        int beat = Locate(seconds, out float local);
        return Position(beat, local);
    }

    private void Place(float seconds) { int beat = Locate(seconds, out float local); Place(beat, local); }
    private void Place(int beat, float local)
    {
        // A humanoid sample also writes root motion; the routine alone decides where the resident stands.
        transform.localPosition = Position(beat, local);
        float progress = turn > 0 ? Mathf.SmoothStep(0, 1, local / turn) : 1;
        transform.localRotation = Quaternion.Euler(0, Mathf.LerpAngle(Heading(Previous(beat)), Heading(beat), progress), 0);
        transform.localScale = scale;
    }

    private Vector3 Position(int beat, float local)
    {
        var current = routine[beat];
        int before = Previous(beat);
        if (current.walk) return Vector3.Lerp(routine[before].mark, current.mark, Walked(beat, local));
        // A walk slows to a stop over the first moments of the beat after it, while its stride fades out.
        if (routine[before].walk && local < Ramp(before))
            return Vector3.Lerp(routine[Previous(before)].mark, routine[before].mark, Walked(before, Duration(before) + local));
        return current.mark;
    }

    private int Previous(int beat) => (beat + Count - 1) % Count;

    private int Locate(float seconds, out float local)
    {
        float cycle = Cycle, t = cycle > 0 ? Mathf.Repeat(seconds + phase, cycle) : 0;
        for (int i = 0; i < Count; i++)
        {
            float duration = Duration(i);
            if (t < duration || i == Count - 1) { local = Mathf.Min(t, duration); return i; }
            t -= duration;
        }
        local = 0; return 0;
    }

    private float Duration(int i)
    {
        var beat = routine[i];
        if (beat.seconds > 0) return beat.seconds;
        return beat.clip != null ? Mathf.Max(.1f, (beat.clip.length - beat.start) / Rate(beat)) : 1;
    }
    private static float Rate(Beat beat) => beat.speed > 0 ? beat.speed : 1;

    private void Sample(int i, float local)
    {
        var beat = routine[i];
        if (beat.clip == null) return;
        float length = beat.clip.length, time = beat.start + local * Rate(beat);
        beat.clip.SampleAnimation(gameObject, beat.loop && length > 0 ? Mathf.Repeat(time, length) : Mathf.Min(time, length));
    }

    private float Heading(int i)
    {
        var beat = routine[i];
        if (!beat.walk) return beat.yaw;
        var way = beat.mark - routine[Previous(i)].mark;
        return way.sqrMagnitude > 1e-6f ? Mathf.Atan2(way.x, way.z) * Mathf.Rad2Deg : beat.yaw;
    }

    private float Ramp(int walk) => Mathf.Min(blend, Duration(walk) / 2);
    // Fraction of walk beat `i` covered `local` seconds after it began: speeding up while its stride fades in, steady
    // to the end of the beat, then slowing to a stop over the next beat's cross-fade as the stride fades out. The steady
    // pace (the walk's length over its seconds) is the one the builder matches the clip's stride to.
    private float Walked(int i, float local)
    {
        float seconds = Duration(i), ramp = Ramp(i), pace = 1 / seconds;
        if (ramp <= 0) return Mathf.Clamp01(local * pace);
        if (local < ramp) return .5f * pace * local * local / ramp;
        if (local <= seconds) return pace * (local - ramp / 2);
        float after = Mathf.Min(local - seconds, ramp);
        return 1 - .5f * pace * ramp + pace * after - .5f * pace * after * after / ramp;
    }
}
