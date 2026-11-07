using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class TrolaAtmosphere : MonoBehaviour
{
    public GatekeeperController gatekeeper;
    public float PatienceFireScale { get; private set; } = 1;
    private float targetFireScale = 1;
    public Camera portraitCamera;
    public Transform actor;
    public Volume atmosphereVolume;
    public Light[] fireLights;
    public RenderPipelineAsset cinematicPipeline;
    public Vector3 lookOffset = new Vector3(0, 1.55f, .04f);
    private Vector3 cameraHome;
    private float[] intensities;
    private DepthOfField focus;
    private float elapsed;
    private RenderPipelineAsset previousPipeline;
    private bool pipelineChanged;
    // Additive camera shake requested by the performance layer; applied here after drift and
    // look-at, so the camera keeps a single writer and the offset never accumulates.
    public Vector3 ShakePosition { get; private set; }
    public Vector3 ShakeEuler { get; private set; }
    public void SetShake(Vector3 cameraSpacePosition, Vector3 euler) { ShakePosition = cameraSpacePosition; ShakeEuler = euler; }
    // A cinematic pose from the experience director (ExperienceDirector, docs/approach.md), applied here so the camera keeps a
    // single writer. Weight 1 is that pose, 0 the drifting close-up home; between, position, rotation and field of view blend.
    // Shake still applies on top. The director renews it every frame; a pose not renewed since the previous frame lapses,
    // so a director that stops (disabled, destroyed, failed) can never leave the camera on the road.
    public float CinematicWeight => Time.frameCount - cinematicFrame <= 1 ? cinematicWeight : 0;
    private float cinematicWeight;
    private int cinematicFrame = int.MinValue / 2;
    private Vector3 cinematicPosition;
    private Quaternion cinematicRotation = Quaternion.identity;
    private float cinematicFieldOfView, homeFieldOfView, cinematicFocus;
    private bool fieldOfViewChanged;
    // focusDistance > 0: depth of field focuses there (blended by weight) instead of on Trola's face; the walk into the
    // camp looks past him (docs/entry.md). 0 keeps his face in focus, as the road approach does.
    public void SetCinematicPose(Vector3 position, Quaternion rotation, float fieldOfView, float weight, float focusDistance = 0)
    {
        cinematicPosition = position; cinematicRotation = rotation;
        cinematicFieldOfView = fieldOfView > 0 ? fieldOfView : homeFieldOfView;
        cinematicWeight = Mathf.Clamp01(weight); cinematicFrame = Time.frameCount; cinematicFocus = focusDistance;
    }
    public void ClearCinematicPose() { cinematicWeight = 0; }
    public Vector3 HomePosition => cameraHome;
    public float HomeFieldOfView => homeFieldOfView;

    private void OnEnable()
    {
        if (gatekeeper != null) { gatekeeper.PatienceChanged += ReactToPatience; ReactToPatience(3, gatekeeper.Patience); }
        if (!Application.isPlaying || cinematicPipeline == null) return;
        previousPipeline = QualitySettings.renderPipeline;
        QualitySettings.renderPipeline = cinematicPipeline;
        pipelineChanged = true;
    }

    private void OnDisable()
    {
        if (gatekeeper != null) gatekeeper.PatienceChanged -= ReactToPatience;
        if (pipelineChanged && QualitySettings.renderPipeline == cinematicPipeline) QualitySettings.renderPipeline = previousPipeline;
        pipelineChanged = false;
    }

    private void Awake()
    {
        if (portraitCamera != null) { cameraHome = portraitCamera.transform.position; homeFieldOfView = portraitCamera.fieldOfView; }
        intensities = new float[fireLights?.Length ?? 0];
        for (int i = 0; i < intensities.Length; i++) if (fireLights[i] != null) intensities[i] = fireLights[i].intensity;
        if (atmosphereVolume != null) atmosphereVolume.profile.TryGet(out focus);
    }

    private void ReactToPatience(int previous, int current)
    {
        // Anger feeds the fire; no patience HUD. Fade keeps the flicker continuous.
        targetFireScale = Mathf.Lerp(1.9f, .65f, Mathf.Clamp01(current / 5f));
    }
    private void LateUpdate()
    {
        elapsed += Time.unscaledDeltaTime;
        PatienceFireScale = Mathf.MoveTowards(PatienceFireScale, targetFireScale, Time.unscaledDeltaTime * 2);
        for (int i = 0; i < intensities.Length; i++)
            if (fireLights[i] != null)
            {
                float flicker = Mathf.PerlinNoise(i * 11.7f + .9f, elapsed * 3.1f) - .5f;
                float slow = Mathf.Sin(elapsed * 1.8f + i * 2.4f);
                fireLights[i].intensity = intensities[i] * PatienceFireScale * (1 + .16f * flicker + .025f * slow);
            }
        if (portraitCamera == null || actor == null) return;
        Vector3 drifted = cameraHome + new Vector3(.022f * Mathf.Sin(elapsed * .18f), .009f * Mathf.Sin(elapsed * .13f), 0);
        Quaternion look = Quaternion.LookRotation(actor.position + lookOffset - drifted);
        float weight = CinematicWeight;
        if (weight > 0) { drifted = Vector3.Lerp(drifted, cinematicPosition, weight); look = Quaternion.Slerp(look, cinematicRotation, weight); }
        portraitCamera.transform.position = drifted + look * ShakePosition;
        portraitCamera.transform.rotation = look * Quaternion.Euler(ShakeEuler);
        // Written only while a cinematic pose blends, and once more to restore the close-up's own field of view.
        if (weight > 0 || fieldOfViewChanged)
        {
            portraitCamera.fieldOfView = Mathf.Lerp(homeFieldOfView, cinematicFieldOfView, weight);
            fieldOfViewChanged = weight > 0;
        }
        if (focus != null)
        {
            Vector3 face = actor.position + new Vector3(0, 1.55f, .12f);
            float distance = Mathf.Max(.5f, Vector3.Dot(face - portraitCamera.transform.position, portraitCamera.transform.forward));
            if (weight > 0 && cinematicFocus > 0) distance = Mathf.Lerp(distance, cinematicFocus, weight);
            focus.focusDistance.value = distance;
        }
    }
}
