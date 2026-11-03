using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// Vesper on screen. Runtime Volumes with their own VolumeProfile instances (PortraitGrade is never written; DoF overrides
// aperture only, so TrolaAtmosphere keeps Trola's face in focus), a code-generated halo and countdown ring, her line, the
// mind field, thought -> label, and world dimming. Everything it changes outside its own objects (choice and subtitle
// alpha, particle speed, camera focal length, choice marks) is restored on Hide, OnDisable and OnDestroy. Choices stay
// interactable, Trola's subtitle text is never written and his input is never focused, except to undo the cancel when the
// Esc that lets her go also hit his focused field. See docs/spirit.md.
public sealed class SpiritPresence : MonoBehaviour
{
    private const int UILayer = 5;
    private const string MarkName = "Spirit mark";
    private static readonly Color Cyan = new Color(.62f, .85f, 1), Amber = new Color(1, .7f, .4f), Ember = new Color(1, .45f, .35f);
    private static readonly Color LineColor = new Color(.80f, .92f, 1), MomentFilter = new Color(.86f, .93f, 1);
    private static readonly Color FlinchTint = new Color(.60f, .10f, .06f), WarmTint = new Color(.95f, .78f, .45f), WarmFilter = new Color(1.05f, .98f, .9f);

    [Tooltip("Seconds; the director copies timing.halo_in / halo_out / snap_out here.")]
    public float haloIn = .6f, haloOut = .8f, snapOut = .2f;
    public float warmWeight = .5f;
    public string spiritName = "Vesper";

    // Her voice, for the sentence reveal of her line (the director sets it); without it her lines show whole.
    public SpiritVoice voice;
    public float Weight { get; private set; }
    public string Draft => fieldOpen && field != null ? field.text ?? "" : "";
    public event Action<string> Submitted;
    public event Action Escaped;

    private sealed class Look
    {
        public Volume volume; public VolumeProfile profile;
        public Vignette vignette; public ChromaticAberration chromatic; public DepthOfField focus; public ColorAdjustments color; public Bloom bloom;
    }
    private enum ThoughtMode { None, Dissolving, Waiting, Label }

    private GatekeeperView view;
    private TrolaAtmosphere atmosphere;
    private SpiritLines.Visual visual = new SpiritLines.Visual();
    private bool built, shown, speaking, fieldOpen, refocus, moveCaretToEnd;
    private string pendingSubmit;
    private Look moment, warm, flinch;
    private float from, to, tweenStart, tweenSeconds = 1;
    private bool easeIn;
    private float flinchAt = -10, warmAt = -10, warmHold, envelope;
    private Color tint = Cyan;
    private Texture2D haloTexture;
    private Canvas haloCanvas, textCanvas;
    private RawImage glow, ring;
    private float ringAlpha, countdownAt = -10, remaining = 1, impatience;
    private Text line, nameTag, thought, hint, placeholder, fieldText, ghost;
    private Image underline, thread, strike;
    private InputField field;
    private float lineAlpha, lineTarget, lineShownAt = -10, labelShownAt = -10, ghostAt = -10;
    private string lineText = "";
    private List<(int start, int length)> lineSentences = new List<(int start, int length)>();
    private int revealVisible = -1; // what the line's markup was last built for; -1 rebuilds it
    private float revealNewest;
    private byte revealAlpha;
    private ThoughtMode thoughtMode;
    private string thoughtText = "";
    private float thoughtAt, thoughtAlpha;
    private Color thoughtColor = Cyan;
    private CanvasGroup choiceGroup, sentenceGroup;
    private bool addedChoiceGroup, addedSentenceGroup;
    private ParticleSystem[] particles = Array.Empty<ParticleSystem>();
    private float[] particleSpeeds = Array.Empty<float>();
    private Camera lens;
    private float baseFocal, appliedWorld = -1;
    private int errors, escapeFrame = -1, trolaRestoreFrame = -1;
    private string trolaDraft;

    public static SpiritPresence Create(GatekeeperView view, TrolaAtmosphere atmosphere, SpiritLines.Visual visual)
    {
        var host = new GameObject("Spirit presence") { layer = 0 }; // Volumes: the portrait camera's mask is Default only.
        var presence = host.AddComponent<SpiritPresence>();
        presence.Build(view, atmosphere, visual ?? new SpiritLines.Visual());
        return presence;
    }

    private void Build(GatekeeperView owner, TrolaAtmosphere mood, SpiritLines.Visual look)
    {
        view = owner; atmosphere = mood; visual = look;
        moment = MakeLook("Spirit moment", 50);
        moment.vignette.color.Override(Cyan); moment.vignette.intensity.Override(visual.vignette); moment.vignette.smoothness.Override(visual.smoothness);
        moment.chromatic.intensity.Override(visual.chromatic);
        moment.focus.aperture.Override(visual.aperture);
        moment.color.saturation.Override(visual.saturation); moment.color.colorFilter.Override(MomentFilter); moment.color.postExposure.Override(visual.exposure);
        moment.bloom.intensity.Override(visual.bloom);
        warm = MakeLook("Spirit warm ending", 51);
        warm.vignette.color.Override(WarmTint); warm.vignette.intensity.Override(.30f); warm.vignette.smoothness.Override(.8f);
        warm.chromatic.intensity.Override(.15f); warm.focus.aperture.Override(2.8f);
        warm.color.saturation.Override(4); warm.color.colorFilter.Override(WarmFilter); warm.color.postExposure.Override(0);
        warm.bloom.intensity.Override(.70f);
        flinch = MakeLook("Spirit flinch", 52);
        flinch.vignette.color.Override(FlinchTint); flinch.vignette.intensity.Override(.55f); flinch.vignette.smoothness.Override(.6f);
        flinch.chromatic.intensity.Override(.85f); flinch.color.saturation.Override(-10);

        var viewCanvas = view != null ? view.GetComponentInParent<Canvas>() : null;
        if (viewCanvas != null) viewCanvas = viewCanvas.rootCanvas;
        haloTexture = MakeHaloTexture();
        haloCanvas = MakeCanvas("Spirit halo canvas", viewCanvas, -1, false);
        glow = MakeRaw("Spirit halo", haloCanvas.transform);
        ring = MakeRaw("Spirit countdown ring", haloCanvas.transform);
        ring.rectTransform.localScale = Vector3.one * 1.1f; // Covers the corners while it breathes ±2.5°.

        textCanvas = MakeCanvas("Spirit text canvas", viewCanvas, 1, true);
        Font font = view != null && view.sentence != null && view.sentence.font != null ? view.sentence.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        line = MakeText("Spirit line", textCanvas.transform, font, 28, FontStyle.Italic, LineColor, .83f, new Vector2(0, 90));
        line.rectTransform.anchorMin = new Vector2(.14f, .83f); line.rectTransform.anchorMax = new Vector2(.86f, .83f);
        var outline = line.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(.45f, .80f, 1, .25f); outline.effectDistance = new Vector2(1.2f, -1.2f);
        nameTag = MakeText("Spirit name", textCanvas.transform, font, 16, FontStyle.Normal, LineColor, .83f, new Vector2(300, 24));
        nameTag.rectTransform.anchoredPosition = new Vector2(0, 52);
        nameTag.text = Spaced(spiritName);
        thought = MakeText("Spirit thought", textCanvas.transform, font, 22, FontStyle.Italic, Cyan, .75f, new Vector2(900, 40));
        ghost = MakeText("Spirit cut-off draft", textCanvas.transform, font, 24, FontStyle.Italic, Cyan, .75f, new Vector2(600, 40));
        ghost.horizontalOverflow = HorizontalWrapMode.Overflow; // One line, like the field, so the strike runs through it.
        strike = MakeImage("Strike", ghost.transform, Cyan);
        strike.rectTransform.anchorMin = strike.rectTransform.anchorMax = new Vector2(.5f, .5f); strike.rectTransform.sizeDelta = new Vector2(0, 2);
        BuildField(font);
        if (view != null && view.input != null) view.input.onEndEdit.AddListener(TrolaEndEdit);

        particles = FindObjectsByType<ParticleSystem>();
        particleSpeeds = Array.ConvertAll(particles, system => system.main.simulationSpeed);
        lens = atmosphere != null && atmosphere.portraitCamera != null ? atmosphere.portraitCamera : Camera.main;
        if (lens != null) baseFocal = lens.focalLength;
        built = true;
        HideAllGraphics();
    }

    private Look MakeLook(string name, int priority)
    {
        var host = new GameObject(name) { layer = 0 };
        host.transform.SetParent(transform, false);
        var look = new Look { volume = host.AddComponent<Volume>(), profile = ScriptableObject.CreateInstance<VolumeProfile>() };
        look.profile.name = name + " (runtime)"; look.profile.hideFlags = HideFlags.DontSave;
        look.volume.isGlobal = true; look.volume.priority = priority; look.volume.weight = 0;
        look.volume.sharedProfile = look.profile; look.volume.enabled = false;
        look.vignette = look.profile.Add<Vignette>(); look.chromatic = look.profile.Add<ChromaticAberration>();
        look.focus = look.profile.Add<DepthOfField>(); look.color = look.profile.Add<ColorAdjustments>(); look.bloom = look.profile.Add<Bloom>();
        return look;
    }

    private Canvas MakeCanvas(string name, Canvas reference, int order, bool raycaster)
    {
        var go = new GameObject(name, typeof(RectTransform)) { layer = UILayer };
        go.transform.SetParent(transform, false);
        var canvas = go.AddComponent<Canvas>();
        var camera = reference != null && reference.renderMode == RenderMode.ScreenSpaceCamera ? reference.worldCamera : null;
        canvas.renderMode = camera != null ? RenderMode.ScreenSpaceCamera : RenderMode.ScreenSpaceOverlay;
        canvas.worldCamera = camera; canvas.planeDistance = reference != null ? reference.planeDistance : 1;
        if (reference != null) canvas.sortingLayerID = reference.sortingLayerID;
        canvas.sortingOrder = (reference != null ? reference.sortingOrder : 0) + order;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        if (raycaster) go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    private static RectTransform UI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform)) { layer = UILayer };
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private RawImage MakeRaw(string name, Transform parent)
    {
        var rect = UI(name, parent);
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        var image = rect.gameObject.AddComponent<RawImage>();
        image.texture = haloTexture; image.raycastTarget = false; image.color = Color.clear;
        return image;
    }

    private static Text MakeText(string name, Transform parent, Font font, int size, FontStyle style, Color color, float y, Vector2 box)
    {
        var rect = UI(name, parent);
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, y); rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = box;
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font; text.fontSize = size; text.fontStyle = style; text.color = color; text.alignment = TextAnchor.MiddleCenter;
        text.supportRichText = false; text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static Image MakeImage(string name, Transform parent, Color color)
    {
        var image = UI(name, parent).gameObject.AddComponent<Image>();
        image.color = color; image.raycastTarget = false;
        return image;
    }

    private void BuildField(Font font)
    {
        var root = UI("Spirit mind field", textCanvas.transform);
        root.gameObject.SetActive(false); // Wired while inactive; it exists only while she listens.
        root.anchorMin = root.anchorMax = new Vector2(.5f, .75f); root.sizeDelta = new Vector2(600, 40);
        var hit = root.gameObject.AddComponent<Image>(); hit.color = Color.clear; // Click target only: no box.
        fieldText = MakeText("Text", root, font, 24, FontStyle.Italic, Cyan, .5f, Vector2.zero);
        fieldText.rectTransform.anchorMin = Vector2.zero; fieldText.rectTransform.anchorMax = Vector2.one;
        fieldText.rectTransform.offsetMin = new Vector2(8, 0); fieldText.rectTransform.offsetMax = new Vector2(-8, 0);
        fieldText.horizontalOverflow = HorizontalWrapMode.Overflow;
        placeholder = MakeText("Placeholder", root, font, 24, FontStyle.Italic, new Color(Cyan.r, Cyan.g, Cyan.b, .45f), .5f, Vector2.zero);
        placeholder.rectTransform.anchorMin = Vector2.zero; placeholder.rectTransform.anchorMax = Vector2.one;
        placeholder.text = "Answer her in your head… (Enter)";
        underline = MakeImage("Underline", root, new Color(Cyan.r, Cyan.g, Cyan.b, .30f));
        underline.rectTransform.anchorMin = new Vector2(0, 0); underline.rectTransform.anchorMax = new Vector2(1, 0);
        underline.rectTransform.sizeDelta = new Vector2(0, 1); underline.rectTransform.anchoredPosition = new Vector2(0, -2);
        thread = MakeImage("Time thread", root, Cyan);
        thread.rectTransform.anchorMin = thread.rectTransform.anchorMax = new Vector2(.5f, 0);
        thread.rectTransform.sizeDelta = new Vector2(600, 2); thread.rectTransform.anchoredPosition = new Vector2(0, -6);
        hint = MakeText("Hint", root, font, 15, FontStyle.Normal, new Color(Cyan.r, Cyan.g, Cyan.b, .5f), 0, new Vector2(600, 22));
        hint.rectTransform.anchoredPosition = new Vector2(0, -22);
        hint.text = "Enter: think · Esc: let her go";
        field = root.gameObject.AddComponent<InputField>();
        field.textComponent = fieldText; field.placeholder = placeholder; field.targetGraphic = hit;
        field.lineType = InputField.LineType.SingleLine; field.characterLimit = 80; // Refocus selects all; the caret is moved to the end next frame.
        field.customCaretColor = true; field.caretColor = Cyan; field.selectionColor = new Color(Cyan.r, Cyan.g, Cyan.b, .25f);
        var colors = field.colors; colors.normalColor = colors.highlightedColor = colors.selectedColor = colors.pressedColor = Color.clear; field.colors = colors;
        field.onEndEdit.AddListener(EndEdit);
    }

    // Edge glow: alpha smoothstep(.55, 1, r)·.55 with Perlin wisps; clamp wrap makes the zoomed-out ring thicken inward.
    private static Texture2D MakeHaloTexture()
    {
        const int size = 256;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { name = "Spirit halo (runtime)", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + .5f) / size * 2 - 1, v = (y + .5f) / size * 2 - 1, r = Mathf.Sqrt(u * u + v * v), angle = Mathf.Atan2(v, u);
                float wisps = .68f + .32f * Mathf.PerlinNoise(Mathf.Cos(angle) * 2.4f + 4.1f + r * 1.9f, Mathf.Sin(angle) * 2.4f + 9.7f + r * .8f);
                float a = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.55f, 1, r)) * .55f * wisps;
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255));
            }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }

    private static string Spaced(string name)
    {
        var chars = (string.IsNullOrWhiteSpace(name) ? "Vesper" : name.Trim()).ToUpperInvariant().ToCharArray();
        return string.Join(((char)0x2009).ToString(), Array.ConvertAll(chars, c => c.ToString())); // Thin-space tracking.
    }

    private static Color BandTint(SpiritBand band) => band switch
    {
        SpiritBand.Done or SpiritBand.Furious => new Color(.70f, .30f, .45f),
        SpiritBand.Annoyed or SpiritBand.Wary => new Color(.42f, .62f, .86f),
        SpiritBand.Listening => new Color(.62f, .80f, 1),
        _ => WarmTint
    };

    // ---- Public surface ----

    public void Show(SpiritMomentKind kind, SpiritBand band)
    {
        if (!built) return;
        tint = BandTint(band);
        moment.vignette.color.value = tint;
        moment.chromatic.intensity.value = visual.chromatic; // Undo a snap spike.
        shown = true; speaking = true; // Arrival: choices at .55 until she listens.
        Tween(kind == SpiritMomentKind.Counsel ? 1 : Mathf.Clamp01(visual.whisper_weight), haloIn, true);
    }

    public void Hide(bool snap)
    {
        if (!built) return;
        if (snap) moment.chromatic.intensity.value = 1; // Spike, then gone with the weight.
        CloseField(false);
        ClearAllText();
        shown = false; speaking = false;
        Tween(0, snap ? snapOut : haloOut, false);
    }

    public void Flinch() { flinchAt = Time.unscaledTime; }
    public void Warm(float seconds) { warmAt = Time.unscaledTime; warmHold = Mathf.Max(0, seconds); }
    public void SetVoiceEnvelope(float rms) { envelope = Mathf.Clamp01(rms); }

    public void ShowLine(string text, bool firstOfSession)
    {
        if (!built) return;
        lineText = text ?? ""; lineSentences = GatekeeperSubtitleTiming.Sentences(lineText); revealVisible = -1; // AnimateText writes it.
        nameTag.text = Spaced(spiritName); nameTag.enabled = firstOfSession;
        lineAlpha = 0; lineTarget = 1; lineShownAt = Time.unscaledTime;
    }

    public void ClearLine()
    {
        lineTarget = 0;
        // The thought label stays until her answer ends: only clear it once a line has followed it.
        if (lineShownAt >= labelShownAt && thoughtMode != ThoughtMode.None) thoughtMode = ThoughtMode.None;
    }

    public void OpenField(int characterLimit)
    {
        if (!built) return;
        // The field sits on the thought's row: the label never shows under it (the answer has ended).
        thoughtMode = ThoughtMode.None; thoughtAlpha = 0; thought.color = Color.clear;
        fieldOpen = true;
        field.characterLimit = Mathf.Clamp(characterLimit, 1, 240);
        field.SetTextWithoutNotify("");
        field.gameObject.SetActive(true);
        remaining = 1;
        field.ActivateInputField();
    }

    public void CloseField(bool strikeDraft)
    {
        if (!built || !fieldOpen) return;
        string draft = field.text ?? "";
        fieldOpen = false; refocus = false; pendingSubmit = null; // Before deactivation, which raises onEndEdit.
        if (strikeDraft && draft.Trim().Length > 0)
        {
            // The tail the single-line field last showed (it scrolls to the caret), so text and strike stay one line.
            ghost.text = draft;
            for (int cut = 1; cut < draft.Length && ghost.preferredWidth > 584; cut++) ghost.text = "…" + draft.Substring(cut).TrimStart();
            strike.rectTransform.sizeDelta = new Vector2(Mathf.Min(600, ghost.preferredWidth + 8), 2);
            ghostAt = Time.unscaledTime;
        }
        HideField();
    }

    private void HideField()
    {
        if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == field.gameObject) EventSystem.current.SetSelectedGameObject(null);
        field.SetTextWithoutNotify("");
        field.gameObject.SetActive(false);
    }

    public void ShowThinking(string text)
    {
        if (!built) return;
        thoughtText = text ?? ""; thoughtMode = ThoughtMode.Dissolving; thoughtAt = Time.unscaledTime;
        thoughtColor = Cyan; thoughtAlpha = 1; labelShownAt = Time.unscaledTime;
    }

    // d1's raw label with underscores as spaces, e.g. "state_plan · threaten" -> "‹ state plan · threaten ›".
    public void ShowLabel(string label, bool alarm)
    {
        if (!built) return;
        string text = (label ?? "").Trim();
        thoughtText = text.Length > 0 && text[0] == '‹' ? text : "‹ " + text.Replace('_', ' ') + " ›";
        thoughtMode = ThoughtMode.Label; thoughtColor = alarm ? Ember : Cyan; thoughtAlpha = 1; labelShownAt = Time.unscaledTime;
    }

    public void SetCountdown(float remaining01, float trolaImpatience01)
    {
        remaining = Mathf.Clamp01(remaining01); impatience = Mathf.Clamp01(trolaImpatience01); countdownAt = Time.unscaledTime;
    }

    public void SetSpeakingDim(bool isSpeaking) { speaking = isSpeaking; }

    // Lens only: a 3 px underline under each live choice button. Never a Text, never interactable or label changes.
    public void MarkChoices(IReadOnlyList<SpiritOption> options, bool visible)
    {
        if (!built || view == null || view.choiceRow == null) return;
        foreach (Transform button in view.choiceRow)
        {
            var old = button.Find(MarkName);
            SpiritOption option = null;
            if (visible && options != null && button.gameObject.activeSelf)
                foreach (var candidate in options)
                    if (candidate != null && button.name == "Choice " + candidate.Label) { option = candidate; break; }
            Color? color = option == null ? (Color?)null : option.Verdict == SpiritVerdict.Best ? Cyan
                : option.Verdict == SpiritVerdict.Fatal || option.Aggressive ? Ember : (Color?)null;
            if (color == null)
            {
                if (old != null) { old.gameObject.SetActive(false); old.name = MarkName + " (removed)"; Destroy(old.gameObject); }
                continue;
            }
            var mark = old != null ? old.GetComponent<Image>() : MakeMark(button);
            mark.color = color.Value;
        }
    }

    private static Image MakeMark(Transform button)
    {
        var image = MakeImage(MarkName, button, Cyan);
        image.gameObject.layer = button.gameObject.layer;
        var rect = image.rectTransform;
        rect.anchorMin = new Vector2(0, 0); rect.anchorMax = new Vector2(1, 0); rect.pivot = new Vector2(.5f, 0);
        rect.offsetMin = new Vector2(14, 4); rect.offsetMax = new Vector2(-14, 7);
        image.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        return image;
    }

    // ---- Animation ----

    private void Tween(float target, float seconds, bool rising)
    {
        from = Weight; to = target; tweenStart = Time.unscaledTime; tweenSeconds = Mathf.Max(.01f, seconds); easeIn = rising;
    }

    private void Update()
    {
        if (!built) return;
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame && (shown || fieldOpen)) { escapeFrame = Time.frameCount; Raise(Escaped); }
        if (pendingSubmit != null)
        {
            // Closed and raised outside the InputField's own event processing, so handlers may reopen it freely.
            string text = pendingSubmit; pendingSubmit = null;
            if (!fieldOpen && field.gameObject.activeSelf) HideField();
            Raise(Submitted, text);
        }
    }

    private void LateUpdate()
    {
        if (!built) return;
        try { Animate(); }
        catch (Exception error)
        {
            // Never an error per frame: after three, she stays off screen (OnDisable restores the world).
            if (++errors <= 3) Debug.LogException(error, this);
            if (errors >= 3) enabled = false;
        }
    }

    private void Animate()
    {
        WatchTrolaField();
        float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
        float t = Mathf.Clamp01((now - tweenStart) / tweenSeconds);
        Weight = easeIn ? from + (to - from) * (1 - Mathf.Pow(1 - t, 3)) : from + (to - from) * t * t * (3 - 2 * t);
        SpiritSignals.MomentWeight = Weight;
        float shownWeight = Weight;
        if (shown && speaking) shownWeight = Weight * (.92f + .08f * Mathf.Sin(2 * Mathf.PI * .22f * now)) + .06f * envelope;
        shownWeight = Mathf.Clamp01(shownWeight);
        SetWeight(moment, shownWeight);
        float sinceFlinch = now - flinchAt;
        SetWeight(flinch, sinceFlinch < 0 ? 0 : sinceFlinch < .06f ? sinceFlinch / .06f : Mathf.Clamp01(1 - (sinceFlinch - .06f) / .5f));
        float sinceWarm = now - warmAt, warmOut = sinceWarm - haloIn - warmHold;
        SetWeight(warm, warmWeight * (sinceWarm < 0 ? 0 : sinceWarm < haloIn ? 1 - Mathf.Pow(1 - sinceWarm / haloIn, 3)
            : warmOut < 0 ? 1 : 1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01(warmOut / Mathf.Max(.01f, haloOut)))));

        glow.color = new Color(tint.r, tint.g, tint.b, shownWeight);
        bool counting = fieldOpen || now - countdownAt < .25f;
        ringAlpha = Mathf.MoveTowards(ringAlpha, counting && shown ? 1 : 0, dt * 4);
        float k = .05f + .35f * (1 - remaining);
        ring.uvRect = new Rect(-k, -k, 1 + 2 * k, 1 + 2 * k);
        Color ringColor = Color.Lerp(Cyan, Amber, impatience);
        ring.color = new Color(ringColor.r, ringColor.g, ringColor.b, ringAlpha * Weight * .9f);
        ring.rectTransform.localEulerAngles = new Vector3(0, 0, 2.5f * Mathf.Sin(2 * Mathf.PI * .07f * now));
        haloCanvas.enabled = Weight > .001f || ringAlpha > .001f;

        AnimateText(now, dt, ringColor);
        AnimateWorld(dt);
    }

    private static void SetWeight(Look look, float weight)
    {
        look.volume.weight = weight;
        bool active = weight > .0001f;
        if (look.volume.enabled != active) look.volume.enabled = active;
    }

    private void AnimateText(float now, float dt, Color ringColor)
    {
        lineAlpha = Mathf.MoveTowards(lineAlpha, lineTarget, dt / .25f);
        line.color = new Color(LineColor.r, LineColor.g, LineColor.b, lineAlpha);
        line.rectTransform.anchoredPosition = new Vector2(0, -6 * (1 - lineAlpha)); // Drifts up 6 px as it fades in.
        // Sentence by sentence as she says them (GatekeeperView.RevealBySentence): the unspoken rest stays laid out but
        // transparent through the line's fade, and the newest sentence's fade multiplies with it. Rich text only then.
        // Rebuilt only when its inputs change, as GatekeeperView does: a cut line holds its reveal until her next line.
        float newest = 1;
        int visible = voice != null && GatekeeperView.RevealBySentence ? voice.RevealedSentences(lineText, out newest) : int.MaxValue;
        byte alpha = ((Color32)line.color).a;
        if (visible != revealVisible || newest != revealNewest || alpha != revealAlpha)
        {
            revealVisible = visible; revealNewest = newest; revealAlpha = alpha;
            string shown = visible >= lineSentences.Count && newest >= 1 ? lineText
                : GatekeeperSubtitleTiming.Markup(lineText, lineSentences, visible, newest, line.color, out _);
            bool markup = shown != lineText;
            if (line.supportRichText != markup) line.supportRichText = markup;
            if (line.text != shown) line.text = shown;
        }
        nameTag.color = new Color(LineColor.r, LineColor.g, LineColor.b, .7f * lineAlpha);

        if (fieldOpen)
        {
            if (refocus || !field.isFocused) { refocus = false; field.ActivateInputField(); moveCaretToEnd = true; }
            else if (moveCaretToEnd) { moveCaretToEnd = false; field.MoveTextEnd(false); }
            thread.rectTransform.sizeDelta = new Vector2(600 * remaining, 2); // Shrinks from both ends.
            thread.color = ringColor;
        }

        float ghostAge = now - ghostAt;
        float ghostAlpha = ghostAge < 0 ? 0 : Mathf.Clamp01(1 - ghostAge / .45f) * .9f;
        ghost.color = new Color(Cyan.r, Cyan.g, Cyan.b, ghostAlpha);
        strike.color = new Color(Ember.r, Ember.g, Ember.b, ghostAlpha);
        ghost.rectTransform.anchoredPosition = new Vector2(0, 8 * (1 - ghostAlpha));

        switch (thoughtMode)
        {
            case ThoughtMode.Dissolving:
                float p = Mathf.Clamp01((now - thoughtAt) / .25f);
                thought.text = Dissolve(thoughtText, p);
                thought.color = new Color(thoughtColor.r, thoughtColor.g, thoughtColor.b, Mathf.Lerp(.6f, .3f, p));
                if (p >= 1) { thoughtMode = ThoughtMode.Waiting; thoughtAt = now; }
                break;
            case ThoughtMode.Waiting:
                int dots = 1 + (int)((now - thoughtAt) / .3f) % 3;
                thought.text = dots == 1 ? "‹ ·     ›" : dots == 2 ? "‹ · ·   ›" : "‹ · · · ›";
                thought.color = new Color(Cyan.r, Cyan.g, Cyan.b, .55f + .15f * Mathf.Sin(now * 6));
                break;
            case ThoughtMode.Label:
                thought.text = thoughtText;
                thought.color = new Color(thoughtColor.r, thoughtColor.g, thoughtColor.b, thoughtAlpha);
                break;
            default:
                thoughtAlpha = Mathf.MoveTowards(thoughtAlpha, 0, dt / .25f);
                thought.color = new Color(thoughtColor.r, thoughtColor.g, thoughtColor.b, thoughtAlpha);
                break;
        }
    }

    // Letters drop out in a fixed scattered order as the thought dissolves into the waiting dots.
    private static string Dissolve(string text, float progress)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            uint h = (uint)(i + 1) * 2654435761u;
            if (!char.IsWhiteSpace(chars[i]) && (h >> 8) % 1000 / 1000f < progress) chars[i] = ' ';
        }
        return new string(chars);
    }

    private void AnimateWorld(float dt)
    {
        // Alpha only; interactable and blocksRaycasts stay as they are, so choices remain clickable. Groups exist only
        // while dimmed: one this script added is removed again once back at 1.
        Dim(ref choiceGroup, ref addedChoiceGroup, view != null && view.choiceRow != null ? view.choiceRow.gameObject : null, shown ? speaking ? .55f : .85f : 1, dt);
        Dim(ref sentenceGroup, ref addedSentenceGroup, view != null && view.sentence != null ? view.sentence.gameObject : null, shown && speaking ? .35f : 1, dt);
        ApplyWorld(Weight);
    }

    private void Dim(ref CanvasGroup group, ref bool added, GameObject target, float alpha, float dt)
    {
        if (group == null)
        {
            if (target == null || alpha >= 1) return;
            added = !target.TryGetComponent(out group);
            if (added) group = target.AddComponent<CanvasGroup>();
        }
        group.alpha = Mathf.MoveTowards(group.alpha, alpha, dt / .25f);
        if (alpha >= 1 && group.alpha >= 1) { if (added) Destroy(group); group = null; added = false; }
    }

    // Fire and embers slow (particles use scaled time; Trola runs on unscaled time), and the lens pushes in slightly.
    private void ApplyWorld(float weight)
    {
        if (Mathf.Abs(weight - appliedWorld) < .0005f && (weight > 0 || appliedWorld == 0)) return;
        appliedWorld = weight;
        float slow = 1 - Mathf.Clamp01(visual.slow_motion) * weight;
        for (int i = 0; i < particles.Length; i++)
            if (particles[i] != null) { var main = particles[i].main; main.simulationSpeed = weight <= 0 ? particleSpeeds[i] : particleSpeeds[i] * slow; }
        if (lens != null && lens.usePhysicalProperties) lens.focalLength = baseFocal + visual.camera_push_mm * weight;
    }

    // ---- Input ----

    // Speech input (PlayerVoiceInput): while the player talks to her the field shows what is coming, which also counts
    // as a draft so her window does not close mid-sentence; the transcript is then submitted as if typed and Entered.
    public bool FieldOpen => built && fieldOpen;
    public void ShowSpoken(string text) { if (FieldOpen) { field.SetTextWithoutNotify(text ?? ""); moveCaretToEnd = true; } }
    public void SubmitSpoken(string text)
    {
        string draft = (text ?? "").Trim();
        if (!FieldOpen || draft.Length == 0) return;
        if (draft.Length > field.characterLimit) draft = draft.Substring(0, field.characterLimit).TrimEnd();
        field.SetTextWithoutNotify(draft);
        fieldOpen = false; refocus = false;
        pendingSubmit = draft;
    }

    private void EndEdit(string text)
    {
        if (!fieldOpen) return;
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) return; // Update raises Escaped.
        bool enter = keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
        string draft = (text ?? "").Trim();
        if (!enter || draft.Length == 0) { refocus = true; return; } // Focus lost or empty: take it back.
        // The field closes on Enter (next Update, outside this callback); the director reopens it for another reply.
        fieldOpen = false; refocus = false;
        pendingSubmit = draft;
    }

    // Esc lets her go, but in a whisper at the name, business or free-talk prompt Trola's field has focus and uGUI cancels
    // it on the same key: his draft reverts to the text it had when focused and focus drops. Raised after that revert,
    // whichever of this Update and the EventSystem's ran first; put both back.
    private void TrolaEndEdit(string unused)
    {
        var keyboard = Keyboard.current;
        if (!built || !enabled || keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;
        if (!shown && Weight <= .001f && escapeFrame != Time.frameCount) return; // Not hers: his field's own Esc.
        var input = view.input;
        if (trolaDraft != null) input.SetTextWithoutNotify(trolaDraft);
        input.ActivateInputField(); // Activates in its LateUpdate, which selects all; WatchTrolaField then moves the caret to the end.
        trolaRestoreFrame = Time.frameCount;
    }

    // LateUpdate, after this frame's keystrokes: the text an Esc next frame would revert.
    private void WatchTrolaField()
    {
        var input = view != null ? view.input : null;
        if (input == null) return;
        bool focused = input.isFocused;
        if (trolaRestoreFrame >= 0 && (focused || Time.frameCount > trolaRestoreFrame + 2))
        {
            if (focused) input.MoveTextEnd(false); // The next key must not replace the restored draft.
            trolaRestoreFrame = -1;
        }
        trolaDraft = focused ? input.text : null; // Null: if it activates later this frame, its revert text is the current text.
    }

    private void Raise(Action handlers)
    {
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception error) { Debug.LogException(error, this); }
    }

    private void Raise(Action<string> handlers, string value)
    {
        if (handlers == null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception error) { Debug.LogException(error, this); }
    }

    // ---- Restore ----

    private void ClearAllText()
    {
        lineTarget = 0; thoughtMode = ThoughtMode.None;
    }

    private void HideAllGraphics()
    {
        lineAlpha = lineTarget = 0; thoughtMode = ThoughtMode.None; thoughtAlpha = 0; ghostAt = -10; ringAlpha = 0;
        if (line != null) line.color = Color.clear;
        if (nameTag != null) nameTag.color = Color.clear;
        if (thought != null) thought.color = Color.clear;
        if (ghost != null) { ghost.color = Color.clear; strike.color = Color.clear; }
        if (glow != null) glow.color = Color.clear;
        if (ring != null) ring.color = Color.clear;
    }

    private void RestoreWorld()
    {
        shown = false; speaking = false; Weight = from = to = 0; flinchAt = warmAt = -10;
        SpiritSignals.MomentWeight = 0;
        if (fieldOpen) CloseField(false);
        foreach (var look in new[] { moment, warm, flinch }) if (look != null && look.volume != null) SetWeight(look, 0);
        ApplyWorld(0);
        if (choiceGroup != null) { if (addedChoiceGroup) Destroy(choiceGroup); else choiceGroup.alpha = 1; }
        if (sentenceGroup != null) { if (addedSentenceGroup) Destroy(sentenceGroup); else sentenceGroup.alpha = 1; }
        choiceGroup = sentenceGroup = null;
        MarkChoices(null, false);
        HideAllGraphics();
    }

    private void OnEnable()
    {
        if (!built) return;
        if (haloCanvas != null) haloCanvas.gameObject.SetActive(true);
        if (textCanvas != null) textCanvas.gameObject.SetActive(true);
    }

    private void OnDisable()
    {
        if (!built) return;
        RestoreWorld();
        if (haloCanvas != null) haloCanvas.gameObject.SetActive(false);
        if (textCanvas != null) textCanvas.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (!built) return;
        RestoreWorld();
        if (view != null && view.input != null) view.input.onEndEdit.RemoveListener(TrolaEndEdit);
        foreach (var look in new[] { moment, warm, flinch })
        {
            if (look?.profile == null) continue;
            if (look.volume != null) look.volume.sharedProfile = null;
            foreach (var component in look.profile.components) if (component != null) Destroy(component);
            Destroy(look.profile);
        }
        if (haloTexture != null) Destroy(haloTexture);
        built = false;
    }
}
