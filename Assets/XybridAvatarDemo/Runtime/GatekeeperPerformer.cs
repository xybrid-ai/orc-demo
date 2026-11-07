using System;
using System.Collections.Generic;
using UnityEngine;

// Everything the troll does besides moving his mouth. Runs after XybridAvatarPlayer has sampled
// the idle and written the mouth, then adds procedural offsets in character space.
// Owns: brow, lid, eyeLook (lid follow), noseSneer and cheekSquint shapes; eye, head, neck,
// upper-chest, clavicle and ear bones; the camera-shake request; the snarl request; and, while he strikes
// (Strike, docs/defeat.md), the right arm and the torso turn, applied last so neither the idle nor the other
// offsets overwrite them.
// Never writes jaw, mouth, tongue, cheekPuff or cheekHollow. Reads only resolved line metadata
// and controller state, so authored and free-form sources perform identically.
[DefaultExecutionOrder(100)]
[DisallowMultipleComponent]
public sealed class GatekeeperPerformer : MonoBehaviour
{
    public GatekeeperPerformanceSettings settings;
    [Tooltip("Trola's strike on a provoked expulsion (docs/defeat.md).")]
    public TrolaStrike.Settings strike = new TrolaStrike.Settings();
    public XybridAvatarPlayer character;
    public GatekeeperController gatekeeper;
    public TrolaAtmosphere atmosphere;
    [Tooltip("Gaze target; defaults to the main camera.")] public Camera viewer;

    private const int FaceN = GatekeeperPerformanceSettings.Face.Channels, BodyN = GatekeeperPerformanceSettings.Body.Channels, PoseN = FaceN + BodyN;
    private const int HeadPitch = FaceN, HeadYaw = FaceN + 1, HeadRoll = FaceN + 2, Lean = FaceN + 3, ShoulderRaise = FaceN + 4, ShoulderForward = FaceN + 5,
        BreathRate = FaceN + 6, BreathDepth = FaceN + 7, BlinkRate = FaceN + 8, DartRate = FaceN + 9, AccentGain = FaceN + 10, SnarlCh = FaceN + 11, EarsBack = FaceN + 12, EarsDroop = FaceN + 13;
    // Left channel of each face pair; right is +1.
    private const int InnerUp = 0, InnerDown = 2, OuterUp = 4, Frown = 8, Squint = 12, WideUpper = 14, BlinkUpper = 18, BlinkLower = 20, Sneer = 22, CheekSquint = 24;
    private static readonly string[] Forbidden = { "mouth", "jaw", "tongue", "cheekPuff", "cheekHollow" };
    private static readonly string[] LookNames = { "eyeLookUpLeft", "eyeLookUpRight", "eyeLookDownLeft", "eyeLookDownRight", "eyeLookInLeft", "eyeLookInRight", "eyeLookOutLeft", "eyeLookOutRight" };

    private sealed class Shape { public SkinnedMeshRenderer mesh; public int index; }
    private sealed class Driven { public Transform t; public Quaternion baseLocal, written, smooth; public bool hasWritten, hasSmooth, idleDamped; }
    private enum SourceKind { Neutral, Line, Linger, Settle, Preview }
    private struct Source { public SourceKind kind; public string emotion; public float intensity; public GatekeeperOutcome outcome; public int serial; }
    private struct Context
    {
        // presenting: a line is being delivered, with audio (speaking) or as a subtitle only.
        public bool speaking, presenting; public float speechTime; public string emotion, delivery; public float intensity; public AudioClip clip;
        public bool waiting; public float waitFraction; public GatekeeperOutcome outcome; public float patience;
    }
    private struct Accent { public float start, amount, yaw, roll; }

    private readonly List<Shape>[] faceShapes = new List<Shape>[FaceN];
    private readonly List<Shape>[] lookShapes = new List<Shape>[8];
    private readonly List<string> owned = new List<string>();
    private readonly List<Driven> driven = new List<Driven>();
    private Driven spine, chest, upperChest, neck, head, eyeL, eyeR, clavL, clavR, earL, earR, armUpper, armLower, armHand;
    private Transform root;
    private Vector3 eyeFwdL, eyeFwdR, headFwd, headUp;
    // The flag must reset with its nonserializable bone, shape and RNG caches.
    [NonSerialized] private bool ready;
    private System.Random rng;
    private float clock;

    private Source current;
    private readonly float[] snapshot = new float[PoseN], mood = new float[PoseN], target = new float[PoseN], work = new float[PoseN], lastLinePose = new float[PoseN];
    private float switchTime, switchSeconds = .3f, lastSpeechEnd;
    private bool hasLastLine, wasPresenting;
    private int serial;
    private GatekeeperScenario.Line lastLine;

    private float lowPatience, highPatience, speakBlend, restless, restlessVelocity;
    private float laughEnv, roarEnv, quietEnv, loudSlow, loudFast;
    private GatekeeperPerformanceSettings.QuietDelivery quietProfile;
    private readonly Dictionary<AudioClip, GatekeeperSpeechLoudness> analyses = new Dictionary<AudioClip, GatekeeperSpeechLoudness>();
    private readonly List<AudioClip> analysisOrder = new List<AudioClip>();
    private readonly List<Accent> accents = new List<Accent>();
    private float lastSpeechTime = -1, onlinePrev, onlinePrev2, onlineLastPeak = -9, outputPeak = .05f;
    private float breathPhase, nextBlink, blinkStart = -9, blinkQueued = -1, nextDart, dartEnd, dartFollow;
    private bool dartActive;
    private float currentDartRate = 1;
    private float eyeYaw, eyePitch, eyeYawTarget, eyePitchTarget, followYaw, followPitch, followYawVel, followPitchVel;
    private bool wasWaiting, glanceDone, exhaleDone;
    private float waitStart, exhaleStart = -99, interruptStart = -99, stareUntil = -99, repeatAt = -99, earFlickStart = -99, nextTwitch, twitchStart = -99, twitchSide = 1;

    // Read-outs for the preview panel and checks.
    public IReadOnlyList<string> OwnedShapes => owned;
    public GatekeeperPerformanceSettings.Family ActiveFamily { get; private set; }
    public float Loudness => loudSlow;
    public float LaughBounce { get; private set; }
    public float RoarLunge => roarEnv;
    public float QuietAmount => quietEnv;
    public float InterruptAmount { get; private set; }
    public float RepeatAmount { get; private set; }
    public float BlinkAmount { get; private set; }
    public float Restlessness => restless;
    public float HeadAccent { get; private set; }
    public Vector3 ShakeOffset { get; private set; }
    public bool EyeContact => !dartActive && Mathf.Abs(eyeYaw) + Mathf.Abs(eyePitch) < .05f;
    public float EyeContactErrorDegrees { get; private set; }
    public int AccentCount { get; private set; }
    public int BlinkCount { get; private set; }
    public int DartCount { get; private set; }
    public int GlanceCount { get; private set; }
    public int ExhaleCount { get; private set; }
    public string MoodSource => current.kind + ":" + current.outcome;
    // The strike in progress, null when none; StrikeImpact is raised the frame the blow lands, after the pose is written.
    public TrolaStrike CurrentStrike { get; private set; }
    public bool Striking => CurrentStrike != null;
    public Transform StrikeHand => armHand?.t;
    public event Action StrikeImpact;
    public float StrikeImpactSeconds => new TrolaStrike(strike).ImpactSeconds;
    private Vector3 strikeTarget;

    // Debug preview: drives the same evaluation without a conversation.
    public bool Previewing { get; private set; }
    public string PreviewEmotion { get; private set; } = "";
    public float PreviewIntensity { get; private set; } = .7f;
    public string PreviewDelivery { get; private set; } = "";
    public bool PreviewSpeaking { get; private set; }
    public float PreviewPatience { get; private set; } = 3;
    public float PreviewWait { get; private set; } = -1;
    public GatekeeperOutcome PreviewOutcome { get; private set; }
    public bool PreviewHoldStill { get; set; }
    private float previewSpeechStart, previewWaitFraction = -1;
    private int previewSerial;

    private void OnEnable()
    {
        if (gatekeeper != null) { gatekeeper.LineStarted += OnLineStarted; gatekeeper.Interrupted += TriggerInterruption; gatekeeper.Restarted += OnRestarted; }
    }

    private void OnDisable()
    {
        if (gatekeeper != null) { gatekeeper.LineStarted -= OnLineStarted; gatekeeper.Interrupted -= TriggerInterruption; gatekeeper.Restarted -= OnRestarted; }
        foreach (var d in driven)
        {
            if (d.t != null && d.hasWritten && Same(d.t.localRotation, d.written)) d.t.localRotation = d.baseLocal;
            d.hasWritten = false;
        }
        if (ready) { for (int c = 0; c < FaceN; c++) Write(faceShapes[c], 0); for (int c = 0; c < 8; c++) Write(lookShapes[c], 0); }
        if (character != null) character.SnarlRequest = 0;
        if (atmosphere != null) atmosphere.SetShake(Vector3.zero, Vector3.zero);
        ShakeOffset = Vector3.zero;
    }

    public bool Initialize()
    {
        if (ready) return true;
        if (settings == null || character == null) return false;
        root = character.transform;
        if (viewer == null) viewer = Camera.main;
        var animator = character.GetComponent<Animator>();
        Transform Bone(HumanBodyBones b, string fallback)
        {
            var t = animator != null && animator.isHuman ? animator.GetBoneTransform(b) : null;
            return t != null ? t : Find(fallback);
        }
        spine = Drive(Bone(HumanBodyBones.Spine, "spine_01"));
        chest = Drive(Bone(HumanBodyBones.Chest, "spine_02"));
        upperChest = Drive(Bone(HumanBodyBones.UpperChest, "spine_03"));
        clavL = Drive(Bone(HumanBodyBones.LeftShoulder, "clavicle_l"));
        clavR = Drive(Bone(HumanBodyBones.RightShoulder, "clavicle_r"));
        neck = Drive(Bone(HumanBodyBones.Neck, "neck_01"));
        head = Drive(Bone(HumanBodyBones.Head, "head"));
        eyeL = Drive(Bone(HumanBodyBones.LeftEye, "eye_l"));
        eyeR = Drive(Bone(HumanBodyBones.RightEye, "eye_r"));
        foreach (var d in driven) d.idleDamped = true;
        // The striking arm: the idle keeps all of its motion; only a strike moves it.
        armUpper = Drive(Bone(HumanBodyBones.RightUpperArm, "upperarm_r"));
        armLower = Drive(Bone(HumanBodyBones.RightLowerArm, "lowerarm_r"));
        armHand = Drive(Bone(HumanBodyBones.RightHand, "hand_r"));
        earL = Drive(Find(settings.leftEarBone));
        earR = Drive(Find(settings.rightEarBone));
        if (head == null) return false;
        headFwd = LocalAxis(head.t, root.forward); headUp = LocalAxis(head.t, root.up);
        if (eyeL != null) eyeFwdL = LocalAxis(eyeL.t, root.forward);
        if (eyeR != null) eyeFwdR = LocalAxis(eyeR.t, root.forward);

        owned.Clear();
        for (int c = 0; c < FaceN; c++) faceShapes[c] = new List<Shape>();
        for (int c = 0; c < 8; c++) lookShapes[c] = new List<Shape>();
        foreach (var renderer in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = renderer.sharedMesh; if (mesh == null) continue;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i); int dot = name.LastIndexOf('.'); if (dot >= 0) name = name.Substring(dot + 1);
                int channel = FaceChannel(name), look = Array.IndexOf(LookNames, name);
                if (channel < 0 && look < 0) continue;
                foreach (string prefix in Forbidden)
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("The performance layer may not own " + name);
                var shape = new Shape { mesh = renderer, index = i };
                if (channel >= 0) faceShapes[channel].Add(shape); else lookShapes[look].Add(shape);
                if (!owned.Contains(name)) owned.Add(name);
            }
        }
        rng = new System.Random(settings.randomSeed);
        Neutral(mood); Neutral(snapshot); Neutral(lastLinePose);
        nextBlink = clock + Range(settings.blinkInterval);
        nextDart = clock + Range(settings.contactSeconds);
        nextTwitch = clock + Range(settings.earTwitchInterval);
        ready = true;
        return true;
    }

    private void LateUpdate()
    {
        if (!ready && !Initialize()) return;
        var s = settings;
        float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0, .1f);
        clock += dt;
        RefreshBase();
        var ctx = ReadContext();
        if (wasPresenting && !ctx.presenting) { lastSpeechEnd = clock; if (hasLastLine) Array.Copy(target, lastLinePose, PoseN); }
        wasPresenting = ctx.presenting;
        UpdateMood(ctx);
        Array.Copy(mood, work, PoseN);

        // Patience: a persistent bias in every state, smoothed so it never snaps.
        float pn = Mathf.Max(.01f, s.patienceNeutral), pmax = Mathf.Max(pn + .01f, s.patienceMaximum), rate = dt / Mathf.Max(.01f, s.patienceBlendSeconds);
        lowPatience = Mathf.MoveTowards(lowPatience, Mathf.Clamp01((pn - ctx.patience) / pn), rate);
        highPatience = Mathf.MoveTowards(highPatience, Mathf.Clamp01((ctx.patience - pn) / (pmax - pn)), rate);
        s.lowPatienceFace.AddTo(work, lowPatience);
        s.lowPatienceBody.Write(work, FaceN, lowPatience);
        s.highPatienceBody.Write(work, FaceN, highPatience);
        float relief = 1 - s.highPatienceRelief * highPatience;
        foreach (int c in new[] { InnerDown, Frown, Squint, Sneer }) { work[c] *= relief; work[c + 1] *= relief; }

        speakBlend = Mathf.MoveTowards(speakBlend, ctx.speaking ? 1 : 0, dt / Mathf.Max(.01f, s.capBlendSeconds));
        UpdateLoudness(ctx, dt);
        UpdateDelivery(ctx, dt);
        float life = Mathf.Lerp(1, quietProfile != null ? quietProfile.lifeScale : 1, quietEnv);
        if (PreviewHoldStill) life = 0;
        DampIdle(s.idleMotionScale * life, dt);

        // Waiting: watch the camera, then grow restless toward the idle taunt.
        if (ctx.waiting && !wasWaiting) { waitStart = clock; glanceDone = exhaleDone = false; }
        wasWaiting = ctx.waiting;
        float restlessTarget = ctx.waiting ? Smooth01((ctx.waitFraction - s.restlessFrom) / Mathf.Max(.01f, 1 - s.restlessFrom)) : 0;
        restless = Mathf.SmoothDamp(restless, restlessTarget, ref restlessVelocity, Mathf.Max(.01f, s.restlessSmoothSeconds), Mathf.Infinity, dt);
        bool attentive = ctx.waiting && clock - waitStart < s.attentiveSeconds;
        if (ctx.waiting && !glanceDone && !attentive && ctx.waitFraction >= s.glanceAt && !PreviewHoldStill)
        {
            glanceDone = true; GlanceCount++;
            StartDart(Sign() * s.glanceYaw, s.glancePitch, s.glanceSeconds, s.glanceHeadFollow);
        }
        if (ctx.waiting && !exhaleDone && ctx.waitFraction >= s.exhaleAt) { exhaleDone = true; exhaleStart = clock; ExhaleCount++; }
        float exhaleT = (clock - exhaleStart) / Mathf.Max(.01f, s.exhaleSeconds);
        float exhale = exhaleT >= 0 && exhaleT <= 1 ? Mathf.Sin(exhaleT * Mathf.PI) : 0;

        // Beats.
        InterruptAmount = Envelope(clock - interruptStart, s.interruptAttackSeconds, s.interruptHoldSeconds, s.interruptReleaseSeconds);
        float repeatT = clock - repeatAt;
        RepeatAmount = repeatT >= 0 && repeatT <= s.repeatSeconds ? Mathf.Min(Smooth01(repeatT / .2f), Smooth01((s.repeatSeconds - repeatT) / .35f)) : 0;

        // Breathing, weight shift and micro motion keep him alive in every state.
        breathPhase += dt * s.breathsPerMinute / 60f * work[BreathRate] * (exhale > 0 ? .4f : 1);
        float breath = .5f - .5f * Mathf.Cos(breathPhase * 2 * Mathf.PI);
        float depth = work[BreathDepth] * Mathf.Lerp(1, quietProfile != null ? quietProfile.breathDepthScale : 1, quietEnv);
        float breathChest = -s.breathChestDegrees * depth * breath;
        float spineRoll = s.weightShiftRollDegrees * Noise(1.3f, clock / Mathf.Max(.1f, s.weightShiftSeconds)) * life;
        float spineYaw = s.weightShiftYawDegrees * Noise(7.9f, clock / Mathf.Max(.1f, s.weightShiftSeconds)) * life;
        float micro = s.microHeadDegrees * life * Mathf.Lerp(1, s.speakingMicroScale, speakBlend);

        // Accents on loudness peaks.
        float accentGain = Mathf.Lerp(s.accentGainByIntensity.x, s.accentGainByIntensity.y, Mathf.Clamp01(ctx.intensity)) * work[AccentGain]
            * Mathf.Lerp(1, quietProfile != null ? quietProfile.accentScale : 1, quietEnv);
        float accentPitch = 0, accentYaw = 0, accentRoll = 0, accentAmount = 0;
        for (int i = accents.Count - 1; i >= 0; i--)
        {
            float t = clock - accents[i].start;
            float shape = t < s.accentAttackSeconds ? Smooth01(t / Mathf.Max(.001f, s.accentAttackSeconds)) : Mathf.Exp(-(t - s.accentAttackSeconds) / Mathf.Max(.01f, s.accentDecaySeconds));
            if (t > s.accentAttackSeconds + 5 * s.accentDecaySeconds) { accents.RemoveAt(i); continue; }
            float a = accents[i].amount * shape;
            accentAmount = Mathf.Max(accentAmount, a);
            accentPitch += s.accentDegrees * accentGain * a; accentYaw += accents[i].yaw * a; accentRoll += accents[i].roll * a;
        }
        HeadAccent = accentPitch;

        // Delivery.
        var laugh = s.laugh; var roar = s.roar;
        LaughBounce = loudFast * laughEnv;
        float lunge = roarEnv * Mathf.Lerp(.6f, 1, loudSlow);
        float quietLean = quietProfile != null ? quietProfile.leanDegrees * Smooth01(quietEnv) : 0;

        float lean = work[Lean] + quietLean + s.exhaleChestSlump * exhale - laugh.bounceChestDegrees * LaughBounce;
        float shoulderRaise = work[ShoulderRaise] + s.breathShoulderDegrees * depth * breath - s.exhaleShoulderDrop * exhale
            + laugh.bounceShoulderDegrees * LaughBounce + roar.shoulderRaise * lunge;
        float headPitch = work[HeadPitch] + accentPitch + micro * Noise(3.1f, clock * s.microHeadHz)
            + s.exhaleHeadDrop * exhale + s.interruptHeadDown * InterruptAmount
            - laugh.bounceHeadDegrees * LaughBounce - laugh.headBackDegrees * Smooth01(laughEnv)
            - lean * s.leanHeadCounter - breathChest * s.breathHeadCounter - lunge * s.roar.lungeDegrees * roar.headCounter;
        float headYaw = work[HeadYaw] + accentYaw + micro * Noise(5.7f, clock * s.microHeadHz * .8f);
        float headRoll = work[HeadRoll] + accentRoll + micro * .6f * Noise(9.2f, clock * s.microHeadHz * .7f)
            + s.restlessTiltDegrees * restless + s.repeatTiltDegrees * RepeatAmount - spineRoll * s.headLevelling;

        // Gaze darts and head follow (eyes lead, head follows late).
        float dartRate = work[DartRate] * life * (1 + s.restlessDartBoost * restless);
        currentDartRate = dartRate;
        bool staring = clock < stareUntil || attentive || PreviewHoldStill;
        if (dartActive && (clock >= dartEnd || staring && clock < stareUntil)) EndDart();
        if (!dartActive && !staring && dartRate > .02f && clock >= nextDart) StartDart(Sign() * Range(s.dartYaw), Range(s.dartPitch), Range(s.dartSeconds), s.headFollow);
        float k = 1 - Mathf.Exp(-dt / Mathf.Max(.005f, s.saccadeSeconds));
        eyeYaw += (eyeYawTarget - eyeYaw) * k; eyePitch += (eyePitchTarget - eyePitch) * k;
        followYaw = Mathf.SmoothDamp(followYaw, eyeYawTarget * dartFollow, ref followYawVel, Mathf.Max(.01f, s.headFollowSeconds), Mathf.Infinity, dt);
        followPitch = Mathf.SmoothDamp(followPitch, eyePitchTarget * dartFollow, ref followPitchVel, Mathf.Max(.01f, s.headFollowSeconds), Mathf.Infinity, dt);
        headYaw += followYaw; headPitch -= followPitch;

        // Bones, parent to child, as rotations about character axes.
        Vector3 right = root.right, up = root.up, forward = root.forward;
        Rotate(spine, forward, spineRoll); Rotate(spine, up, spineYaw);
        Rotate(chest, right, (lean + lunge * roar.lungeDegrees + breathChest) * .4f);
        Rotate(upperChest, right, (lean + lunge * roar.lungeDegrees + breathChest) * .6f);
        Shoulder(clavL, -right, shoulderRaise, work[ShoulderForward]);
        Shoulder(clavR, right, shoulderRaise, work[ShoulderForward]);
        float share = s.neckShare;
        Rotate(neck, right, headPitch * share); Rotate(neck, up, headYaw * share); Rotate(neck, forward, headRoll * share);
        Rotate(head, right, headPitch * (1 - share)); Rotate(head, up, headYaw * (1 - share)); Rotate(head, forward, headRoll * (1 - share));
        Vector3 cameraPosition = viewer != null ? viewer.transform.position : head.t.position + forward * 2;
        float snap = InterruptAmount * s.interruptFaceCamera;
        if (snap > 0)
        {
            var aim = Quaternion.FromToRotation(head.t.rotation * headFwd, (cameraPosition - head.t.position).normalized);
            head.t.rotation = Quaternion.Slerp(Quaternion.identity, aim, snap) * head.t.rotation;
        }
        float lookYawL, lookPitchL, lookYawR, lookPitchR;
        Aim(eyeL, eyeFwdL, cameraPosition, out lookYawL, out lookPitchL);
        Aim(eyeR, eyeFwdR, cameraPosition, out lookYawR, out lookPitchR);
        Ears(work[EarsBack], work[EarsDroop], dt);
        var landed = StrikePose(dt);
        foreach (var d in driven) { d.written = d.t.localRotation; d.hasWritten = true; }

        // Face: mood + patience already in work; add beats, delivery, blinks, then caps.
        s.interruptFace.AddTo(work, InterruptAmount);
        int side = s.repeatNarrowRightEye ? 1 : 0;
        work[Squint + side] += s.repeatSquint * RepeatAmount; work[BlinkUpper + side] += s.repeatLidLower * RepeatAmount; work[InnerDown + side] += s.repeatBrowDown * RepeatAmount;
        AddPair(Squint, laugh.squint * Smooth01(laughEnv) + .25f * LaughBounce); AddPair(CheekSquint, laugh.cheekSquint * Smooth01(laughEnv)); AddPair(BlinkLower, laugh.lowerLid * Smooth01(laughEnv));
        AddPair(WideUpper, roar.eyeWide * roarEnv); AddPair(InnerDown, roar.browDown * roarEnv);
        float lidLower = (quietProfile != null ? quietProfile.lidLower * quietEnv : 0) + s.exhaleLidLower * exhale;
        AddPair(BlinkUpper, lidLower); ScalePair(WideUpper, 1 - Mathf.Clamp01(quietEnv + exhale));
        AddPair(OuterUp, s.accentBrowRaise * Mathf.Clamp01(accentAmount));
        float fury = CurrentStrike != null ? CurrentStrike.ArmWeight : 0;
        AddPair(InnerDown, strike.browDown * fury); AddPair(WideUpper, strike.eyeWide * fury);

        float blink = BlinkValue(ctx, work[BlinkRate] * Mathf.Lerp(1, quietProfile != null ? quietProfile.blinkRateScale : 1, quietEnv));
        BlinkAmount = blink;
        for (int e = 0; e < 2; e++)
        {
            work[BlinkUpper + e] = Mathf.Lerp(work[BlinkUpper + e], Mathf.Max(work[BlinkUpper + e], s.blinkUpperWeight), blink);
            work[BlinkLower + e] = Mathf.Lerp(work[BlinkLower + e], Mathf.Max(work[BlinkLower + e], s.blinkLowerWeight), blink);
            work[WideUpper + e] *= 1 - blink;
        }
        float sneerCap = Mathf.Lerp(s.noseSneerCap, s.noseSneerSpeakingCap, speakBlend), cheekCap = Mathf.Lerp(1, s.cheekSquintSpeakingCap, speakBlend);
        for (int e = 0; e < 2; e++) { work[Sneer + e] = Mathf.Min(work[Sneer + e], sneerCap); work[CheekSquint + e] = Mathf.Min(work[CheekSquint + e], cheekCap); }
        for (int c = 0; c < FaceN; c++) Write(faceShapes[c], Mathf.Clamp(work[c], 0, 1.1f));
        float open = 1 - blink, deg = Mathf.Max(1, s.lidFollowDegrees), max = s.lidFollowMax * open;
        Write(lookShapes[0], max * Mathf.Clamp01(lookPitchL / deg)); Write(lookShapes[1], max * Mathf.Clamp01(lookPitchR / deg));
        Write(lookShapes[2], max * Mathf.Clamp01(-lookPitchL / deg)); Write(lookShapes[3], max * Mathf.Clamp01(-lookPitchR / deg));
        Write(lookShapes[4], max * Mathf.Clamp01(lookYawL / deg)); Write(lookShapes[5], max * Mathf.Clamp01(-lookYawR / deg));
        Write(lookShapes[6], max * Mathf.Clamp01(-lookYawL / deg)); Write(lookShapes[7], max * Mathf.Clamp01(lookYawR / deg));

        // Requests to the owners of the mouth and the camera.
        character.snarlCapSpeaking = s.snarlSpeakingCap; character.snarlCapSilent = s.snarlSilentCap;
        character.SnarlRequest = Mathf.Clamp01(work[SnarlCh] + roar.snarl * roarEnv + strike.snarl * fury);
        float shake = roarEnv * (.35f + .65f * loudSlow);
        ShakeOffset = new Vector3(Noise(11, clock * roar.shakeHz), Noise(13, clock * roar.shakeHz), Noise(17, clock * roar.shakeHz) * .5f) * roar.shakeMeters * shake;
        var shakeEuler = new Vector3(Noise(19, clock * roar.shakeHz), Noise(23, clock * roar.shakeHz), Noise(29, clock * roar.shakeHz)) * roar.shakeDegrees * shake;
        if (atmosphere != null) atmosphere.SetShake(ShakeOffset, shakeEuler);
        if (landed) StrikeImpact?.Invoke(); // After the pose is written, so the camera's recoil meets the fist.
    }

    // Starts the strike: at the viewer as they stand now (the camera moves once it lands). Gaze darts stop; he stares.
    public bool Strike()
    {
        if ((!ready && !Initialize()) || armUpper == null || armLower == null || armHand == null) return false;
        CurrentStrike = new TrolaStrike(strike);
        strikeTarget = viewer != null ? viewer.transform.position : head.t.position + root.forward * 2;
        EndDart(); stareUntil = clock + CurrentStrike.TotalSeconds;
        return true;
    }

    // Torso turn and lean, then the arm aimed bone by bone (upper arm, then forearm) from wherever the idle and the
    // other offsets left it towards the strike's directions, in character axes so the rig's bone axes do not matter.
    // True the frame the blow lands.
    private bool StrikePose(float dt)
    {
        var blow = CurrentStrike;
        if (blow == null) return false;
        var cues = blow.Advance(dt);
        Vector3 right = root.right, up = root.up, forward = root.forward;
        Rotate(chest, up, blow.Twist * .4f); Rotate(upperChest, up, blow.Twist * .6f);
        Rotate(chest, right, blow.Lean * .4f); Rotate(upperChest, right, blow.Lean * .6f);
        float side = Vector3.Dot(armUpper.t.position - root.position, right) >= 0 ? 1 : -1;
        Vector3 toViewer = (strikeTarget - armUpper.t.position).normalized;
        // Cocked: elbow up and out, fist back by the head. Landed: the arm straight at the viewer, coming slightly across.
        Vector3 cockedUpper = (up * .9f - forward * .35f + right * side * .45f).normalized;
        Vector3 cockedLower = (up * .35f - forward * .8f + right * side * .1f).normalized;
        Vector3 landedUpper = (toViewer - right * side * .12f).normalized, landedLower = toViewer;
        Vector3 down = Vector3.Cross(up, landedUpper).normalized;
        Vector3 throughUpper = Quaternion.AngleAxis(blow.Timing.followDrop, down) * landedUpper;
        Vector3 throughLower = Quaternion.AngleAxis(blow.Timing.followDrop * 1.3f, down) * landedLower;
        AimBone(armUpper, armLower.t, Vector3.Slerp(Vector3.Slerp(cockedUpper, landedUpper, blow.Swing), throughUpper, blow.Follow), blow.ArmWeight);
        AimBone(armLower, armHand.t, Vector3.Slerp(Vector3.Slerp(cockedLower, landedLower, blow.Swing), throughLower, blow.Follow), blow.ArmWeight);
        if (blow.Current == TrolaStrike.Stage.Done) CurrentStrike = null;
        return (cues & TrolaStrike.Cue.Impact) != 0;
    }
    // Turns a bone so the direction to its child moves `weight` of the way from where it points now to `direction`.
    private static void AimBone(Driven bone, Transform child, Vector3 direction, float weight)
    {
        if (bone == null || child == null || weight <= 0) return;
        Vector3 now = child.position - bone.t.position;
        if (now.sqrMagnitude < 1e-8f) return;
        now.Normalize();
        bone.t.rotation = Quaternion.FromToRotation(now, Vector3.Slerp(now, direction, Mathf.Clamp01(weight))) * bone.t.rotation;
    }

    private Context ReadContext()
    {
        var ctx = new Context { patience = settings.patienceNeutral, outcome = GatekeeperOutcome.Playing };
        if (Previewing)
        {
            bool real = character.IsSpeaking && character.SpeechTime >= 0;
            ctx.speaking = ctx.presenting = PreviewSpeaking || real;
            ctx.speechTime = real ? character.SpeechTime : clock - previewSpeechStart;
            ctx.clip = real ? character.CurrentClip : null;
            ctx.emotion = PreviewEmotion; ctx.intensity = PreviewIntensity; ctx.delivery = PreviewDelivery;
            ctx.waiting = previewWaitFraction >= 0; ctx.waitFraction = previewWaitFraction;
            ctx.outcome = PreviewOutcome; ctx.patience = PreviewPatience;
            return ctx;
        }
        if (gatekeeper == null) return ctx;
        var line = gatekeeper.CurrentLine;
        ctx.speaking = (gatekeeper.State == GatekeeperController.Phase.Speaking || gatekeeper.IsPreparedReaction) && character.IsSpeaking && character.SpeechTime >= 0 && line != null;
        ctx.presenting = ctx.speaking || gatekeeper.IsSilentLine && line != null;
        ctx.speechTime = character.SpeechTime; ctx.clip = character.CurrentClip;
        if (line != null) { ctx.emotion = line.emotion; ctx.intensity = line.intensity; ctx.delivery = gatekeeper.IsPreparedReaction ? "" : line.delivery; }
        ctx.waiting = gatekeeper.IsWaitingForPlayer;
        float idle = gatekeeper.IdleTauntSeconds > 0 ? gatekeeper.IdleTauntSeconds : settings.fallbackIdleSeconds;
        ctx.waitFraction = ctx.waiting ? gatekeeper.IdleElapsed / Mathf.Max(.1f, idle) : 0;
        ctx.outcome = gatekeeper.Outcome; ctx.patience = gatekeeper.Patience;
        return ctx;
    }

    private void UpdateMood(Context ctx)
    {
        var next = new Source { outcome = ctx.outcome };
        if (Previewing && !(ctx.outcome != GatekeeperOutcome.Playing && !ctx.speaking)) { next.kind = SourceKind.Preview; next.emotion = ctx.emotion; next.intensity = ctx.intensity; next.serial = previewSerial; }
        else if (ctx.presenting) { next.kind = SourceKind.Line; next.emotion = ctx.emotion; next.intensity = ctx.intensity; next.serial = serial; }
        else if (hasLastLine && !Previewing) { next.kind = SourceKind.Linger; next.serial = serial; }
        else next.kind = ctx.outcome != GatekeeperOutcome.Playing ? SourceKind.Settle : SourceKind.Neutral;
        if (next.kind == SourceKind.Line && !hasLastLine) hasLastLine = true;
        bool changed = next.kind != current.kind || next.serial != current.serial || next.outcome != current.outcome
            || next.kind == SourceKind.Preview && (next.emotion != current.emotion || !Mathf.Approximately(next.intensity, current.intensity));
        if (changed)
        {
            Array.Copy(mood, snapshot, PoseN);
            switchTime = clock;
            switchSeconds = next.outcome != current.outcome && !ctx.presenting ? settings.settleSeconds : settings.moodBlendSeconds;
            current = next;
        }
        Evaluate(current, ctx, target);
        if (current.kind == SourceKind.Line) Array.Copy(target, lastLinePose, PoseN);
        float k = Smooth01((clock - switchTime) / Mathf.Max(.01f, switchSeconds));
        for (int i = 0; i < PoseN; i++) mood[i] = Mathf.Lerp(snapshot[i], target[i], k);
        ActiveFamily = current.kind == SourceKind.Line || current.kind == SourceKind.Preview ? settings.FamilyOf(current.emotion) : GatekeeperPerformanceSettings.Family.Neutral;
    }

    private void Evaluate(Source source, Context ctx, float[] dst)
    {
        switch (source.kind)
        {
            case SourceKind.Line: case SourceKind.Preview: EvaluateEmotion(source.emotion, source.intensity, dst); break;
            case SourceKind.Settle: EvaluateBaseline(source.outcome, dst); break;
            case SourceKind.Linger:
                EvaluateBaseline(source.outcome, dst);
                float since = clock - lastSpeechEnd;
                float w = since < settings.moodHoldSeconds ? 1 : Mathf.Lerp(1, settings.moodLinger, Smooth01((since - settings.moodHoldSeconds) / Mathf.Max(.01f, settings.moodReleaseSeconds)));
                for (int i = 0; i < PoseN; i++) dst[i] = Mathf.Lerp(dst[i], lastLinePose[i], w);
                break;
            default: Neutral(dst); break;
        }
    }

    private void EvaluateBaseline(GatekeeperOutcome outcome, float[] dst)
    {
        var ending = settings.EndingFor(outcome);
        if (ending == null) { Neutral(dst); return; }
        EvaluateFamily(ending.family, ending.intensity, 0, dst);
        ending.extra.Write(dst, FaceN, 1);
    }

    private void EvaluateEmotion(string emotion, float intensity, float[] dst)
    {
        var tag = settings.Tag(emotion);
        EvaluateFamily(tag != null ? tag.family : GatekeeperPerformanceSettings.Family.Neutral, intensity, tag != null ? tag.earDroop : 0, dst);
    }

    private void EvaluateFamily(GatekeeperPerformanceSettings.Family family, float intensity, float earDroop, float[] dst)
    {
        Neutral(dst);
        var pose = settings.Pose(family); if (pose == null) return;
        intensity = Mathf.Clamp01(intensity);
        float amp = settings.Amplitude(intensity), scale = pose.faceScale * settings.facialScale;
        if (pose.useStrongFace)
        {
            float full = Mathf.Clamp(pose.faceFullAt, .05f, .95f);
            if (intensity <= full) pose.face.AddTo(dst, scale * intensity / full);
            else { float k = (intensity - full) / (1 - full); pose.face.AddTo(dst, scale * (1 - k)); pose.strongFace.AddTo(dst, scale * k); }
        }
        else pose.face.AddTo(dst, scale * amp);
        pose.body.Write(dst, FaceN, amp);
        dst[EarsDroop] += earDroop * amp;
    }

    private static void Neutral(float[] dst)
    {
        Array.Clear(dst, 0, dst.Length);
        for (int c = 0; c < BodyN; c++) if (GatekeeperPerformanceSettings.Body.IsRate(c)) dst[FaceN + c] = 1;
    }

    private void UpdateLoudness(Context ctx, float dt)
    {
        if (!ctx.speaking) { loudSlow = Mathf.MoveTowards(loudSlow, 0, dt * 4); loudFast = Mathf.MoveTowards(loudFast, 0, dt * 8); lastSpeechTime = -1; return; }
        float t = ctx.speechTime;
        var analysis = Analysis(ctx.clip);
        if (analysis != null)
        {
            loudSlow = analysis.Slow(t); loudFast = analysis.Fast(t);
            for (int i = 0; i < analysis.peakTimes.Count; i++)
                if (analysis.peakTimes[i] > lastSpeechTime && analysis.peakTimes[i] <= t && lastSpeechTime >= 0) AddAccent(analysis.peakStrengths[i]);
        }
        else
        {
            float raw;
            if (ctx.clip != null && character.speech != null)
            {
                // Streamed audio: follow the live output instead of the clip's PCM.
                var buffer = new float[256]; character.speech.GetOutputData(buffer, 0);
                double sum = 0; foreach (float v in buffer) sum += v * v;
                raw = Mathf.Sqrt((float)(sum / buffer.Length)) / Mathf.Max(.05f, character.speech.volume);
                outputPeak = Mathf.Max(outputPeak * .999f, raw); raw = Mathf.Clamp01(raw / outputPeak);
            }
            else raw = Synthetic(t, ctx.delivery);
            loudSlow += (raw - loudSlow) * (1 - Mathf.Exp(-dt / (raw > loudSlow ? settings.envelopeAttackSeconds : settings.envelopeReleaseSeconds)));
            loudFast += (raw - loudFast) * (1 - Mathf.Exp(-dt / (raw > loudFast ? settings.laugh.envelopeAttackSeconds : settings.laugh.envelopeReleaseSeconds)));
            if (onlinePrev >= settings.peakFloor && onlinePrev >= onlinePrev2 && onlinePrev > loudSlow && clock - onlineLastPeak >= settings.peakCooldownSeconds) { AddAccent(onlinePrev); onlineLastPeak = clock; }
            onlinePrev2 = onlinePrev; onlinePrev = loudSlow;
        }
        lastSpeechTime = t;
    }

    // Preview stand-in for speech loudness: syllables, laugh pulses or a sustained roar.
    private static float Synthetic(float t, string delivery)
    {
        float phrase = Mathf.Repeat(t, 3.2f);
        if (phrase > 2.7f) return 0;
        if (GatekeeperPerformanceSettings.Is(delivery, "laugh")) return Mathf.Pow(Mathf.Max(0, Mathf.Sin(2 * Mathf.PI * 4.5f * phrase)), 1.5f);
        if (GatekeeperPerformanceSettings.Is(delivery, "roar")) return .85f + .15f * Mathf.Sin(2 * Mathf.PI * 1.3f * phrase);
        float syllable = Mathf.Max(0, Mathf.Sin(2 * Mathf.PI * 3.6f * phrase + .7f * Mathf.Sin(2 * Mathf.PI * .9f * phrase)));
        bool quiet = GatekeeperPerformanceSettings.Is(delivery, "quiet threat") || GatekeeperPerformanceSettings.Is(delivery, "mutter");
        return quiet ? .3f + .3f * syllable : .35f + .65f * syllable;
    }

    private GatekeeperSpeechLoudness Analysis(AudioClip clip)
    {
        if (clip == null) return null;
        if (analyses.TryGetValue(clip, out var known)) return known;
        var made = GatekeeperSpeechLoudness.Analyze(clip, settings, settings.laugh.envelopeAttackSeconds, settings.laugh.envelopeReleaseSeconds);
        analyses[clip] = made; analysisOrder.Add(clip);
        while (analysisOrder.Count > 16) { analyses.Remove(analysisOrder[0]); analysisOrder.RemoveAt(0); }
        return made;
    }

    private void AddAccent(float strength)
    {
        if (PreviewHoldStill) return;
        accents.Add(new Accent { start = clock, amount = Mathf.Clamp01(strength), yaw = Sign() * .35f, roll = Sign() * .25f });
        AccentCount++;
    }

    private void UpdateDelivery(Context ctx, float dt)
    {
        var s = settings;
        bool laughing = ctx.speaking && GatekeeperPerformanceSettings.Is(ctx.delivery, s.laugh.tag);
        bool roaring = ctx.speaking && GatekeeperPerformanceSettings.Is(ctx.delivery, s.roar.tag);
        var quiet = ctx.speaking ? s.Quiet(ctx.delivery) : null;
        laughEnv = Mathf.MoveTowards(laughEnv, laughing ? 1 : 0, dt / Mathf.Max(.01f, laughing ? s.laugh.rampSeconds : s.laugh.releaseSeconds));
        roarEnv = Mathf.MoveTowards(roarEnv, roaring ? 1 : 0, dt / Mathf.Max(.01f, roaring ? s.roar.lungeAttackSeconds : s.roar.lungeReleaseSeconds));
        if (quiet != null) quietProfile = quiet;
        float quietSeconds = quietProfile != null ? quietProfile.leanSeconds : 1;
        quietEnv = Mathf.MoveTowards(quietEnv, quiet != null ? 1 : 0, dt / Mathf.Max(.01f, quiet != null ? quietSeconds : quietSeconds * .6f));
        if (quietEnv <= 0 && quiet == null) quietProfile = null;
    }

    private float BlinkDuration => settings.blinkCloseSeconds + settings.blinkHoldSeconds + settings.blinkOpenSeconds;
    private bool BlinkBusy => clock - blinkStart < BlinkDuration || blinkQueued >= 0;
    private void StartBlink(bool allowDouble)
    {
        blinkStart = clock; BlinkCount++;
        if (allowDouble && rng.NextDouble() < settings.doubleBlinkChance) blinkQueued = clock + BlinkDuration + settings.doubleBlinkGap;
    }
    private void GazeShiftBlink(float shift)
    {
        var s = settings;
        if (shift >= s.gazeShiftBlinkDegrees && clock - (blinkStart + BlinkDuration) > s.blinkRefractorySeconds && rng.NextDouble() < s.gazeShiftBlinkChance) TriggerBlink();
    }

    private float BlinkValue(Context ctx, float rate)
    {
        var s = settings;
        if (!PreviewHoldStill && clock >= nextBlink) TriggerBlink();
        if (blinkQueued >= 0 && clock >= blinkQueued) { blinkQueued = -1; StartBlink(false); }
        float t = clock - blinkStart, close = Mathf.Max(.01f, s.blinkCloseSeconds), hold = s.blinkHoldSeconds, open = Mathf.Max(.01f, s.blinkOpenSeconds);
        float value = t < 0 ? 0 : t < close ? Smooth01(t / close) : t < close + hold ? 1 : t < close + hold + open ? 1 - Smooth01((t - close - hold) / open) : 0;
        if (clock >= nextBlink - 1e-4f || nextBlink <= blinkStart) nextBlink = blinkStart + close + hold + open + Range(s.blinkInterval) / Mathf.Max(.1f, rate);
        return value;
    }

    private void StartDart(float yaw, float pitch, float seconds, float follow)
    {
        var s = settings;
        GazeShiftBlink(new Vector2(yaw - eyeYawTarget, pitch - eyePitchTarget).magnitude);
        eyeYawTarget = Mathf.Clamp(yaw, -s.eyeYawLimit, s.eyeYawLimit); eyePitchTarget = Mathf.Clamp(pitch, -s.eyePitchLimit, s.eyePitchLimit);
        dartFollow = follow; dartActive = true; dartEnd = clock + seconds; DartCount++;
    }

    private void EndDart()
    {
        var s = settings;
        GazeShiftBlink(new Vector2(eyeYawTarget, eyePitchTarget).magnitude);
        eyeYawTarget = eyePitchTarget = 0; dartActive = false;
        float rate = Mathf.Max(.05f, currentDartRate);
        nextDart = clock + Range(s.contactSeconds) / rate;
    }

    private void Aim(Driven eye, Vector3 localForward, Vector3 cameraPosition, out float yaw, out float pitch)
    {
        yaw = pitch = 0;
        if (eye == null) return;
        var s = settings;
        Vector3 hUp = head.t.rotation * headUp, hFwd = head.t.rotation * headFwd, hRight = Vector3.Cross(hUp, hFwd);
        Vector3 toCamera = (cameraPosition - eye.t.position).normalized;
        Vector3 dir = Quaternion.AngleAxis(eyeYaw, hUp) * Quaternion.AngleAxis(-eyePitch, hRight) * toCamera;
        yaw = Mathf.Clamp(Mathf.Atan2(Vector3.Dot(dir, hRight), Vector3.Dot(dir, hFwd)) * Mathf.Rad2Deg, -s.eyeYawLimit, s.eyeYawLimit);
        pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(Vector3.Dot(dir, hUp), -1, 1)) * Mathf.Rad2Deg, -s.eyePitchLimit, s.eyePitchLimit);
        dir = Quaternion.AngleAxis(yaw, hUp) * Quaternion.AngleAxis(-pitch, hRight) * hFwd;
        eye.t.rotation = Quaternion.FromToRotation(eye.t.rotation * localForward, dir) * eye.t.rotation;
        float error = Vector3.Angle(eye.t.rotation * localForward, toCamera);
        EyeContactErrorDegrees = eye == eyeL ? error : Mathf.Max(EyeContactErrorDegrees, error);
    }

    private void Ears(float back, float droop, float dt)
    {
        if (earL == null && earR == null) return;
        var s = settings;
        float flickT = clock - earFlickStart;
        float flick = flickT >= 0 && flickT < s.earFlickSeconds ? Mathf.Sin(flickT * s.earFlickHz * 2 * Mathf.PI) * Mathf.Exp(-flickT / Mathf.Max(.01f, s.earFlickSeconds * .35f)) * s.interruptEarFlickDegrees : 0;
        if (!PreviewHoldStill && clock >= nextTwitch) { twitchStart = clock; twitchSide = Sign(); nextTwitch = clock + Range(s.earTwitchInterval); }
        float twitchT = clock - twitchStart;
        float twitch = twitchT >= 0 && twitchT < .3f ? Mathf.Sin(twitchT / .3f * Mathf.PI) * s.earTwitchDegrees : 0;
        Vector3 forward = root.forward, up = root.up, right = root.right;
        Ear(earL, -right, back, droop, flick + (twitchSide < 0 ? twitch : 0), forward, up);
        Ear(earR, right, back, droop, flick + (twitchSide > 0 ? twitch : 0), forward, up);
    }

    private void Ear(Driven ear, Vector3 outward, float back, float droop, float extraBack, Vector3 forward, Vector3 up)
    {
        if (ear == null) return;
        var s = settings;
        Rotate(ear, Vector3.Cross(outward, -forward).normalized, s.earBackDegrees * Mathf.Clamp01(back) + extraBack);
        Rotate(ear, Vector3.Cross(outward, -up).normalized, s.earDroopDegrees * Mathf.Clamp01(droop));
    }

    private void Shoulder(Driven clavicle, Vector3 outward, float raise, float forwardDegrees)
    {
        if (clavicle == null) return;
        Rotate(clavicle, Vector3.Cross(outward, root.up).normalized, raise);
        Rotate(clavicle, Vector3.Cross(outward, root.forward).normalized, forwardDegrees);
    }

    private void OnLineStarted(GatekeeperScenario.Line line)
    {
        serial++; lastLine = line; hasLastLine = true; accents.Clear(); lastSpeechTime = -1;
        repeatAt = -99;
        if (line == null || gatekeeper == null || string.IsNullOrEmpty(line.text)) return;
        int index = -1;
        foreach (string slot in new[] { gatekeeper.PlayerName, gatekeeper.MangledName, gatekeeper.Business })
        {
            if (string.IsNullOrEmpty(slot) || slot.Length < settings.repeatMinimumLength) continue;
            int at = line.text.IndexOf(slot, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (index < 0 || at < index)) index = at;
        }
        // The line's audio length, or its on-screen time when it plays as a subtitle only.
        float length = gatekeeper.CurrentLineSeconds;
        if (index >= 0 && length > 0) repeatAt = clock + Mathf.Max(0, index / (float)line.text.Length * length - settings.repeatLeadSeconds);
    }

    private void OnRestarted() { hasLastLine = false; lastLine = null; repeatAt = -99; restless = 0; CurrentStrike = null; stareUntil = -99; }

    public void TriggerInterruption()
    {
        interruptStart = clock; stareUntil = clock + settings.interruptStareSeconds; earFlickStart = clock;
        eyeYawTarget = eyePitchTarget = 0; dartActive = false;
    }
    public void TriggerRepetition() { repeatAt = clock; }
    public void TriggerBlink() { if (ready && !BlinkBusy) StartBlink(true); }
    public void TriggerGlance() { if (!ready) return; GlanceCount++; StartDart(Sign() * settings.glanceYaw, settings.glancePitch, settings.glanceSeconds, settings.glanceHeadFollow); }
    public void TriggerExhale() { exhaleStart = clock; ExhaleCount++; }

    public void BeginPreview()
    {
        if (Previewing) return;
        Previewing = true; previewSerial++;
        if (gatekeeper != null) gatekeeper.Suspend();
    }
    public void EndPreview()
    {
        if (!Previewing) return;
        Previewing = false; PreviewSpeaking = false; PreviewHoldStill = false; previewWaitFraction = -1; PreviewWait = -1; PreviewOutcome = GatekeeperOutcome.Playing;
        if (character != null) character.StopSpeech();
        if (gatekeeper != null) gatekeeper.Resume();
    }
    public void SetPreviewEmotion(string emotion, float intensity) { PreviewEmotion = emotion ?? ""; PreviewIntensity = Mathf.Clamp01(intensity); PreviewOutcome = GatekeeperOutcome.Playing; previewSerial++; }
    public void SetPreviewDelivery(string delivery, bool speaking) { PreviewDelivery = delivery ?? ""; PreviewSpeaking = speaking; previewSpeechStart = clock; previewSerial++; }
    public void SetPreviewPatience(float patience) { PreviewPatience = patience; }
    public void SetPreviewWait(float fraction) { PreviewWait = fraction; previewWaitFraction = fraction; }
    public void SetPreviewOutcome(GatekeeperOutcome outcome) { PreviewOutcome = outcome; previewSerial++; }

    private void RefreshBase()
    {
        // The idle rewrites humanoid bones every frame; bones nothing rewrote are restored, so offsets never accumulate.
        foreach (var d in driven)
        {
            if (d.t == null) continue;
            if (d.hasWritten && Same(d.t.localRotation, d.written)) d.t.localRotation = d.baseLocal;
            d.baseLocal = d.t.localRotation;
        }
    }

    // Keeps the idle's mean posture but only a share of its sway: blends each fresh idle sample toward a
    // low-passed copy of itself. Bones the idle does not animate are unaffected (their base never changes).
    private void DampIdle(float keep, float dt)
    {
        float k = 1 - Mathf.Exp(-dt / Mathf.Max(.05f, settings.idleSmoothSeconds));
        foreach (var d in driven)
        {
            if (d.t == null || !d.idleDamped) continue;
            d.smooth = d.hasSmooth ? Quaternion.Slerp(d.smooth, d.baseLocal, k) : d.baseLocal;
            d.hasSmooth = true;
            d.t.localRotation = Quaternion.Slerp(d.smooth, d.baseLocal, Mathf.Clamp01(keep));
        }
    }

    private Driven Drive(Transform t)
    {
        if (t == null) return null;
        var existing = driven.Find(d => d.t == t);
        if (existing != null) return existing;
        var made = new Driven { t = t, baseLocal = t.localRotation };
        driven.Add(made);
        return made;
    }
    private Transform Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var t in character.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }
    private static Vector3 LocalAxis(Transform t, Vector3 world)
    {
        Vector3 best = Vector3.zero; float score = -2;
        foreach (var axis in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
        {
            float d = Vector3.Dot(t.rotation * axis, world.normalized);
            if (d > score) { score = d; best = axis; }
        }
        return best;
    }
    private static int FaceChannel(string name)
    {
        for (int pair = 0; pair < GatekeeperPerformanceSettings.Face.Pairs; pair++)
        {
            string shape = GatekeeperPerformanceSettings.Face.Shapes[pair];
            if (name == shape + "Left") return pair * 2;
            if (name == shape + "Right") return pair * 2 + 1;
        }
        return -1;
    }
    private static void Rotate(Driven d, Vector3 axis, float degrees)
    {
        if (d == null || Mathf.Abs(degrees) < 1e-5f || axis.sqrMagnitude < 1e-8f) return;
        d.t.rotation = Quaternion.AngleAxis(degrees, axis) * d.t.rotation;
    }
    private static void Write(List<Shape> shapes, float weight) { foreach (var s in shapes) s.mesh.SetBlendShapeWeight(s.index, weight * 100); }
    private void AddPair(int channel, float value) { work[channel] += value; work[channel + 1] += value; }
    private void ScalePair(int channel, float value) { work[channel] *= value; work[channel + 1] *= value; }
    private static bool Same(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    private static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }
    private static float Envelope(float t, float attack, float hold, float release)
    {
        if (t < 0) return 0;
        if (t < attack) return Smooth01(t / Mathf.Max(.001f, attack));
        if (t < attack + hold) return 1;
        return 1 - Smooth01((t - attack - hold) / Mathf.Max(.001f, release));
    }
    private float Noise(float seed, float t) => (Mathf.PerlinNoise(seed * 7.13f + settings.randomSeed * .01f, t) - .5f) * 2;
    private float Range(Vector2 r) => Mathf.Lerp(r.x, r.y, (float)rng.NextDouble());
    private float Sign() => rng.NextDouble() < .5 ? -1 : 1;
}
