using System;
using System.Collections.Generic;
using UnityEngine;

// The single tuning surface for the troll's performance layer: life, mood families, delivery,
// beats, endings, lip-safety caps and preview levels. Face values are blend-shape weights in 0..1
// (Synty A_FacePose keys divided by 100). Angles are degrees in character space.
[CreateAssetMenu(menuName = "Xybrid/Gatekeeper Performance Settings")]
public sealed class GatekeeperPerformanceSettings : ScriptableObject
{
    public enum Family { Neutral, Contempt, Anger, Grumpy, Suspicion, Disgust, Warm }

    [Serializable] public struct LR
    {
        public float left, right;
        public LR(float left, float right) { this.left = left; this.right = right; }
    }

    // Non-mouth shapes only. The mouth owner keeps jaw, mouth, tongue, cheekPuff and cheekHollow.
    [Serializable] public sealed class Face
    {
        public LR browInnerUp, browInnerDown, browOuterUp, browOuterDown, browFrown, browRaise;
        public LR eyeSquint, eyeWideUpper, eyeWideLower, eyeBlinkUpper, eyeBlinkLower;
        public LR noseSneer, cheekSquint;
        public const int Pairs = 13, Channels = Pairs * 2;
        public static readonly string[] Shapes =
        {
            "browInnerUp", "browInnerDown", "browOuterUp", "browOuterDown", "browFrown", "browRaise",
            "eyeSquint", "eyeWideUpper", "eyeWideLower", "eyeBlinkUpper", "eyeBlinkLower", "noseSneer", "cheekSquint"
        };
        // Channel order: shape pair index * 2 + (0 left, 1 right).
        public void AddTo(float[] v, float scale)
        {
            Add(v, 0, browInnerUp, scale); Add(v, 1, browInnerDown, scale); Add(v, 2, browOuterUp, scale);
            Add(v, 3, browOuterDown, scale); Add(v, 4, browFrown, scale); Add(v, 5, browRaise, scale);
            Add(v, 6, eyeSquint, scale); Add(v, 7, eyeWideUpper, scale); Add(v, 8, eyeWideLower, scale);
            Add(v, 9, eyeBlinkUpper, scale); Add(v, 10, eyeBlinkLower, scale); Add(v, 11, noseSneer, scale); Add(v, 12, cheekSquint, scale);
        }
        private static void Add(float[] v, int pair, LR value, float scale) { v[pair * 2] += value.left * scale; v[pair * 2 + 1] += value.right * scale; }
    }

    [Serializable] public sealed class Body
    {
        [Tooltip("Head nod in degrees; + looks down.")] public float headPitch;
        [Tooltip("Head turn in degrees; + turns toward the character's right (camera left).")] public float headYaw;
        [Tooltip("Head tilt in degrees; + tips toward the character's left shoulder.")] public float headRoll;
        [Tooltip("Upper-chest lean in degrees; + leans toward the camera.")] public float lean;
        [Tooltip("Clavicle raise in degrees; + is tense, - relaxed.")] public float shoulderRaise;
        [Tooltip("Clavicles forward in degrees; + hunches.")] public float shoulderForward;
        [Tooltip("Multiplies breaths per minute.")] public float breathRate = 1;
        [Tooltip("Multiplies breathing depth.")] public float breathDepth = 1;
        [Tooltip("Multiplies blink frequency.")] public float blinkRate = 1;
        [Tooltip("Multiplies how often the eyes dart away.")] public float dartRate = 1;
        [Tooltip("Multiplies head accents on loudness peaks.")] public float accentGain = 1;
        [Tooltip("Snarl request (0-1) sent to the mouth owner, which caps it.")] [Range(0, 1)] public float snarl;
        [Tooltip("Ears pinned back (0-1).")] [Range(0, 1)] public float earsBack;
        [Tooltip("Ears drooping (0-1).")] [Range(0, 1)] public float earsDroop;
        public const int Channels = 14;
        // Offsets are 0 at rest; rates (6-10) are multipliers that are 1 at rest.
        public static bool IsRate(int channel) => channel >= 6 && channel <= 10;
        public void Write(float[] v, int offset, float amplitude)
        {
            v[offset] += headPitch * amplitude; v[offset + 1] += headYaw * amplitude; v[offset + 2] += headRoll * amplitude;
            v[offset + 3] += lean * amplitude; v[offset + 4] += shoulderRaise * amplitude; v[offset + 5] += shoulderForward * amplitude;
            v[offset + 6] *= Mathf.LerpUnclamped(1, breathRate, amplitude); v[offset + 7] *= Mathf.LerpUnclamped(1, breathDepth, amplitude);
            v[offset + 8] *= Mathf.LerpUnclamped(1, blinkRate, amplitude); v[offset + 9] *= Mathf.LerpUnclamped(1, dartRate, amplitude);
            v[offset + 10] *= Mathf.LerpUnclamped(1, accentGain, amplitude);
            v[offset + 11] += snarl * amplitude; v[offset + 12] += earsBack * amplitude; v[offset + 13] += earsDroop * amplitude;
        }
        public static Body Of(float pitch, float yaw, float roll, float lean, float raise, float forward, float breathRate, float breathDepth, float blink, float dart, float accent, float snarl, float earsBack, float earsDroop = 0)
            => new Body { headPitch = pitch, headYaw = yaw, headRoll = roll, lean = lean, shoulderRaise = raise, shoulderForward = forward, breathRate = breathRate, breathDepth = breathDepth, blinkRate = blink, dartRate = dart, accentGain = accent, snarl = snarl, earsBack = earsBack, earsDroop = earsDroop };
    }

    [Serializable] public sealed class FamilyPose
    {
        public Family family;
        [Tooltip("Provenance of the face values.")] public string source;
        [Tooltip("Face at full amplitude, or at 'Face Full At' when a stronger face is used.")] public Face face = new Face();
        [Tooltip("Blend on toward a stronger face as intensity rises past 'Face Full At'.")] public bool useStrongFace;
        public Face strongFace = new Face();
        [Range(.05f, .95f)] public float faceFullAt = .5f;
        [Tooltip("Multiplies this family's face (restraint).")] [Range(0, 1.5f)] public float faceScale = 1;
        [Tooltip("Body at full amplitude.")] public Body body = new Body();
    }

    [Serializable] public sealed class EmotionTag
    {
        public string tag;
        public Family family;
        [Tooltip("Extra ear droop for this tag (weariness).")] [Range(0, 1)] public float earDroop;
        public EmotionTag() { }
        public EmotionTag(string tag, Family family, float earDroop = 0) { this.tag = tag; this.family = family; this.earDroop = earDroop; }
    }

    [Serializable] public sealed class LaughDelivery
    {
        public string tag = "laugh";
        [Tooltip("Chest hitch (degrees) at full loudness bounce.")] public float bounceChestDegrees = 2.4f;
        public float bounceShoulderDegrees = 3.5f, bounceHeadDegrees = 1.8f;
        [Tooltip("Head tipped back (degrees) across the laugh line.")] public float headBackDegrees = 8;
        [Range(0, 1)] public float squint = .35f, cheekSquint = .4f, lowerLid = .25f;
        public float envelopeAttackSeconds = .012f, envelopeReleaseSeconds = .08f, rampSeconds = .25f, releaseSeconds = .45f;
    }

    [Serializable] public sealed class RoarDelivery
    {
        public string tag = "roar";
        [Tooltip("Forward lunge of the upper chest and neck (degrees).")] public float lungeDegrees = 8;
        public float lungeAttackSeconds = .16f, lungeReleaseSeconds = .55f;
        [Tooltip("Share of the lunge the head counter-rotates to keep the face on camera.")] [Range(0, 1)] public float headCounter = .65f;
        public float shoulderRaise = 4;
        [Tooltip("Camera shake applied by TrolaAtmosphere.")] public float shakeMeters = .006f, shakeDegrees = .4f, shakeHz = 17;
        [Range(0, 1)] public float eyeWide = .35f, browDown = .35f, snarl = .8f;
    }

    [Serializable] public sealed class QuietDelivery
    {
        public string tag;
        public float leanDegrees = 4, leanSeconds = 1.4f;
        [Tooltip("Upper lids lowered.")] [Range(0, 1)] public float lidLower = .3f;
        [Tooltip("Scales darts, sway, micro motion and weight shifts (near stillness).")] [Range(0, 1)] public float lifeScale = .12f;
        [Range(0, 1)] public float breathDepthScale = .5f, accentScale = .15f, blinkRateScale = .6f;
    }

    [Serializable] public sealed class Ending
    {
        public Family family;
        [Range(0, 1)] public float intensity = .5f;
        [Tooltip("Body added on top of the family pose while settled.")] public Body extra = new Body();
    }

    [Header("Mood")]
    [Tooltip("Crossfade between moods; never snaps.")] public float moodBlendSeconds = .3f;
    [Tooltip("Mood held after a line ends before it releases.")] public float moodHoldSeconds = 1.2f;
    public float moodReleaseSeconds = 1.6f;
    [Tooltip("Share of the last line's mood kept until the next line.")] [Range(0, 1)] public float moodLinger = .35f;
    [Tooltip("Pose amplitude by line intensity (x: intensity, y: amplitude).")]
    public AnimationCurve intensityAmplitude = new AnimationCurve(new Keyframe(0, 0, 1.8f, 1.8f), new Keyframe(.4f, .58f, .9f, .9f), new Keyframe(.7f, .81f, .65f, .65f), new Keyframe(1, 1, .6f, .6f));
    [Range(0, 1.5f)] public float facialScale = 1;
    public List<EmotionTag> emotionTags = DefaultTags();
    public List<FamilyPose> families = DefaultFamilies();

    [Header("Caps that keep the lip sync readable")]
    [Range(0, 1)] public float noseSneerCap = .55f;
    [Range(0, 1)] public float noseSneerSpeakingCap = .22f;
    [Range(0, 1)] public float cheekSquintSpeakingCap = .45f;
    [Tooltip("Additive mouthUpperUp the mouth owner allows while speaking.")] [Range(0, 1)] public float snarlSpeakingCap = .08f;
    [Range(0, 1)] public float snarlSilentCap = .3f;
    public float capBlendSeconds = .15f;

    [Header("Life: blinks")]
    public Vector2 blinkInterval = new Vector2(2.2f, 5.6f);
    public float blinkCloseSeconds = .07f, blinkHoldSeconds = .03f, blinkOpenSeconds = .12f;
    [Range(0, 1)] public float doubleBlinkChance = .18f;
    public float doubleBlinkGap = .09f;
    [Range(0, 1.2f)] public float blinkUpperWeight = 1, blinkLowerWeight = .25f;
    [Tooltip("Gaze shifts larger than this blink (degrees).")] public float gazeShiftBlinkDegrees = 9;
    [Range(0, 1)] public float gazeShiftBlinkChance = .5f;
    [Tooltip("No gaze-shift blink within this many seconds of the last blink.")] public float blinkRefractorySeconds = 1.2f;

    [Header("Life: gaze (eye bones aim at the camera)")]
    [Tooltip("Seconds of eye contact between darts.")] public Vector2 contactSeconds = new Vector2(2.4f, 6f);
    public Vector2 dartSeconds = new Vector2(.25f, .6f);
    public Vector2 dartYaw = new Vector2(4, 11), dartPitch = new Vector2(-6, 3);
    public float saccadeSeconds = .04f;
    public float eyeYawLimit = 28, eyePitchLimit = 20;
    [Tooltip("Share of an eye shift the head follows, late.")] [Range(0, 1)] public float headFollow = .3f;
    public float headFollowSeconds = .22f;
    [Tooltip("Eye angle (degrees) that drives an eyeLook lid-follow shape to its maximum.")] public float lidFollowDegrees = 25;
    [Range(0, 1)] public float lidFollowMax = .6f;

    [Header("Life: breathing, weight and micro motion")]
    public float breathsPerMinute = 13;
    public float breathChestDegrees = 1f, breathShoulderDegrees = .8f;
    [Range(0, 1)] public float breathHeadCounter = .6f;
    public float weightShiftSeconds = 9, weightShiftRollDegrees = .9f, weightShiftYawDegrees = .7f;
    [Range(0, 1)] public float headLevelling = .7f;
    public float microHeadDegrees = .7f, microHeadHz = .23f;
    [Range(0, 1)] public float neckShare = .35f;
    [Tooltip("Share of the idle clip's own sway kept; the rest is low-passed away (its 1.77 s loop reads as mechanical).")] [Range(0, 1)] public float idleMotionScale = .55f;
    public float idleSmoothSeconds = 1.2f;
    [Tooltip("Share of a lean the head counter-rotates to keep looking at the camera.")] [Range(0, 1)] public float leanHeadCounter = .7f;

    [Header("Speech: accents on loudness peaks")]
    public float loudnessHopSeconds = .01f, envelopeAttackSeconds = .02f, envelopeReleaseSeconds = .12f;
    [Range(0, 1)] public float peakFloor = .35f;
    public float peakRise = 1.25f, peakLookbackSeconds = .15f, peakCooldownSeconds = .2f;
    public float accentDegrees = 1.6f, accentAttackSeconds = .06f, accentDecaySeconds = .26f;
    [Tooltip("Accent gain at line intensity 0 (x) and 1 (y).")] public Vector2 accentGainByIntensity = new Vector2(.5f, 1.7f);
    [Range(0, 1)] public float accentBrowRaise = .12f;
    public float speakingMicroScale = 1.3f;

    [Header("Delivery tags")]
    public LaughDelivery laugh = new LaughDelivery();
    public RoarDelivery roar = new RoarDelivery();
    public QuietDelivery quietThreat = new QuietDelivery { tag = "quiet threat", leanDegrees = 5, lidLower = .12f };
    public QuietDelivery mutter = new QuietDelivery { tag = "mutter", leanDegrees = 3, lidLower = .15f, lifeScale = .2f };

    [Header("Waiting for the player (leads into the idle taunt)")]
    public float attentiveSeconds = 1.5f;
    [Range(0, 1)] public float restlessFrom = .3f;
    public float restlessSmoothSeconds = .6f;
    [Range(0, 1)] public float glanceAt = .45f;
    public float glanceYaw = 16, glancePitch = -6, glanceSeconds = 1f;
    [Range(0, 1)] public float glanceHeadFollow = .55f;
    [Range(0, 1)] public float exhaleAt = .62f;
    public float exhaleSeconds = 1.9f, exhaleShoulderDrop = 2.5f, exhaleChestSlump = 1.6f, exhaleHeadDrop = 2.5f;
    [Range(0, 1)] public float exhaleLidLower = .25f;
    public float restlessTiltDegrees = 5, restlessDartBoost = 1.5f;
    [Tooltip("Used when the prompt has no idle taunt.")] public float fallbackIdleSeconds = 8;

    [Header("Patience (0 = none left)")]
    public float patienceNeutral = 3, patienceMaximum = 5, patienceBlendSeconds = .8f;
    public Face lowPatienceFace = new Face { browInnerDown = new LR(.35f, .35f), browFrown = new LR(.2f, .2f), eyeSquint = new LR(.15f, .15f) };
    public Body lowPatienceBody = Body.Of(3, 0, 0, 4, 2.5f, 1, 1.15f, 1.15f, .75f, .5f, 1.1f, .1f, .5f);
    public Body highPatienceBody = Body.Of(-2, 0, 0, -1.5f, -2, -.5f, .9f, 1, 1.1f, 1.2f, 1, 0, 0);
    [Tooltip("At full patience, brow-down and squint channels relax by this share.")] [Range(0, 1)] public float highPatienceRelief = .3f;

    [Header("Interruption: head snaps to the camera, brows down")]
    public float interruptAttackSeconds = .06f, interruptHoldSeconds = .35f, interruptReleaseSeconds = .6f;
    [Range(0, 1)] public float interruptFaceCamera = 1;
    public float interruptHeadDown = 3, interruptStareSeconds = 1.6f;
    public Face interruptFace = new Face { browInnerDown = new LR(.8f, .8f), browFrown = new LR(.6f, .6f), eyeSquint = new LR(.2f, .2f), eyeWideUpper = new LR(.35f, .35f), noseSneer = new LR(.25f, .3f) };
    public float interruptEarFlickDegrees = 16;

    [Header("Name or business repeated: tilt and a narrowed eye")]
    public float repeatLeadSeconds = .12f, repeatSeconds = 1.4f, repeatTiltDegrees = -6;
    public bool repeatNarrowRightEye = true;
    [Range(0, 1)] public float repeatSquint = .45f, repeatLidLower = .3f, repeatBrowDown = .3f;
    public int repeatMinimumLength = 2;

    [Header("Ending settle poses")]
    public float settleSeconds = 1.2f;
    public Ending passage = new Ending { family = Family.Contempt, intensity = .4f, extra = Body.Of(-3, 0, 0, -1, -1.5f, 0, .85f, 1.1f, .9f, 1, 1, 0, 0) };
    public Ending expulsion = new Ending { family = Family.Anger, intensity = .75f, extra = Body.Of(1, 0, 0, 2, 2, 1, 1.4f, 1.6f, .7f, .5f, 1, .2f, .7f) };
    public Ending friendship = new Ending { family = Family.Warm, intensity = .6f, extra = Body.Of(0, 0, 3, 0, -2, -1, .9f, 1, 1.1f, 1.1f, 1, 0, 0) };

    [Header("Ears (skinned dynamic bones)")]
    public string leftEarBone = "earl_dyn_01_01_c", rightEarBone = "earr_dyn_01_01_r";
    public float earBackDegrees = 22, earDroopDegrees = 14;
    public float earFlickHz = 9, earFlickSeconds = .35f;
    public Vector2 earTwitchInterval = new Vector2(6, 14);
    public float earTwitchDegrees = 6;

    [Header("Debug preview and determinism")]
    [Tooltip("Low, medium and high preview intensities.")] public Vector3 previewIntensities = new Vector3(.4f, .7f, 1f);
    public int randomSeed = 431;

    public float Amplitude(float intensity) => Mathf.Max(0, intensityAmplitude.Evaluate(Mathf.Clamp01(intensity)));
    public EmotionTag Tag(string emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion)) return null;
        string key = emotion.Trim();
        return emotionTags.Find(t => t != null && string.Equals(t.tag, key, StringComparison.OrdinalIgnoreCase));
    }
    // Unknown tags fall back to neutral.
    public Family FamilyOf(string emotion) => Tag(emotion)?.family ?? Family.Neutral;
    public FamilyPose Pose(Family family) => family == Family.Neutral ? null : families.Find(p => p != null && p.family == family);
    public QuietDelivery Quiet(string delivery) =>
        Is(delivery, quietThreat.tag) ? quietThreat : Is(delivery, mutter.tag) ? mutter : null;
    public static bool Is(string delivery, string tag) => !string.IsNullOrEmpty(delivery) && string.Equals(delivery.Trim(), tag, StringComparison.OrdinalIgnoreCase);
    public Ending EndingFor(GatekeeperOutcome outcome) =>
        outcome == GatekeeperOutcome.Passed ? passage : outcome == GatekeeperOutcome.Refused ? expulsion : outcome == GatekeeperOutcome.Liked ? friendship : null;

    private static List<EmotionTag> DefaultTags() => new List<EmotionTag>
    {
        new EmotionTag("contempt", Family.Contempt), new EmotionTag("pride", Family.Contempt), new EmotionTag("mockery", Family.Contempt),
        new EmotionTag("anger", Family.Anger), new EmotionTag("menace", Family.Anger), new EmotionTag("resentment", Family.Anger),
        new EmotionTag("irritation", Family.Grumpy), new EmotionTag("annoyance", Family.Grumpy), new EmotionTag("impatience", Family.Grumpy),
        new EmotionTag("weariness", Family.Grumpy, .6f), new EmotionTag("reluctance", Family.Grumpy, .25f),
        new EmotionTag("suspicion", Family.Suspicion), new EmotionTag("distrust", Family.Suspicion), new EmotionTag("interest", Family.Suspicion), new EmotionTag("curiosity", Family.Suspicion),
        new EmotionTag("disgust", Family.Disgust),
        new EmotionTag("amusement", Family.Warm), new EmotionTag("fondness", Family.Warm)
    };

    private static Face F(float iuL, float iuR, float idL, float idR, float ouL, float ouR, float odL, float odR, float frL, float frR, float raL, float raR,
        float sqL, float sqR, float wuL, float wuR, float wlL, float wlR, float buL, float buR, float blL, float blR, float snL, float snR, float csL, float csR)
        => new Face
        {
            browInnerUp = new LR(iuL, iuR), browInnerDown = new LR(idL, idR), browOuterUp = new LR(ouL, ouR), browOuterDown = new LR(odL, odR),
            browFrown = new LR(frL, frR), browRaise = new LR(raL, raR), eyeSquint = new LR(sqL, sqR), eyeWideUpper = new LR(wuL, wuR),
            eyeWideLower = new LR(wlL, wlR), eyeBlinkUpper = new LR(buL, buR), eyeBlinkLower = new LR(blL, blR), noseSneer = new LR(snL, snR), cheekSquint = new LR(csL, csR)
        };

    // Face data starts from Synty A_FacePose clips (brow, lid, noseSneer and cheekSquint keys only), then is tuned
    // for this rig: its brow sits under a painted mask, so lids, gaze-in-head and head pose carry the read.
    private static List<FamilyPose> DefaultFamilies() => new List<FamilyPose>
    {
        new FamilyPose
        {
            family = Family.Contempt, source = "A_FacePose_Smug; right sneer raised, left outer brow raised, right lid heavy and left open (lopsided), chin up",
            //      innerUp    innerDown   outerUp    outerDown  frown      raise      squint      wideUp     wideLow    blinkUp    blinkLow   sneer       cheekSq
            face = F(0, 0,     .68f, .68f, .6f, 0,     0, .25f,   .4f, .04f, .3f, 0,  .4f, .85f, .15f, .1f, .44f, .64f, .2f, .45f, 0, 0,  .05f, 1,   .02f, .35f),
            body = Body.Of(-8, 8, 4, -2, -1, 0, .85f, 1.1f, .6f, .6f, .8f, .45f, .25f)
        },
        new FamilyPose
        {
            family = Family.Anger, source = "Neutral -> A_FacePose_Angry -> A_FacePose_Enraged; head down so the wide eyes glare up", useStrongFace = true, faceFullAt = .4f,
            face = F(0, 0,     .87f, .87f, 0, 0,      .02f, .02f, .19f, .19f, 0, 0, 1.05f, 1.05f, .24f, .42f, .15f, .31f, 0, 0, 0, 0,     .07f, .36f, .39f, .38f),
            strongFace = F(0, 0, 1, 1,     .01f, .01f, 0, 0,   .3f, .34f, 0, 0,  .3f, .3f,   .7f, .8f,  .15f, .31f, 0, 0, 0, 0,     .06f, .34f, .34f, .33f),
            body = Body.Of(7, 0, 0, 5, 4, 2, 1.5f, 1.5f, .5f, .3f, 1.6f, .6f, 1)
        },
        new FamilyPose
        {
            family = Family.Grumpy, source = "A_FacePose_Grumpy; upper lids lowered, looks away often",
            face = F(0, 0,     .64f, .64f, .19f, .19f, 0, 0,   .22f, .39f, 0, 0,  .6f, .5f,  .1f, .1f,  .44f, .64f, .28f, .28f, 0, 0,  .78f, .78f, .32f, .27f),
            body = Body.Of(3, -7, -3, 0, 2.5f, 1.5f, 1.15f, 1.25f, 1.2f, 1.8f, 1.1f, .25f, .4f)
        },
        new FamilyPose
        {
            family = Family.Suspicion, source = "A_FacePose_Suspicious; right eye squinted shut-ish, left eye open under a raised brow, side-eye tilt",
            face = F(.4f, 0,   .3f, .6f,   .3f, 0,    0, 0,     0, .5f,    .7f, 0, .3f, .95f,  .35f, 0,    0, 0,   0, .3f,  0, 0,     .5f, .5f, .32f, .27f),
            body = Body.Of(2, 10, -9, 3, 1, 0, .85f, .9f, .4f, .3f, .7f, 0, 0)
        },
        new FamilyPose
        {
            family = Family.Disgust, source = "A_FacePose_Disgusted; lids lowered, recoil and turn away",
            face = F(.58f, 0,  .32f, .79f, 0, 0,      .37f, .42f, .27f, .03f, 0, 0, .23f, .76f, .2f, .2f,  0, 0,   .2f, .3f,  0, 0,     .31f, 1, .46f, .46f),
            body = Body.Of(-6, -11, 4, -5, 3, -1.5f, 1.1f, 1.3f, 1.3f, 1.2f, 1, .85f, .6f)
        },
        new FamilyPose
        {
            family = Family.Warm, source = "A_FacePose_Content -> A_FacePose_Happy, restrained; lower lids raised, upper lids soft", useStrongFace = true, faceFullAt = .5f, faceScale = .75f,
            face = F(.16f, .16f, 0, 0,    0, 0,      0, 0,   0, 0,      0, 0,   .38f, .38f, .3f, .3f,  0, 0,     0, 0, .15f, .15f,  0, 0,     .32f, .32f),
            strongFace = F(.38f, .38f, 0, 0, 0, 0,   0, 0,   0, 0,      0, 0,   .74f, .74f, .3f, .3f,  0, 0,     0, 0, .3f, .3f,    0, 0,     .68f, .68f),
            body = Body.Of(-3, 3, 7, -1.5f, -2.5f, -1, .9f, 1.1f, 1.2f, 1.1f, 1.2f, 0, 0)
        }
    };
}
