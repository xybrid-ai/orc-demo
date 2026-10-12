using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

public static class XybridDuskScene
{
    private const string Root = "Assets/XybridAvatarDemo";
    private const string Art = Root + "/Materials/Dusk";

    [MenuItem("Xybrid/Avatar Demo/Apply Dusk Portrait")]
    public static void Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before adjusting the shot.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != Root + "/GoblinSpeakingDemo.unity") throw new InvalidOperationException("Open the gatekeeper scene first.");
        var actor = UnityEngine.Object.FindFirstObjectByType<XybridAvatarPlayer>();
        var view = UnityEngine.Object.FindFirstObjectByType<GatekeeperView>();
        var camera = Camera.main;
        if (actor == null || view == null || camera == null || GameObject.Find("Village Gate Set") == null) throw new InvalidOperationException("Build the fortified village encounter first.");
        Folder(Art);
        var old = GameObject.Find("Dusk Atmosphere"); if (old != null) Undo.DestroyObjectImmediate(old);
        var root = new GameObject("Dusk Atmosphere"); Undo.RegisterCreatedObjectUndo(root, "Create dusk portrait");
        Undo.RecordObject(actor.transform, "Separate Trola from gate chains");
        actor.transform.position = new Vector3(.30f, 0, .55f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(actor.transform);
        Undo.RecordObject(camera, "Set portrait lens"); Undo.RecordObject(camera.transform, "Lower portrait camera");
        camera.transform.position = new Vector3(.30f, 1.29f, 2.75f);
        camera.transform.LookAt(actor.transform.position + new Vector3(0, 1.55f, .04f));
        camera.usePhysicalProperties = true; camera.sensorSize = new Vector2(36, 24); camera.focalLength = 60;
        camera.gateFit = Camera.GateFitMode.Vertical; camera.nearClipPlane = .1f; camera.allowHDR = true;
        camera.clearFlags = CameraClearFlags.Skybox; camera.cullingMask &= ~(1 << 5);
        var cameraData = camera.GetUniversalAdditionalCameraData(); Undo.RecordObject(cameraData, "Enable cinematic camera rendering");
        cameraData.renderPostProcessing = true; cameraData.requiresDepthTexture = true;
        cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
        cameraData.cameraStack.Clear();

        Directional("Key", new Color(.35f, .48f, .78f), .16f, new Vector3(35, 135, 0), true);
        Directional("Fill", new Color(.29f, .40f, .67f), .04f, new Vector3(18, 310, 0), false);
        Directional("Rim", new Color(.28f, .52f, 1), 1.55f, new Vector3(25, 5, 0), false);
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(.095f, .13f, .21f);
        RenderSettings.ambientEquatorColor = new Color(.055f, .072f, .105f);
        RenderSettings.ambientGroundColor = new Color(.025f, .03f, .045f);
        RenderSettings.fogColor = new Color(.055f, .078f, .125f); RenderSettings.fogDensity = .035f;
        RenderSettings.fog = true; RenderSettings.fogMode = FogMode.ExponentialSquared;
        var sky = Material("Dusk sky", "Xybrid/Trola Dusk Sky");
        sky.SetColor("_Top", new Color(.04f, .06f, .12f)); sky.SetColor("_Horizon", new Color(.15f, .20f, .31f));
        RenderSettings.skybox = sky;

        var fireLights = new List<Light>();
        foreach (var particles in GameObject.Find("Village Gate Set").GetComponentsInChildren<ParticleSystem>())
        {
            WarmFlame(particles);
            var lamp = particles.GetComponentInChildren<Light>();
            if (lamp != null) { Undo.RecordObject(lamp, "Strengthen dusk torchlight"); lamp.intensity = 1.7f; lamp.range = 2.7f; lamp.color = new Color(1, .58f, .28f); fireLights.Add(lamp); }
        }
        fireLights.Add(Spot("Face firelight", root.transform, new Vector3(-.8f, 1.27f, 2.05f), actor.transform.position + new Vector3(0, 1.3f, 0), new Color(1, .68f, .40f), 7, 3.5f, 68));
        fireLights.Add(Spot("Gentle fire bounce", root.transform, new Vector3(1.5f, 1.1f, 2.1f), actor.transform.position + new Vector3(0, 1.42f, 0), new Color(1, .57f, .30f), 1.2f, 3.5f, 75));
        // Broad, shadowless hearth bounce reveals lip shapes between the tusks and beard.
        var mouthFill = Spot("Mouth hearth fill", root.transform, new Vector3(.30f, 1.02f, 1.55f), actor.transform.position + new Vector3(0, 1.51f, .12f), new Color(1, .78f, .52f), 1.5f, 1.7f, 42);
        mouthFill.innerSpotAngle = 14; mouthFill.shadows = LightShadows.None;
        fireLights.Add(mouthFill);
        Spot("Moon shoulder rim", root.transform, new Vector3(-1.1f, 2.05f, -.65f), actor.transform.position + new Vector3(0, 1.4f, 0), new Color(.27f, .52f, 1), 10, 4, 70);
        Spot("Moon hair rim", root.transform, new Vector3(.95f, 2.2f, -.10f), actor.transform.position + new Vector3(0, 1.82f, .04f), new Color(.32f, .58f, 1), 5, 2.8f, 55);
        var crest = GameObject.Find("Gate crest");
        if (crest != null)
        {
            Undo.RecordObject(crest.transform, "Clear the topknot silhouette");
            var position = crest.transform.position; position.x = 1.45f; crest.transform.position = position;
            PrefabUtility.RecordPrefabInstancePropertyModifications(crest.transform);
        }
        // Background torches motivate the warm lighting; their short reach keeps the gate darker.
        foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            Undo.RecordObject(renderer, "Enable character contact shadows"); renderer.shadowCastingMode = ShadowCastingMode.On; renderer.receiveShadows = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }
        var volumeGo = new GameObject("Portrait post-processing"); Undo.RegisterCreatedObjectUndo(volumeGo, "Create portrait grade"); volumeGo.transform.SetParent(root.transform, false);
        var volume = volumeGo.AddComponent<Volume>(); volume.isGlobal = true; volume.priority = 20;
        volume.sharedProfile = Profile(camera, actor.transform);
        var motion = root.AddComponent<TrolaAtmosphere>(); motion.portraitCamera = camera; motion.actor = actor.transform;
        motion.atmosphereVolume = volume; motion.fireLights = fireLights.ToArray(); motion.cinematicPipeline = Pipeline();
        int phase = 0;
        foreach (string name in new[] { "Gate crest", "Watchtower standard -1", "Watchtower standard 1" })
        {
            var banner = GameObject.Find(name); if (banner == null) continue;
            var sway = banner.GetComponent<TrolaBannerSway>() ?? Undo.AddComponent<TrolaBannerSway>(banner); sway.phase = phase++ * 2.3f;
        }
        Particles("Low swamp mist", root.transform, new Vector3(0, .24f, -3), false);
        Particles("Drifting embers", root.transform, new Vector3(0, 1.2f, -.8f), true);
        BuildHud(view, camera, root.transform);
        actor.SetAffect(.90f, .78f, 0, 0); actor.EvaluateAt(-1, 0);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Debug.Log("[Dusk] Portrait camera, firelight, blue rim, mist, motion, and slim HUD saved.");
    }

    private static VolumeProfile Profile(Camera camera, Transform actor)
    {
        string path = Art + "/PortraitGrade.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null) { profile = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(profile, path); }
        T Component<T>() where T : VolumeComponent { if (!profile.TryGet<T>(out var value)) { value = profile.Add<T>(true); AssetDatabase.AddObjectToAsset(value, profile); } return value; }
        var dof = Component<DepthOfField>(); dof.mode.Override(DepthOfFieldMode.Bokeh);
        dof.focusDistance.Override(Vector3.Dot(actor.position + new Vector3(0, 1.55f, .12f) - camera.transform.position, camera.transform.forward));
        dof.focalLength.Override(60); dof.aperture.Override(3.8f); dof.bladeCount.Override(6);
        var bloom = Component<Bloom>(); bloom.intensity.Override(.48f); bloom.threshold.Override(1); bloom.scatter.Override(.60f);
        var vignette = Component<Vignette>(); vignette.intensity.Override(.17f); vignette.smoothness.Override(.35f);
        var grade = Component<ColorAdjustments>(); grade.postExposure.Override(.4f); grade.contrast.Override(7); grade.saturation.Override(-5); grade.colorFilter.Override(new Color(1, .98f, .93f));
        var balance = Component<WhiteBalance>(); balance.temperature.Override(5); balance.tint.Override(1);
        Component<Tonemapping>().mode.Override(TonemappingMode.ACES);
        foreach (var component in profile.components) EditorUtility.SetDirty(component);
        EditorUtility.SetDirty(profile); return profile;
    }

    private static UniversalRenderPipelineAsset Pipeline()
    {
        string path = Art + "/PortraitPipeline.asset";
        var source = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (source == null) throw new InvalidOperationException("URP is required.");
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
        {
            pipeline = UnityEngine.Object.Instantiate(source); pipeline.name = "Trola portrait pipeline"; AssetDatabase.CreateAsset(pipeline, path);
            var sourceData = new SerializedObject(source).FindProperty("m_RendererDataList").GetArrayElementAtIndex(0).objectReferenceValue as ScriptableRendererData;
            var renderer = UnityEngine.Object.Instantiate(sourceData); renderer.name = "Trola portrait renderer";
            string renderPath = Art + "/PortraitRenderer.asset"; AssetDatabase.CreateAsset(renderer, renderPath);
            var features = new List<ScriptableRendererFeature>(sourceData.rendererFeatures); renderer.rendererFeatures.Clear();
            foreach (var feature in features)
            {
                var clone = UnityEngine.Object.Instantiate(feature); clone.name = feature.name; AssetDatabase.AddObjectToAsset(clone, renderer); renderer.rendererFeatures.Add(clone);
                if (clone is ScreenSpaceAmbientOcclusion)
                {
                    clone.SetActive(true); var settings = new SerializedObject(clone);
                    settings.FindProperty("m_Settings.Intensity").floatValue = 1.5f;
                    settings.FindProperty("m_Settings.Radius").floatValue = .15f;
                    settings.FindProperty("m_Settings.DirectLightingStrength").floatValue = .20f;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            var data = new SerializedObject(pipeline); var list = data.FindProperty("m_RendererDataList"); list.arraySize = 1; list.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            data.FindProperty("m_DefaultRendererIndex").intValue = 0;
            data.FindProperty("m_MainLightShadowsSupported").boolValue = true;
            data.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
            data.FindProperty("m_ShadowDistance").floatValue = 20;
            data.FindProperty("m_RequireDepthTexture").boolValue = true;
            var limit = data.FindProperty("m_AdditionalLightsPerObjectLimit"); if (limit != null) limit.intValue = 8;
            data.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(pipeline);
        }
        return pipeline;
    }

    private static void BuildHud(GatekeeperView view, Camera camera, Transform root)
    {
        Undo.RecordObject(view.gameObject, "Set dialogue canvas layer"); view.gameObject.layer = 5;
        var canvas = view.GetComponent<Canvas>(); Undo.RecordObject(canvas, "Keep HUD sharp after depth of field"); Undo.RecordObject(view, "Slim dialogue panel");
        var ui = new GameObject("Portrait UI Camera", typeof(Camera)); Undo.RegisterCreatedObjectUndo(ui, "Create sharp HUD camera"); ui.transform.SetParent(root, false);
        ui.transform.position = new Vector3(0, 0, -100); var uiCamera = ui.GetComponent<Camera>(); uiCamera.orthographic = true; uiCamera.orthographicSize = 1;
        uiCamera.clearFlags = CameraClearFlags.Depth; uiCamera.cullingMask = 1 << 5; uiCamera.nearClipPlane = .1f; uiCamera.farClipPlane = 10; uiCamera.allowHDR = true;
        var data = uiCamera.GetUniversalAdditionalCameraData(); data.renderType = CameraRenderType.Overlay; data.renderPostProcessing = false;
        camera.GetUniversalAdditionalCameraData().cameraStack.Add(uiCamera);
        canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = uiCamera; canvas.planeDistance = 1;
        for (int i = view.transform.childCount - 1; i >= 0; i--) Undo.DestroyObjectImmediate(view.transform.GetChild(i).gameObject);
        var fade = UI("Bottom gradient", view.transform); var image = fade.AddComponent<Image>(); image.sprite = FadeSprite(); image.raycastTarget = false;
        Rect((RectTransform)fade.transform, new Vector2(0, 0), new Vector2(1, .38f), Vector2.zero, Vector2.zero);
        var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Fonts/Cinzel-VariableFont_wght.ttf") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var sentence = Text("Subtitle", view.transform, font, 31); sentence.color = new Color(.96f, .91f, .80f);
        Rect(sentence.rectTransform, new Vector2(.12f, .115f), new Vector2(.88f, .115f), new Vector2(0, 80), new Vector2(.5f, 0));
        var shadow = sentence.gameObject.AddComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, .95f); shadow.effectDistance = new Vector2(1, -3);
        var outline = sentence.gameObject.AddComponent<Outline>(); outline.effectColor = new Color(0, 0, 0, .65f); outline.effectDistance = new Vector2(.7f, -.7f);
        var row = UI("Your reply", view.transform); Rect((RectTransform)row.transform, new Vector2(.22f, .038f), new Vector2(.78f, .038f), new Vector2(0, 54), new Vector2(.5f, 0));
        var layout = row.AddComponent<HorizontalLayoutGroup>(); layout.spacing = 10; layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = false;
        var entry = UI("Typed response", row.transform); entry.AddComponent<Image>().color = new Color(.025f, .041f, .058f, .82f); entry.AddComponent<LayoutElement>().flexibleWidth = 1;
        var input = entry.AddComponent<InputField>(); input.characterLimit = GatekeeperIntentRules.MaxInputLength; input.lineType = InputField.LineType.SingleLine;
        var inputFont = font;
        var entered = Text("Text", entry.transform, inputFont, 24); Stretch(entered.rectTransform, new Vector2(16, 7)); entered.alignment = TextAnchor.MiddleLeft; input.textComponent = entered;
        var placeholder = Text("Placeholder", entry.transform, inputFont, 24); placeholder.text = "What do you say?"; placeholder.color = new Color(.63f, .67f, .70f);
        placeholder.alignment = TextAnchor.MiddleLeft; Stretch(placeholder.rectTransform, new Vector2(16, 7)); input.placeholder = placeholder;
        var send = Button("Send", row.transform, font, "Send"); send.gameObject.AddComponent<LayoutElement>().preferredWidth = 125;
        var action = Button("Advance", view.transform, font, "Skip"); Rect((RectTransform)action.transform, new Vector2(.5f, .038f), new Vector2(.5f, .038f), new Vector2(180, 44), new Vector2(.5f, 0));
        view.sentence = sentence; view.inputRow = row; view.input = input; view.send = send; view.action = action; view.actionText = action.GetComponentInChildren<Text>();
        view.Refresh();
    }

    private static Sprite FadeSprite()
    {
        string path = Art + "/DialogueFade.asset";
        string texturePath = Art + "/DialogueFadeTexture.asset";
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture == null) { texture = new Texture2D(2, 128, TextureFormat.RGBA32, false); AssetDatabase.CreateAsset(texture, texturePath); }
        texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < 128; y++) for (int x = 0; x < 2; x++) texture.SetPixel(x, y, new Color(.008f, .012f, .02f, .96f * Mathf.Pow(1 - y / 127f, .50f)));
        texture.Apply(); EditorUtility.SetDirty(texture);
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null) { sprite = Sprite.Create(texture, new Rect(0, 0, 2, 128), Vector2.one * .5f, 100, 0, SpriteMeshType.FullRect); AssetDatabase.CreateAsset(sprite, path); }
        return sprite;
    }

    private static void WarmFlame(ParticleSystem particles)
    {
        Undo.RecordObject(particles, "Give flames a luminous amber core");
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particles.main; main.startColor = new Color(1, 1, 1, .75f);
        main.startSize = new ParticleSystem.MinMaxCurve(.075f, .15f);
        var color = particles.colorOverLifetime; color.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(new Color(1, .75f, .22f), .55f), new GradientColorKey(new Color(.7f, .35f, .04f), 1) }, new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(.8f, .15f), new GradientAlphaKey(0, 1) });
        color.color = gradient;
        var material = Material("Amber torch flame", "Universal Render Pipeline/Particles/Unlit");
        material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Materials/VillageGate/FlameMask.asset"));
        material.SetColor("_BaseColor", new Color(3, 2.3f, .7f, 1)); material.SetFloat("_Surface", 1); material.SetFloat("_Blend", 2);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); material.SetFloat("_DstBlend", (float)BlendMode.One);
        material.SetFloat("_ZWrite", 0); material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.DisableKeyword("_ALPHAPREMULTIPLY_ON"); material.renderQueue = 3000;
        var renderer = particles.GetComponent<ParticleSystemRenderer>(); Undo.RecordObject(renderer, "Use luminous amber flame material"); renderer.sharedMaterial = material;
        particles.Play();
    }

    private static void Particles(string name, Transform parent, Vector3 position, bool embers)
    {
        var go = new GameObject(name, typeof(ParticleSystem)); Undo.RegisterCreatedObjectUndo(go, "Create ambient motion"); go.transform.SetParent(parent, false); go.transform.position = position;
        var ps = go.GetComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.loop = true; main.prewarm = true; main.duration = 16; main.startLifetime = embers ? 6 : 16;
        main.startSpeed = embers ? .05f : .015f; main.maxParticles = embers ? 45 : 30; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startColor = embers ? new Color(4, 1.2f, .12f, .8f) : new Color(.23f, .34f, .48f, .13f);
        main.startSize3D = true; main.startSizeX = embers ? .018f : 5; main.startSizeY = embers ? .018f : .55f; main.startSizeZ = 1;
        var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = embers ? new Vector3(4, 1.5f, 2.5f) : new Vector3(11, .18f, 5);
        var emission = ps.emission; emission.rateOverTime = embers ? 4 : 1.4f;
        var velocity = ps.velocityOverLifetime; velocity.enabled = true; velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = embers ? .045f : .04f; velocity.y = embers ? .13f : 0; velocity.z = .015f;
        var color = ps.colorOverLifetime; color.enabled = true; var gradient = new Gradient(); gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(.8f, .25f), new GradientAlphaKey(0, 1) }); color.color = gradient;
        var mat = Material(embers ? "Embers" : "Ground mist", "Universal Render Pipeline/Particles/Unlit"); mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Materials/VillageGate/FlameMask.asset"));
        mat.SetFloat("_Surface", 1); mat.SetFloat("_ZWrite", 0); mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); mat.renderQueue = 3000;
        ps.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
        ps.Play();
    }

    private static void Directional(string name, Color color, float intensity, Vector3 angles, bool shadows)
    {
        var go = GameObject.Find(name); if (go == null) return; var light = go.GetComponent<Light>();
        Undo.RecordObject(light, "Set dusk light"); Undo.RecordObject(go.transform, "Set dusk light"); light.color = color; light.intensity = intensity; go.transform.rotation = Quaternion.Euler(angles);
        light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
    }

    private static Light Spot(string name, Transform parent, Vector3 position, Vector3 target, Color color, float intensity, float range, float angle)
    {
        var go = new GameObject(name, typeof(Light)); Undo.RegisterCreatedObjectUndo(go, "Create portrait firelight"); go.transform.SetParent(parent, false); go.transform.position = position; go.transform.LookAt(target);
        var light = go.GetComponent<Light>(); light.type = LightType.Spot; light.color = color; light.intensity = intensity; light.range = range; light.spotAngle = angle; light.innerSpotAngle = angle * .75f;
        light.shadows = LightShadows.Soft; light.shadowStrength = .9f; light.shadowBias = .025f; light.shadowNormalBias = .15f; return light;
    }

    private static Material Material(string name, string shader)
    {
        string path = Art + "/" + name + ".mat"; var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { var program = Shader.Find(shader); if (program == null) throw new InvalidOperationException("Missing shader: " + shader); mat = new Material(program); AssetDatabase.CreateAsset(mat, path); }
        EditorUtility.SetDirty(mat); return mat;
    }

    private static GameObject UI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform)); go.layer = 5; Undo.RegisterCreatedObjectUndo(go, "Create slim dialogue UI"); go.transform.SetParent(parent, false); return go;
    }
    private static Text Text(string name, Transform parent, Font font, int size)
    {
        var text = UI(name, parent).AddComponent<Text>(); text.font = font; text.fontSize = size; text.color = new Color(.94f, .92f, .86f); text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false; text.supportRichText = false; return text;
    }
    private static Button Button(string name, Transform parent, Font font, string label)
    {
        var go = UI(name, parent); go.AddComponent<Image>().color = new Color(.11f, .15f, .17f, .82f); var button = go.AddComponent<Button>(); var text = Text("Label", go.transform, font, 22); text.text = label; Stretch(text.rectTransform, new Vector2(8, 5)); return button;
    }
    private static void Rect(RectTransform rect, Vector2 min, Vector2 max, Vector2 size, Vector2 pivot) { rect.anchorMin = min; rect.anchorMax = max; rect.sizeDelta = size; rect.pivot = pivot; rect.anchoredPosition = Vector2.zero; }
    private static void Stretch(RectTransform rect, Vector2 inset) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = inset; rect.offsetMax = -inset; }
    private static void Folder(string path) { if (AssetDatabase.IsValidFolder(path)) return; int slash = path.LastIndexOf('/'); AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1)); }
}
