using UnityEngine;

public sealed class GatekeeperBarrier : MonoBehaviour
{
    public GatekeeperController gatekeeper;
    public Transform hinge;
    public Transform secondHinge;
    public float openingAngle = -92;
    private Quaternion closed, secondClosed;
    private bool open;
    public bool IsOpen => open;
    public bool IsFullyOpen => open && hinge != null && Quaternion.Angle(hinge.localRotation, closed * Quaternion.Euler(0, openingAngle, 0)) < .1f
        && (secondHinge == null || Quaternion.Angle(secondHinge.localRotation, secondClosed * Quaternion.Euler(0, -openingAngle, 0)) < .1f);

    private void Awake()
    {
        if (hinge != null) closed = hinge.localRotation;
        if (secondHinge != null) secondClosed = secondHinge.localRotation;
    }
    private void OnEnable() { if (gatekeeper != null) gatekeeper.Changed += Refresh; }
    private void OnDisable() { if (gatekeeper != null) gatekeeper.Changed -= Refresh; }
    private void Refresh() => open = gatekeeper.GateOpenRequested;
    // The doors are where the encounter wants them (not swinging).
    public bool AtRest => hinge == null || Quaternion.Angle(hinge.localRotation, closed * Quaternion.Euler(0, open ? openingAngle : 0, 0)) < .1f;
    // A new approach starts with the doors already where the new encounter wants them (closed), not swinging shut in view.
    public void Settle()
    {
        if (gatekeeper != null) Refresh();
        if (hinge != null) hinge.localRotation = closed * Quaternion.Euler(0, open ? openingAngle : 0, 0);
        if (secondHinge != null) secondHinge.localRotation = secondClosed * Quaternion.Euler(0, open ? -openingAngle : 0, 0);
    }
    private void Update()
    {
        if (hinge == null) return;
        Quaternion target = closed * Quaternion.Euler(0, open ? openingAngle : 0, 0);
        hinge.localRotation = Quaternion.RotateTowards(hinge.localRotation, target, Time.unscaledDeltaTime * 150);
        if (secondHinge != null)
        {
            Quaternion secondTarget = secondClosed * Quaternion.Euler(0, open ? -openingAngle : 0, 0);
            secondHinge.localRotation = Quaternion.RotateTowards(secondHinge.localRotation, secondTarget, Time.unscaledDeltaTime * 150);
        }
        if (IsFullyOpen && gatekeeper.IsWaitingForGate) gatekeeper.NotifyGateOpened();
    }
}
