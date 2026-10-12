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
        if (portraitCamera != null) cameraHome = portraitCamera.transform.position;
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
        portraitCamera.transform.position = drifted + look * ShakePosition;
        portraitCamera.transform.rotation = look * Quaternion.Euler(ShakeEuler);
        if (focus != null)
        {
            Vector3 face = actor.position + new Vector3(0, 1.55f, .12f);
            focus.focusDistance.value = Vector3.Dot(face - portraitCamera.transform.position, portraitCamera.transform.forward);
        }
    }
}
