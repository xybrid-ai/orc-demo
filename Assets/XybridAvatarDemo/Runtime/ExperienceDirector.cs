using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// The experience around Trola's encounter (docs/approach.md). Milestone 2: the road approach. It decides when the
// encounter starts and hands TrolaAtmosphere the camera poses it blends; dialogue, patience and endings stay with
// GatekeeperController and its line sources, and Vesper's line with SpiritDirector. Installs on its own, like
// SpiritDirector. -xybridNoOpening, -xybridSelfTest, -xybridSpeechTest, a self-test or speech validation in the scene,
// a scene without TrolaAtmosphere, or a controller that does not auto-start keep the original immediate start.
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

    public GatekeeperController Controller { get; private set; }
    public RoadApproach Approach { get; private set; }
    public bool IsApproaching => Approach != null;
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
    private float fadeAlpha, fadeSeconds = 1;
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
        if (HasArgument(NoOpeningFlag) || HasArgument("-xybridSelfTest") || HasArgument("-xybridSpeechTest")) return;
        var controller = FindAnyObjectByType<GatekeeperController>();
        var atmosphere = FindAnyObjectByType<TrolaAtmosphere>();
        if (controller == null || !controller.autoStart || controller.scenario == null || !controller.scenario.authoredConversation) return;
        if (atmosphere == null || atmosphere.portraitCamera == null || atmosphere.actor == null || UnderTest()) return;
        // After every Awake, before the controller's Start: the encounter now begins at the gate, not 0.5 s into Play.
        controller.autoStart = false;
        var director = new GameObject("Experience director (runtime)").AddComponent<ExperienceDirector>();
        director.Controller = controller; director.atmosphere = atmosphere;
    }
    private static bool HasArgument(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    private static bool UnderTest() => FindAnyObjectByType<GatekeeperSelfTest>() != null || FindAnyObjectByType<GatekeeperSpeechValidation>() != null;

    private void Awake() { Instance = this; }

    private void Start()
    {
        view = FindAnyObjectByType<GatekeeperView>();
        barrier = FindAnyObjectByType<GatekeeperBarrier>();
        try
        {
            BuildFade(); BuildFootsteps();
            if (!BeginApproach()) throw new InvalidOperationException("the encounter could not be prepared");
        }
        catch (Exception error) { Fallback(error); }
    }

    // Plays (or replays) the approach: a new encounter is prepared and waits at the gate. False when it cannot run
    // (suspended, broken, or the scene is invalid); GatekeeperView's Try again then restarts the encounter directly.
    public bool BeginApproach()
    {
        if (broken || Controller == null || Controller.IsSuspended) return false;
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
        RefreshSkip();
        AnimateFade();
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
    private void FadeFromBlack(float seconds) { fadeAlpha = 1; fadeSeconds = Mathf.Max(.01f, seconds); Paint(); }
    private void AnimateFade()
    {
        if (fadeAlpha <= 0) return;
        fadeAlpha = Mathf.MoveTowards(fadeAlpha, 0, Mathf.Min(Time.unscaledDeltaTime, MaxStep) / fadeSeconds);
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
        Debug.LogWarning("[Experience] The road approach is off for this session; Trola's encounter starts at the gate.", this);
        broken = true;
        try { EndApproach(); } catch (Exception cleanup) { Debug.LogException(cleanup, this); }
        if (atmosphere != null) atmosphere.ClearCinematicPose();
        fadeAlpha = 0; Paint();
        if (skipButton != null) skipButton.gameObject.SetActive(false);
        if (Controller != null && !Controller.IsSuspended && (Controller.IsOpening || !begun)) Controller.Restart();
    }

    private void OnDestroy()
    {
        if (atmosphere != null) atmosphere.ClearCinematicPose();
        if (stepClips != null) foreach (var clip in stepClips) if (clip != null) Destroy(clip);
        if (Instance == this) Instance = null;
    }
}
