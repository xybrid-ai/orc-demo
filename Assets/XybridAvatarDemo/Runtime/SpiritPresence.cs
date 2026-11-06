using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// Subtitle and effects only. Never creates a reply field/countdown, routes input, or writes choice presentation.
public sealed class SpiritPresence : MonoBehaviour
{
    private const int UILayer = 5;
    private static readonly Color Cyan = new Color(.62f, .85f, 1), LineColor = new Color(.80f, .92f, 1);
    private static readonly Color MomentFilter = new Color(.86f, .93f, 1), WarmTint = new Color(.95f, .78f, .45f);

    public float haloIn = .6f, haloOut = .8f, snapOut = .2f;
    [Range(0, 1), Tooltip("Overall halo, post effects, world dimming and particle slowdown; visual.effects_strength in SpiritLines.json.")]
    public float effectsStrength = .5f;
    public string spiritName = "Vesper";
    public SpiritVoice voice;
    public float Weight { get; private set; }

    private sealed class Look
    {
        public Volume volume; public VolumeProfile profile;
        public Vignette vignette; public ChromaticAberration chromatic; public DepthOfField focus; public ColorAdjustments color; public Bloom bloom;
    }
    private SpiritLines.Visual visual;
    private GatekeeperView view;
    private Look moment;
    private bool built, shown, easeIn;
    private float from, to, tweenStart, tweenSeconds = 1, envelope;
    private Color tint = Cyan;
    private Texture2D haloTexture;
    private Canvas haloCanvas, textCanvas;
    private RawImage glow;
    private Text line, nameTag;
    private float lineAlpha, lineTarget;
    private string lineText = "";
    private List<(int start, int length)> linePhrases = new List<(int start, int length)>();
    private int revealVisible = -1;
    private float revealNewest;
    private byte revealAlpha;
    private CanvasGroup sentenceGroup;
    private bool addedSentenceGroup;
    private float sentenceAlpha = 1;
    private ParticleSystem[] particles = Array.Empty<ParticleSystem>();
    private float[] particleSpeeds = Array.Empty<float>();
    private float appliedWorld = -1;
    private int errors;

    public static SpiritPresence Create(GatekeeperView view, TrolaAtmosphere atmosphere, SpiritLines.Visual visual)
    {
        var host = new GameObject("Spirit presence") { layer = 0 };
        var presence = host.AddComponent<SpiritPresence>();
        presence.Build(view, visual ?? new SpiritLines.Visual());
        return presence;
    }

    private void Build(GatekeeperView owner, SpiritLines.Visual look)
    {
        view = owner; visual = look; effectsStrength = look.effects_strength;
        moment = MakeLook("Spirit advice", 50);
        moment.vignette.color.Override(Cyan); moment.vignette.intensity.Override(visual.vignette); moment.vignette.smoothness.Override(visual.smoothness);
        moment.chromatic.intensity.Override(visual.chromatic); moment.focus.aperture.Override(visual.aperture);
        moment.color.saturation.Override(visual.saturation); moment.color.colorFilter.Override(MomentFilter); moment.color.postExposure.Override(visual.exposure);
        moment.bloom.intensity.Override(visual.bloom);
        var reference = view != null ? view.GetComponentInParent<Canvas>() : null;
        if (reference != null) reference = reference.rootCanvas;
        haloTexture = MakeHaloTexture();
        haloCanvas = MakeCanvas("Spirit halo canvas", reference, -1, false);
        glow = MakeRaw("Spirit halo", haloCanvas.transform);
        textCanvas = MakeCanvas("Spirit text canvas", reference, 1, false);
        var font = view != null && view.sentence != null && view.sentence.font != null ? view.sentence.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        line = MakeText("Spirit line", textCanvas.transform, font, 28, FontStyle.Italic, LineColor, .83f, new Vector2(0, 90));
        line.rectTransform.anchorMin = new Vector2(.14f, .83f); line.rectTransform.anchorMax = new Vector2(.86f, .83f);
        var outline = line.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(.45f, .80f, 1, .25f); outline.effectDistance = new Vector2(1.2f, -1.2f);
        nameTag = MakeText("Spirit name", textCanvas.transform, font, 16, FontStyle.Normal, LineColor, .83f, new Vector2(300, 24));
        nameTag.rectTransform.anchoredPosition = new Vector2(0, 52);
        particles = FindObjectsByType<ParticleSystem>();
        particleSpeeds = Array.ConvertAll(particles, system => system.main.simulationSpeed);
        built = true;
        RestoreWorld();
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

    public void Show(SpiritMomentKind kind, SpiritBand band)
    {
        if (!built) return;
        tint = BandTint(band); moment.vignette.color.value = tint;
        shown = true;
        Tween(kind == SpiritMomentKind.Counsel ? 1 : Mathf.Clamp01(visual.whisper_weight), haloIn, true);
    }

    public void Hide(bool snap)
    {
        if (!built) return;
        lineTarget = 0; shown = false;
        Tween(0, snap ? snapOut : haloOut, false);
    }

    public void SetVoiceEnvelope(float rms) { envelope = Mathf.Clamp01(rms); }
    public void ShowLine(string text, bool firstOfSession)
    {
        if (!built) return;
        lineText = text ?? ""; linePhrases = GatekeeperSubtitleTiming.Phrases(lineText); revealVisible = -1;
        nameTag.text = Spaced(spiritName); nameTag.enabled = firstOfSession;
        lineAlpha = 0; lineTarget = 1;
    }

    private void Tween(float target, float seconds, bool rising)
    {
        from = Weight; to = target; tweenStart = Time.unscaledTime; tweenSeconds = Mathf.Max(.01f, seconds); easeIn = rising;
    }

    private void LateUpdate()
    {
        if (!built) return;
        try
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            float t = Mathf.Clamp01((now - tweenStart) / tweenSeconds);
            Weight = easeIn ? from + (to - from) * (1 - Mathf.Pow(1 - t, 3)) : from + (to - from) * t * t * (3 - 2 * t);
            float strength = Mathf.Clamp01(effectsStrength), world = Weight * strength;
            SpiritSignals.MomentWeight = world;
            float shownWeight = Weight;
            if (shown) shownWeight = Weight * (.92f + .08f * Mathf.Sin(2 * Mathf.PI * .22f * now)) + .06f * envelope;
            shownWeight = Mathf.Clamp01(shownWeight) * strength;
            moment.volume.weight = shownWeight; moment.volume.enabled = shownWeight > .0001f;
            glow.color = new Color(tint.r, tint.g, tint.b, shownWeight);
            haloCanvas.enabled = shownWeight > .001f;
            AnimateText(dt);
            DimSentence(shown ? 1 - .65f * strength : 1, dt);
            ApplyWorld(world);
        }
        catch (Exception error)
        {
            if (++errors <= 3) Debug.LogException(error, this);
            if (errors >= 3) enabled = false;
        }
    }

    private void AnimateText(float dt)
    {
        lineAlpha = Mathf.MoveTowards(lineAlpha, lineTarget, dt / .25f);
        line.color = new Color(LineColor.r, LineColor.g, LineColor.b, lineAlpha);
        line.rectTransform.anchoredPosition = new Vector2(0, -6 * (1 - lineAlpha)); // Drifts up 6 px as it fades in.
        // Phrase by phrase as she says them (GatekeeperView.RevealBySentence): the unspoken rest stays laid out but
        // transparent through the line's fade, and the newest phrase's fade multiplies with it. Rich text only then.
        // Rebuilt only when its inputs change, as GatekeeperView does: a cut line holds its reveal until her next line.
        float newest = 1;
        int visible = voice != null && GatekeeperView.RevealBySentence ? voice.RevealedPhrases(lineText, out newest) : int.MaxValue;
        byte alpha = ((Color32)line.color).a;
        if (visible != revealVisible || newest != revealNewest || alpha != revealAlpha)
        {
            revealVisible = visible; revealNewest = newest; revealAlpha = alpha;
            string shown = visible >= linePhrases.Count && newest >= 1 ? lineText
                : GatekeeperSubtitleTiming.Markup(lineText, linePhrases, visible, newest, line.color, out _);
            bool markup = shown != lineText;
            if (line.supportRichText != markup) line.supportRichText = markup;
            if (line.text != shown) line.text = shown;
        }
        nameTag.color = new Color(LineColor.r, LineColor.g, LineColor.b, .7f * lineAlpha);

    }

    // Only Trola's existing subtitle is dimmed; the view remains the sole owner of choice alpha/visibility.
    private void DimSentence(float alpha, float dt)
    {
        if (sentenceGroup == null)
        {
            if (view == null || view.sentence == null || alpha >= 1) return;
            addedSentenceGroup = !view.sentence.TryGetComponent(out sentenceGroup);
            if (addedSentenceGroup) sentenceGroup = view.sentence.gameObject.AddComponent<CanvasGroup>();
            sentenceAlpha = sentenceGroup.alpha;
        }
        float target = sentenceAlpha * alpha;
        sentenceGroup.alpha = Mathf.MoveTowards(sentenceGroup.alpha, target, dt / .25f);
        if (alpha >= 1 && sentenceGroup.alpha == sentenceAlpha)
        {
            if (addedSentenceGroup) Destroy(sentenceGroup);
            sentenceGroup = null; addedSentenceGroup = false;
        }
    }

    private void ApplyWorld(float weight)
    {
        if (Mathf.Abs(weight - appliedWorld) < .0005f && (weight > 0 || appliedWorld == 0)) return;
        appliedWorld = weight;
        float slow = 1 - Mathf.Clamp01(visual.slow_motion) * weight;
        for (int i = 0; i < particles.Length; i++)
            if (particles[i] != null) { var main = particles[i].main; main.simulationSpeed = particleSpeeds[i] * slow; }
        // Camera pose/focal length belong to TrolaAtmosphere; Vesper only supplies effects.
    }

    private void RestoreWorld()
    {
        shown = false; Weight = from = to = 0; envelope = 0;
        SpiritSignals.MomentWeight = 0;
        if (moment?.volume != null) { moment.volume.weight = 0; moment.volume.enabled = false; }
        if (visual != null) ApplyWorld(0);
        if (sentenceGroup != null) { if (addedSentenceGroup) Destroy(sentenceGroup); else sentenceGroup.alpha = sentenceAlpha; }
        sentenceGroup = null; addedSentenceGroup = false;
        lineAlpha = lineTarget = 0;
        if (line != null) line.color = Color.clear;
        if (nameTag != null) nameTag.color = Color.clear;
        if (glow != null) glow.color = Color.clear;
    }

    private void OnEnable()
    {
        if (!built) return;
        haloCanvas.gameObject.SetActive(true); textCanvas.gameObject.SetActive(true);
    }
    private void OnDisable()
    {
        if (!built) return;
        RestoreWorld(); haloCanvas.gameObject.SetActive(false); textCanvas.gameObject.SetActive(false);
    }
    private void OnDestroy()
    {
        RestoreWorld();
        if (moment?.profile != null)
        {
            if (moment.volume != null) moment.volume.sharedProfile = null;
            foreach (var component in moment.profile.components) if (component != null) Destroy(component);
            Destroy(moment.profile);
        }
        if (haloTexture != null) Destroy(haloTexture);
        built = false;
    }
}
