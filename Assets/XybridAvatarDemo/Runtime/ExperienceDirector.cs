using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// The experience around Trola's encounter: the road approach (docs/approach.md), entering the camp (docs/entry.md) and
// the provoked defeat (docs/defeat.md). It decides when the encounter starts, walks the player in once it ends Entered,
// has Trola strike and knocks the viewpoint down once it ends Expelled, and hands TrolaAtmosphere the camera
// poses it blends; dialogue, patience and endings stay with GatekeeperController and its line sources, and Vesper's
// line with SpiritDirector. Installs on its own, like SpiritDirector, in a scene with TrolaAtmosphere; never with
// -xybridSelfTest, -xybridSpeechTest, or a self-test or speech validation in the scene. -xybridNoOpening, or a
// controller that does not auto-start an authored encounter, keeps the original immediate start (no approach); the
// walk into the camp still runs.
public sealed class ExperienceDirector : MonoBehaviour
{
    public const string NoOpeningFlag = "-xybridNoOpening";
    private const float MaxStep = 1 / 15f; // A hitch slows the walk rather than jumping it (and its cues) ahead.
    private const float Breath = .35f;     // Trola waits this long after Vesper's warning ends before challenging.
    private const int StepRate = 32000, StepVariants = 4;

    public static ExperienceDirector Instance { get; private set; }

    public RoadApproach.Settings timing = new RoadApproach.Settings();
    [Tooltip("The route in metres from the close-up home, along the road: how far back, how much higher and how far to the side the walk starts, and where it halts.")]
    public float startBack = 12.25f, startRise = .21f, startSide = -.3f, arrivalBack = .95f, arrivalRise = .13f;
    [Tooltip("How far above Trola's eyes the walk first looks (towards the camp), easing down to his face.")]
    public float startLookRise = .5f;
    [Tooltip("Vertical field of view during the walk; 0 keeps the close-up's 60 mm lens, which the set is framed for.")]
    public float walkFieldOfView;
    [Tooltip("Fade from black when an approach starts, and the dip that hides a skip's cut, in seconds.")]
    public float fadeInSeconds = 1, skipFadeSeconds = .35f;
    [Range(0, 1)] public float footstepVolume = .32f;

    [Header("Entering the camp (docs/entry.md)")]
    public CampEntry.Settings entryTiming = new CampEntry.Settings();
    [Tooltip("Which side of Trola the walk passes: 1 his right (screen left in the close-up), -1 his left; and the gap left between his body and the path, in metres.")]
    public float passSide = 1, passClearance = .8f;
    [Tooltip("Eye height of the walk above Trola's feet, and how far before and after the gate's centre the path runs straight through it, in metres.")]
    public float entryEyeHeight = 1.45f, beforeGate = 1.6f, throughGate = 1f;
    [Tooltip("Where the gaze settles: this far past the gate, at this height.")]
    public float campLookDistance = 7, campLookHeight = 1.3f;
    [Tooltip("Vertical field of view of the walk in; 0 keeps the close-up's lens.")]
    public float entryFieldOfView;
    [Tooltip("A beat after his send-off before the walk starts, and the dialogue's fade as it starts, in seconds.")]
    public float entryDelay = .2f, interfaceFadeSeconds = .4f;
    [TextArea] public string cardTitle = "Inside the camp";
    [TextArea] public string cardPassed = "You paid Trola's toll and walked through his gate.";
    [TextArea] public string cardLiked = "Trola laughed you through his gate. His fire is waiting.";

    [Header("Provoked defeat (docs/defeat.md)")]
    public Knockdown.Settings defeatTiming = new Knockdown.Settings();
    [Tooltip("A beat after his last line before he strikes, in seconds.")]
    public float strikeDelay = .25f;
    [Tooltip("The picture dips this far to black as the blow lands, recovering over impactRecover seconds to a dazed level.")]
    [Range(0, 1)] public float impactDip = .55f, dazed = .18f;
    public float impactRecover = .7f;
    [Range(0, 1)] public float impactVolume = 1;
    [TextArea] public string cardDefeatTitle = "Thrown out";
    [TextArea] public string cardDefeat = "You pushed Trola too far.";

    public GatekeeperController Controller { get; private set; }
    public RoadApproach Approach { get; private set; }
    public bool IsApproaching => Approach != null;
    // Plays the road approach at the start and on Try again; false with -xybridNoOpening (the walk in still runs).
    public bool Opens { get; private set; }
    public CampEntry Entry { get; private set; }
    public bool IsEntering => Entry != null;
    public Knockdown Defeat { get; private set; }
    public bool IsDefeated => Defeat != null;
    // The encounter ended Entered (the walk in) or Expelled (the strike and the fall), and this director plays it out and
    // shows the end card with its own Try again (GatekeeperView then hides its own). A calm refusal is the view's, and
    // so is everything once the director has failed.
    public bool OwnsEnding => !broken && Controller != null && Controller.State == GatekeeperController.Phase.Ended
        && (Controller.Ending == GatekeeperEnding.Entered || Controller.Ending == GatekeeperEnding.Expelled);
    public bool CardShown => card != null && card.activeSelf;
    public Button TryAgainButton => cardButton;
    // Space or Skip opening, until Trola's challenge starts; the settle that follows is never cut while he speaks.
    public bool CanSkip => Approach != null && !Approach.Raised(RoadApproach.Cue.Challenge);
    public int Approaches { get; private set; }
    public float FadeAlpha => fadeAlpha;
    public Button SkipButton => skipButton;
    // One line for F4 and the session dump.
    public static string Describe()
    {
        var director = Instance;
        if (director == null) return "Opening: off (immediate start)";
        if (director.broken) return "Opening: off for this session after an error";
        var defeat = director.Defeat;
        if (defeat != null) return "Knocked down: " + defeat.Current + " " + defeat.StageSeconds.ToString("0.0") + " s";
        var entry = director.Entry;
        if (entry != null)
            return "Entering the camp: " + entry.Current + " " + entry.StageSeconds.ToString("0.0") + " s; " + entry.Length.ToString("0.0") + " m at "
                + entry.CruiseSpeed.ToString("0.0") + " m/s";
        if (!director.Opens) return "Opening: off (-xybridNoOpening or no authored auto-start); entering the camp on";
        var approach = director.Approach;
        if (approach == null) return "Opening: done (" + director.Approaches + " this session; Try again replays it)";
        var spirit = SpiritDirector.Instance;
        return "Opening: " + approach.Current + " " + approach.StageSeconds.ToString("0.0") + " s; Trola's voice "
            + (director.Controller.OpeningVoiceSettled ? "ready" : "preparing") + ", Vesper's " + (spirit == null ? "absent" : spirit.ApproachReady ? "ready" : "preparing")
            + (approach.Skipped ? "; skipped" : "");
    }

    private TrolaAtmosphere atmosphere;
    private GatekeeperView view;
    private GatekeeperBarrier barrier;
    private Canvas fadeCanvas;
    private Image fade;
    private float fadeAlpha, fadeTarget, fadeSeconds = 1;
    private CanvasGroup dialogue; // On the view's canvas: the dialogue fades out as the walk in starts.
    private float dialogueAlpha = 1, dialogueTarget = 1;
    private GameObject card;
    private CanvasGroup cardGroup;
    private Text cardHeading, cardLine;
    private Button cardButton;
    private bool endingRan, landed;
    private float endingWait;
    private GatekeeperPerformer performer;
    private AudioClip thud;
    private Button skipButton;
    private AudioSource steps;
    private AudioClip[] stepClips;
    private int stepIndex;
    private bool begun, broken;
    private float quietFor;
    private readonly XorShift32 dice = new XorShift32((uint)Environment.TickCount);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetPlaySession() { Instance = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (HasArgument("-xybridSelfTest") || HasArgument("-xybridSpeechTest")) return;
        var controller = FindAnyObjectByType<GatekeeperController>();
        var atmosphere = FindAnyObjectByType<TrolaAtmosphere>();
        if (controller == null || controller.scenario == null) return;
        if (atmosphere == null || atmosphere.portraitCamera == null || atmosphere.actor == null || UnderTest()) return;
        // After every Awake, before the controller's Start: the encounter now begins at the gate, not 0.5 s into Play.
        bool opens = !HasArgument(NoOpeningFlag) && controller.autoStart && controller.scenario.authoredConversation;
        if (opens) controller.autoStart = false;
        var director = new GameObject("Experience director (runtime)").AddComponent<ExperienceDirector>();
        director.Controller = controller; director.atmosphere = atmosphere; director.Opens = opens;
    }
    private static bool HasArgument(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    private static bool UnderTest() => FindAnyObjectByType<GatekeeperSelfTest>() != null || FindAnyObjectByType<GatekeeperSpeechValidation>() != null;

    private void Awake() { Instance = this; }

    private void Start()
    {
        view = FindAnyObjectByType<GatekeeperView>();
        barrier = FindAnyObjectByType<GatekeeperBarrier>();
        if (Controller != null) Controller.Restarted += OnRestarted;
        try
        {
            BuildFade(); BuildFootsteps();
            if (Opens && !BeginApproach()) throw new InvalidOperationException("the encounter could not be prepared");
        }
        catch (Exception error) { Fallback(error); }
    }

    // Plays (or replays) the approach: a new encounter is prepared and waits at the gate. False when it cannot run
    // (no opening, suspended, broken, or the scene is invalid); Try again then restarts the encounter directly.
    public bool BeginApproach()
    {
        if (!Opens || broken || Controller == null || Controller.IsSuspended) return false;
        EndApproach();
        if (!Controller.PrepareOpening()) return false; // Raises Restarted: input, Vesper, choices and timers reset.
        begun = true;
        Approach = new RoadApproach(timing, BuildRoute());
        Approaches++; quietFor = 0;
        SpiritDirector.Instance?.PrepareApproach();
        if (barrier != null) barrier.Settle();
        Deselect();
        FadeFromBlack(fadeInSeconds);
        ApplyPose();
        return true;
    }
    public bool Replay() => BeginApproach();

    // Space / Skip opening: cancels the walk's cues and cuts, through a short dip to black, to the close-up as Trola
    // challenges. Only before the challenge (CanSkip).
    public void Skip()
    {
        if (!CanSkip) return;
        try
        {
            StopSteps();
            Handle(Approach.Skip());
            FadeFromBlack(skipFadeSeconds);
            EndApproach();
        }
        catch (Exception error) { Fallback(error); }
    }

    private void Update()
    {
        if (Approach != null)
        {
            try { Tick(); }
            catch (Exception error) { Fallback(error); }
        }
        try
        {
            // His send-off has been said and the encounter ended Entered: after a beat, walk in. Expelled: after a beat, he
            // strikes.
            if (Entry == null && Defeat == null && !endingRan && OwnsEnding)
            {
                endingWait += Time.unscaledDeltaTime;
                bool expelled = Controller.Ending == GatekeeperEnding.Expelled;
                if (endingWait >= (expelled ? strikeDelay : entryDelay)) { if (expelled) BeginDefeat(); else BeginEntry(); }
            }
            if (Entry != null) TickEntry();
            if (Defeat != null) TickDefeat();
        }
        catch (Exception error) { Fallback(error); }
        RefreshSkip();
        AnimateFade();
        AnimateDialogue();
        AnimateCard();
    }

    private void Tick()
    {
        // Someone else started or stopped the encounter (a debug restart, jump or suspension): stand down at the close-up.
        if (!Approach.Raised(RoadApproach.Cue.Challenge) && !Controller.IsOpening) { EndApproach(); return; }
        var keyboard = Keyboard.current;
        if (CanSkip && keyboard != null && keyboard.spaceKey.wasPressedThisFrame && !Typing()) { Skip(); return; }
        var spirit = SpiritDirector.Instance;
        bool ready = Controller.OpeningVoiceSettled && (spirit == null || spirit.ApproachReady);
        float step = Mathf.Min(Time.unscaledDeltaTime, MaxStep);
        quietFor = spirit != null && spirit.IsWarning ? 0 : quietFor + step; // She finishes her warning before he speaks.
        Handle(Approach.Advance(step, ready, quietFor >= Breath));
        if (Approach == null) return;
        ApplyPose();
        if (Approach.Current == RoadApproach.Stage.Done) EndApproach();
    }

    private void Handle(RoadApproach.Cue cues)
    {
        var spirit = SpiritDirector.Instance;
        if ((cues & RoadApproach.Cue.Footstep) != 0) Step(Approach.Pace);
        if ((cues & RoadApproach.Cue.Warning) != 0) spirit?.SayApproach();
        if ((cues & RoadApproach.Cue.Challenge) != 0)
        {
            spirit?.EndApproach(Approach.Skipped); // She is never heard over him.
            Deselect(); // A button still selected from a click (Try again, Skip) must not take a Submit while he speaks.
            Controller.StartOpening();
        }
        if ((cues & RoadApproach.Cue.Settled) != 0) Controller.FinishOpening(); // Answers go live in the close-up.
    }

    private void ApplyPose()
    {
        var pose = Approach.Evaluate();
        atmosphere.SetCinematicPose(pose.position, pose.rotation, pose.fieldOfView, Approach.CinematicWeight);
    }

    private void EndApproach()
    {
        if (Approach == null) return;
        Approach = null;
        if (atmosphere != null) atmosphere.ClearCinematicPose();
        StopSteps();
        SpiritDirector.Instance?.EndApproach(true);
        if (Controller != null) Controller.FinishOpening(); // No-op unless the challenge started and the settle was cut short.
    }

    // ---------------------------------------------------------------- Entering the camp (docs/entry.md)
    // From the close-up as it stands (no cut): around Trola on his passSide, straight through the open gate, the dialogue
    // fading as the walk starts and the picture going black as the gate is crossed; then the end card.
    private void BeginEntry()
    {
        endingRan = true;
        SpiritDirector.Instance?.InterruptAdvice();
        Deselect(); // The Enter camp button must not take a Submit meanwhile.
        Entry = new CampEntry(entryTiming, BuildEntryRoute());
        dialogueTarget = 0;
        ApplyEntryPose();
    }

    private void TickEntry()
    {
        var cues = Entry.Advance(Mathf.Min(Time.unscaledDeltaTime, MaxStep));
        if ((cues & CampEntry.Cue.Footstep) != 0) Step(Entry.Pace);
        if ((cues & CampEntry.Cue.FadeOut) != 0) FadeToBlack(Entry.FadeSeconds);
        if ((cues & CampEntry.Cue.Card) != 0)
            ShowCard(cardTitle, Controller != null && Controller.Outcome == GatekeeperOutcome.Liked ? cardLiked : cardPassed);
        ApplyEntryPose(); // Renewed under the card too: the camera stays inside until Try again.
    }

    private void ApplyEntryPose()
    {
        var pose = Entry.Evaluate();
        atmosphere.SetCinematicPose(pose.position, pose.rotation, pose.fieldOfView, 1, pose.focusDistance);
    }

    // The end card's Try again: replays the approach, or restarts at the gate without one (the doors snap shut under the
    // black, as for an approach).
    public void TryAgain()
    {
        if (!BeginApproach() && Controller != null && Controller.Restart() && barrier != null) barrier.Settle();
    }

    // A new encounter (Try again, a debug restart or jump) drops the walk or the fall, the card and the black.
    private void OnRestarted()
    {
        bool black = fadeTarget >= 1 || Entry != null || Defeat != null;
        EndEntry(); EndDefeat();
        endingRan = false; endingWait = 0;
        if (black) FadeFromBlack(fadeInSeconds);
    }

    // ---------------------------------------------------------------- Provoked defeat (docs/defeat.md)
    // His last line said, Trola strikes (GatekeeperPerformer); the camera holds the close-up through the wind-up and
    // takes over the frame the blow lands: recoil, fall, Vesper's reaction from the ground, then black and the card.
    private void BeginDefeat()
    {
        endingRan = true; landed = false;
        Deselect();
        if (performer == null) performer = FindAnyObjectByType<GatekeeperPerformer>();
        float fallback = defeatTiming.impactSeconds;
        if (performer != null)
        {
            performer.StrikeImpact -= OnStrikeImpact; performer.StrikeImpact += OnStrikeImpact;
            if (performer.Strike()) fallback = performer.StrikeImpactSeconds + .4f; // Its impact leads; the timer only covers a lost event.
        }
        Defeat = new Knockdown(defeatTiming, fallback);
        SpiritDirector.Instance?.PrepareReaction();
    }
    private void OnStrikeImpact() { landed = true; }

    private void TickDefeat()
    {
        var spirit = SpiritDirector.Instance;
        bool quiet = spirit == null || !spirit.IsReacting;
        var cues = Defeat.Advance(Mathf.Min(Time.unscaledDeltaTime, MaxStep), landed, quiet, BuildDefeatRoute);
        landed = false;
        if ((cues & Knockdown.Cue.Impact) != 0)
        {
            CampSoundscape.Instance?.Strike();
            Thud(impactVolume, .85f);
            Dip(impactDip, dazed, impactRecover);
            dialogueTarget = 0;
        }
        if ((cues & Knockdown.Cue.Landed) != 0) Thud(impactVolume * .5f, 1.3f);
        if ((cues & Knockdown.Cue.React) != 0) spirit?.SayReaction();
        if ((cues & Knockdown.Cue.FadeOut) != 0) FadeToBlack(defeatTiming.fadeSeconds);
        if ((cues & Knockdown.Cue.Card) != 0) ShowCard(cardDefeatTitle, cardDefeat);
        if (Defeat.HasRoute)
        {
            var pose = Defeat.Evaluate();
            atmosphere.SetCinematicPose(pose.position, pose.rotation, pose.fieldOfView, Defeat.CinematicWeight, pose.focusDistance);
        }
    }

    private void EndDefeat()
    {
        if (Defeat != null && Defeat.HasRoute && atmosphere != null) atmosphere.ClearCinematicPose();
        Defeat = null; landed = false;
        if (performer != null) performer.StrikeImpact -= OnStrikeImpact;
        if (card != null) card.SetActive(false);
        RestoreDialogue();
    }

    // From the camera as the blow lands, away from his face along the ground.
    private Knockdown.Route BuildDefeatRoute()
    {
        var lens = atmosphere.portraitCamera.transform;
        Vector3 feet = atmosphere.actor.position, face = feet + atmosphere.lookOffset;
        Vector3 back = lens.position - face; back.y = 0;
        back = back.sqrMagnitude > 1e-4f ? back.normalized : -lens.forward;
        return new Knockdown.Route
        {
            start = lens.position, startRotation = lens.rotation, back = back, side = Vector3.Cross(Vector3.up, back).normalized,
            face = face, floor = feet.y, fieldOfView = atmosphere.portraitCamera.fieldOfView
        };
    }

    private void Thud(float volume, float pitch)
    {
        if (steps == null || thud == null || volume <= 0) return;
        steps.pitch = pitch;
        steps.PlayOneShot(thud, Mathf.Clamp01(volume));
    }

    private void EndEntry()
    {
        if (Entry != null && atmosphere != null) atmosphere.ClearCinematicPose();
        Entry = null;
        StopSteps();
        if (card != null) card.SetActive(false);
        RestoreDialogue();
    }

    private CampEntry.Route BuildEntryRoute()
    {
        var lens = atmosphere.portraitCamera.transform;
        Vector3 start = lens.position, feet = atmosphere.actor.position, face = feet + atmosphere.lookOffset;
        Vector3 inward = face - start; inward.y = 0; // Fallback: on through Trola, away from the road.
        inward = inward.sqrMagnitude > 1e-4f ? inward.normalized : Vector3.forward;
        Vector3 gate = feet + inward * 6;
        if (barrier != null && barrier.hinge != null && barrier.secondHinge != null)
        {
            gate = (barrier.hinge.position + barrier.secondHinge.position) / 2; // The doors hinge at either side of the opening.
            var through = gate - feet; through.y = 0;
            if (through.sqrMagnitude > 1e-4f) inward = through.normalized;
        }
        gate.y = feet.y;
        Vector3 side = Vector3.Cross(inward, Vector3.up).normalized * (passSide < 0 ? -1 : 1);
        // Trola's half-width towards that side, from his renderers, within reason (a bad bound must not send the walk away).
        var body = ActorBounds(feet);
        float half = Mathf.Clamp(Mathf.Abs(side.x) * body.extents.x + Mathf.Abs(side.z) * body.extents.z, .35f, 1.6f);
        Vector3 centre = new Vector3(body.center.x, feet.y, body.center.z);
        float eye = feet.y + entryEyeHeight;
        Vector3 beside = centre + side * (half + passClearance); beside.y = Mathf.Lerp(start.y, eye, .6f);
        Vector3 before = gate - inward * beforeGate; before.y = eye;
        Vector3 inside = gate + inward * throughGate; inside.y = eye;
        Vector3 settle = gate + inward * campLookDistance; settle.y = feet.y + campLookHeight;
        return new CampEntry.Route
        {
            points = new[] { start, beside, before, inside },
            lookFrom = start + lens.forward * Mathf.Max(.5f, Vector3.Distance(start, face)), lookTo = settle,
            fieldOfView = entryFieldOfView > 0 ? entryFieldOfView : atmosphere.HomeFieldOfView
        };
    }
    public Bounds ActorBounds(Vector3 feet)
    {
        bool any = false; var bounds = new Bounds(feet + Vector3.up, new Vector3(1, 2, 1));
        foreach (var part in atmosphere.actor.GetComponentsInChildren<Renderer>())
        {
            if (!part.enabled || !part.gameObject.activeInHierarchy || part is ParticleSystemRenderer) continue;
            if (!any) { bounds = part.bounds; any = true; } else bounds.Encapsulate(part.bounds);
        }
        return bounds;
    }

    // The dialogue canvas (subtitle, reply field, buttons) fades out as the walk starts and comes back on restart.
    private void AnimateDialogue()
    {
        if (dialogueAlpha == dialogueTarget) return;
        if (dialogue == null && view != null && !view.TryGetComponent(out dialogue)) dialogue = view.gameObject.AddComponent<CanvasGroup>();
        dialogueAlpha = Mathf.MoveTowards(dialogueAlpha, dialogueTarget, Time.unscaledDeltaTime / Mathf.Max(.01f, interfaceFadeSeconds));
        if (dialogue != null) { dialogue.alpha = dialogueAlpha; dialogue.interactable = dialogue.blocksRaycasts = dialogueAlpha > .5f; }
    }
    private void RestoreDialogue()
    {
        dialogueAlpha = dialogueTarget = 1;
        if (dialogue != null) { dialogue.alpha = 1; dialogue.interactable = dialogue.blocksRaycasts = true; }
    }

    // The end card, over the black: where the player ended up and Try again. Built once on the fade canvas, from the
    // view's font and a copy of its Send button (like Skip opening).
    private void ShowCard(string title, string line)
    {
        if (card == null) BuildCard();
        cardHeading.text = title;
        cardLine.text = line;
        cardGroup.alpha = 0;
        card.SetActive(true);
    }
    private void AnimateCard()
    {
        if (card == null || !card.activeSelf || cardGroup.alpha >= 1) return;
        cardGroup.alpha = Mathf.MoveTowards(cardGroup.alpha, 1, Time.unscaledDeltaTime / .5f);
        cardGroup.interactable = cardGroup.blocksRaycasts = cardGroup.alpha > .5f;
    }
    private void BuildCard()
    {
        var host = fadeCanvas.gameObject;
        if (host.GetComponent<GraphicRaycaster>() == null) host.AddComponent<GraphicRaycaster>(); // The card's button takes clicks.
        if (host.GetComponent<CanvasScaler>() == null)
        {
            var scaler = host.AddComponent<CanvasScaler>(); // Sized like the dialogue canvas.
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        }
        card = new GameObject("End card", typeof(RectTransform)) { layer = 5 };
        var rect = (RectTransform)card.transform;
        rect.SetParent(host.transform, false);
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        cardGroup = card.AddComponent<CanvasGroup>();
        var font = view != null && view.sentence != null ? view.sentence.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        cardHeading = CardText("Heading", font, 52, new Color(.93f, .89f, .8f), .57f);
        cardLine = CardText("Line", font, 26, new Color(.8f, .76f, .68f), .49f);
        if (view != null && view.send != null) cardButton = Instantiate(view.send, card.transform);
        else cardButton = new GameObject("Try again", typeof(RectTransform), typeof(Image), typeof(Button)).GetComponent<Button>();
        cardButton.name = "Try again";
        cardButton.transform.SetParent(card.transform, false);
        cardButton.onClick.RemoveAllListeners(); cardButton.onClick.AddListener(TryAgain);
        var button = cardButton.GetComponent<RectTransform>();
        button.anchorMin = button.anchorMax = new Vector2(.5f, .38f); button.pivot = new Vector2(.5f, .5f);
        button.anchoredPosition = Vector2.zero; button.sizeDelta = new Vector2(220, 48);
        var label = cardButton.GetComponentInChildren<Text>(true);
        if (label != null) { label.text = "Try again"; label.font = font; label.fontSize = 24; label.supportRichText = false; label.horizontalOverflow = HorizontalWrapMode.Overflow; }
        cardButton.interactable = true; cardButton.gameObject.SetActive(true);
        card.SetActive(false);
    }
    private Text CardText(string name, Font font, int size, Color color, float height)
    {
        var line = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)) { layer = 5 };
        var rect = (RectTransform)line.transform;
        rect.SetParent(card.transform, false);
        rect.anchorMin = new Vector2(.1f, height); rect.anchorMax = new Vector2(.9f, height); rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(0, size * 1.6f);
        var text = line.GetComponent<Text>();
        text.font = font; text.fontSize = size; text.color = color; text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = false; text.raycastTarget = false;
        return text;
    }

    // Space typed into the reply field is text, not a skip (the field is hidden before the challenge; this keeps it so).
    private bool Typing() => view != null && view.input != null && view.input.isFocused;

    private RoadApproach.Route BuildRoute()
    {
        Vector3 home = atmosphere.HomePosition, face = atmosphere.actor.position + atmosphere.lookOffset;
        Vector3 back = home - face; back.y = 0; // Along the road, away from the gate.
        back = back.sqrMagnitude > 1e-4f ? back.normalized : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, back);
        Vector3 start = home + back * startBack + Vector3.up * startRise + side * startSide;
        Vector3 arrival = home + back * arrivalBack + Vector3.up * arrivalRise;
        return new RoadApproach.Route
        {
            start = start, control = Vector3.Lerp(start, arrival, .5f), arrival = arrival,
            lookFrom = face + Vector3.up * startLookRise, lookTo = face,
            fieldOfView = walkFieldOfView > 0 ? walkFieldOfView : atmosphere.HomeFieldOfView
        };
    }

    private static void Deselect() { if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null); }

    private void BuildFade()
    {
        var host = new GameObject("Approach fade", typeof(RectTransform)) { layer = 5 };
        host.transform.SetParent(transform, false);
        fadeCanvas = host.AddComponent<Canvas>();
        fadeCanvas.renderMode = RenderMode.ScreenSpaceOverlay; fadeCanvas.sortingOrder = 500;
        var black = new GameObject("Black", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)) { layer = 5 };
        var rect = (RectTransform)black.transform;
        rect.SetParent(host.transform, false);
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        fade = black.GetComponent<Image>(); fade.raycastTarget = false; // Skip stays clickable through the fade.
        Paint();
    }
    private void FadeFromBlack(float seconds) { fadeAlpha = 1; fadeTarget = 0; fadeSeconds = Mathf.Max(.01f, seconds); Paint(); }
    private void FadeToBlack(float seconds) { fadeTarget = 1; fadeSeconds = Mathf.Max(.01f, seconds); }
    // At once to `alpha`, then to `to` over `seconds` (the blow's dip, recovering to a daze).
    private void Dip(float alpha, float to, float seconds) { fadeAlpha = Mathf.Max(fadeAlpha, alpha); fadeTarget = to; fadeSeconds = Mathf.Max(.01f, seconds); Paint(); }
    private void AnimateFade()
    {
        if (fadeAlpha == fadeTarget) return;
        fadeAlpha = Mathf.MoveTowards(fadeAlpha, fadeTarget, Mathf.Min(Time.unscaledDeltaTime, MaxStep) / fadeSeconds);
        Paint();
    }
    private void Paint()
    {
        if (fade == null) return;
        fade.color = new Color(0, 0, 0, fadeAlpha * fadeAlpha * (3 - 2 * fadeAlpha));
        fadeCanvas.enabled = fadeAlpha > 0;
    }

    // Same construction as the Hints button: a copy of the view's Send button, bottom right, only before the challenge.
    private void RefreshSkip()
    {
        bool show = CanSkip && view != null && view.isActiveAndEnabled && view.send != null;
        if (show && skipButton == null)
        {
            skipButton = Instantiate(view.send, view.transform);
            skipButton.name = "Skip opening";
            skipButton.onClick.RemoveAllListeners(); skipButton.onClick.AddListener(Skip);
            var rect = skipButton.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.985f, .035f); rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = Vector2.zero; rect.sizeDelta = new Vector2(280, 36);
            var label = skipButton.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = "Skip opening (Space)"; if (view.sentence != null) label.font = view.sentence.font;
                label.fontSize = 18; label.supportRichText = false; label.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            skipButton.interactable = true;
        }
        if (skipButton != null && skipButton.gameObject.activeSelf != show) skipButton.gameObject.SetActive(show);
    }

    // Procedural footfalls on a 2D source, one per stride, louder at full pace; the camp loops are 3D and swell on their own.
    private void BuildFootsteps()
    {
        var host = new GameObject("Approach footsteps");
        host.transform.SetParent(transform, false);
        steps = host.AddComponent<AudioSource>();
        steps.playOnAwake = false; steps.loop = false; steps.spatialBlend = 0; steps.priority = 120;
        steps.bypassReverbZones = true; steps.dopplerLevel = 0;
        stepClips = new AudioClip[StepVariants];
        for (int i = 0; i < StepVariants; i++)
        {
            var data = ProceduralAudio.RenderFootstep(4271 + i * 7919, StepRate);
            if (data == null || data.Length == 0) continue;
            stepClips[i] = AudioClip.Create("approach_footstep_" + i, data.Length, 1, StepRate, false);
            stepClips[i].SetData(data, 0);
        }
        // The blow and the landing (docs/defeat.md): a low body thud, played lower for the blow and higher for the ground.
        var body = ProceduralAudio.Render(SpiritSound.Thud, 9011, StepRate);
        if (body != null && body.Length > 0) { thud = AudioClip.Create("defeat_thud", body.Length, 1, StepRate, false); thud.SetData(body, 0); }
    }
    private void Step(float pace)
    {
        if (steps == null || stepClips == null || footstepVolume <= 0) return;
        var clip = stepClips[stepIndex++ % stepClips.Length];
        if (clip == null) return;
        steps.pitch = dice.Range(.92f, 1.08f);
        float under = SpiritSignals.SpiritAudible ? .6f : 1; // Under Vesper's voice, like the camp.
        steps.PlayOneShot(clip, Mathf.Clamp01(footstepVolume * Mathf.Lerp(.45f, 1, pace) * dice.Range(.85f, 1) * under));
    }
    private void StopSteps() { if (steps != null) steps.Stop(); }

    // The approach must never cost the encounter: log, put the camera home and start Trola directly.
    private void Fallback(Exception error)
    {
        Debug.LogException(error, this);
        Debug.LogWarning("[Experience] The road approach, the walk into the camp and the strike are off for this session; Trola's encounter starts at the gate.", this);
        broken = true;
        try { EndApproach(); EndEntry(); EndDefeat(); } catch (Exception cleanup) { Debug.LogException(cleanup, this); }
        if (atmosphere != null) atmosphere.ClearCinematicPose();
        fadeAlpha = fadeTarget = 0; Paint();
        if (skipButton != null) skipButton.gameObject.SetActive(false);
        if (Opens && Controller != null && !Controller.IsSuspended && (Controller.IsOpening || !begun)) Controller.Restart();
        else if (view != null) view.Refresh(); // An entered ending gets the view's own Try again.
    }

    private void OnDestroy()
    {
        if (Controller != null) Controller.Restarted -= OnRestarted;
        RestoreDialogue();
        if (atmosphere != null) atmosphere.ClearCinematicPose();
        if (stepClips != null) foreach (var clip in stepClips) if (clip != null) Destroy(clip);
        if (thud != null) Destroy(thud);
        if (performer != null) performer.StrikeImpact -= OnStrikeImpact;
        if (Instance == this) Instance = null;
    }
}
