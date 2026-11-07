using System;
using UnityEngine;

// Background business for the visible camp set. Reuses the restored standing idle and rig; no dialogue,
// navigation, root motion or camera ownership. Each resident stays on their authored mark.
public sealed class CampResident : MonoBehaviour
{
    public enum Duty { Lookout, ByTheFire }
    public Duty duty;
    public AnimationClip idle;
    public Camera viewer;
    public GatekeeperController gatekeeper;
    public float phase, idleSpeed = .85f;

    private sealed class Bone
    {
        public Transform transform;
        public Quaternion sampled;
        public bool written;
    }
    private Bone chest, head, upperR, lowerR, handR, upperL, lowerL, handL;
    private Bone[] bones;
    private Renderer[] renderers;
    private readonly Plane[] planes = new Plane[6];
    private Vector3 home, scale;
    private Quaternion facing;
    private float clock, nextVisibilityCheck;
    private bool ready, visible;

    private void Awake() { Initialize(); }

    private void Initialize()
    {
        if (ready) return;
        home = transform.localPosition; facing = transform.localRotation; scale = transform.localScale;
        var animator = GetComponent<Animator>();
        Bone Bind(HumanBodyBones human, string fallback)
        {
            var bone = animator != null && animator.isHuman ? animator.GetBoneTransform(human) : null;
            if (bone == null)
                foreach (var child in GetComponentsInChildren<Transform>(true))
                    if (string.Equals(child.name, fallback, StringComparison.OrdinalIgnoreCase)) { bone = child; break; }
            return new Bone { transform = bone };
        }
        chest = Bind(HumanBodyBones.Chest, "spine_02"); head = Bind(HumanBodyBones.Head, "head");
        upperR = Bind(HumanBodyBones.RightUpperArm, "upperarm_r"); lowerR = Bind(HumanBodyBones.RightLowerArm, "lowerarm_r"); handR = Bind(HumanBodyBones.RightHand, "hand_r");
        upperL = Bind(HumanBodyBones.LeftUpperArm, "upperarm_l"); lowerL = Bind(HumanBodyBones.LeftLowerArm, "lowerarm_l"); handL = Bind(HumanBodyBones.LeftHand, "hand_l");
        bones = new[] { chest, head, upperR, lowerR, handR, upperL, lowerL, handL };
        renderers = GetComponentsInChildren<Renderer>();
        ready = true;
    }

    private void LateUpdate()
    {
        clock += Mathf.Min(Time.deltaTime, .1f);
        // The interior resident is hidden behind the shut doors. The towers leave the frame as the portrait settles.
        if (Time.unscaledTime >= nextVisibilityCheck)
        {
            nextVisibilityCheck = Time.unscaledTime + .25f;
            var camera = viewer != null ? viewer : Camera.main;
            visible = false;
            if (camera != null && (duty != Duty.ByTheFire || gatekeeper == null || gatekeeper.GateOpenRequested))
            {
                GeometryUtility.CalculateFrustumPlanes(camera, planes);
                foreach (var renderer in renderers)
                    if (renderer != null && renderer.enabled && GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) { visible = true; break; }
            }
        }
        if (visible) Pose(clock);
    }

    // Also used by the scene builder to save an idle pose instead of the prefab's bind pose.
    public void Pose(float seconds)
    {
        Initialize(); Restore();
        if (idle != null && idle.length > 0) idle.SampleAnimation(gameObject, Mathf.Repeat(seconds * idleSpeed + phase, idle.length));
        transform.localPosition = home; transform.localRotation = facing; transform.localScale = scale;
        foreach (var bone in bones)
            if (bone.transform != null) { bone.sampled = bone.transform.localRotation; bone.written = true; }

        float t = seconds + phase;
        if (duty == Duty.Lookout)
        {
            Turn(chest, transform.up, 7 * Mathf.Sin(t * .31f));
            Turn(head, transform.up, 23 * Mathf.Sin(t * .39f + .6f));
            // An occasional, unhurried hand to the brow, with the guards on different clocks.
            float watch = Pulse(t, 19, 4, 8);
            Aim(upperR, lowerR, transform.right * .7f + transform.up * .25f + transform.forward * .45f, watch);
            Aim(lowerR, handR, -transform.right * .4f + transform.up * .75f + transform.forward * .25f, watch);
        }
        else
        {
            float warm = Pulse(t, 13, 1, 8);
            Turn(chest, transform.right, 10 * warm);
            Turn(head, transform.right, 12 * warm);
            Aim(upperR, lowerR, transform.forward * .45f - transform.up * .7f + transform.right * .12f, warm * .8f);
            Aim(lowerR, handR, transform.forward * .8f + transform.up * .15f, warm);
            Aim(upperL, lowerL, transform.forward * .45f - transform.up * .7f - transform.right * .12f, warm * .8f);
            Aim(lowerL, handL, transform.forward * .8f + transform.up * .15f, warm);
        }
    }

    private static float Pulse(float time, float period, float start, float end)
    {
        float t = Mathf.Repeat(time, period);
        return Mathf.SmoothStep(0, 1, Mathf.Clamp01((t - start) / 1.6f)) * (1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01((t - end) / 1.6f)));
    }
    private static void Turn(Bone bone, Vector3 axis, float degrees)
    {
        if (bone.transform != null) bone.transform.rotation = Quaternion.AngleAxis(degrees, axis) * bone.transform.rotation;
    }
    private static void Aim(Bone bone, Bone child, Vector3 direction, float weight)
    {
        if (bone.transform == null || child.transform == null || weight <= 0) return;
        var now = child.transform.position - bone.transform.position;
        if (now.sqrMagnitude < 1e-8f) return;
        bone.transform.rotation = Quaternion.FromToRotation(now, Vector3.Slerp(now.normalized, direction.normalized, weight)) * bone.transform.rotation;
    }
    private void Restore()
    {
        if (bones == null) return;
        foreach (var bone in bones)
            if (bone.transform != null && bone.written) { bone.transform.localRotation = bone.sampled; bone.written = false; }
    }
    private void OnDisable() { Restore(); }
}
