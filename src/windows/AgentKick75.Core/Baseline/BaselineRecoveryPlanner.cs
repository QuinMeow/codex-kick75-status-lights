namespace AgentKick75.Core.Baseline;

public enum BaselineRecoveryAction
{
    CaptureNewBaseline,
    RestoreBeforeAcquire,
    RefuseCorruptBaseline,
    RefuseUnsupportedVersion,
    RefuseDeviceIdentityMismatch,
    RefuseTransportProfileMismatch,
    RefuseInterfaceFingerprintMismatch,
    RefuseCurrentModeMismatch,
}

public sealed record BaselineRecoveryDecision(
    BaselineRecoveryAction Action,
    BaselineRecord? Baseline,
    string Reason)
{
    public bool MayWriteDevice => Action is BaselineRecoveryAction.CaptureNewBaseline
        or BaselineRecoveryAction.RestoreBeforeAcquire;
}

public static class BaselineRecoveryPlanner
{
    public static BaselineRecoveryDecision Decide(
        BaselineLoadResult loadResult,
        BaselineDeviceIdentity currentDevice,
        byte currentMode)
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        ArgumentNullException.ThrowIfNull(currentDevice);
        if (currentMode > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentMode),
                "The active current mode must be either 0 or 1.");
        }

        if (loadResult.Status == BaselineLoadStatus.Missing)
        {
            return New(BaselineRecoveryAction.CaptureNewBaseline, null, "No saved baseline exists.");
        }

        if (loadResult.Status == BaselineLoadStatus.Corrupt)
        {
            return New(
                BaselineRecoveryAction.RefuseCorruptBaseline,
                null,
                "The saved baseline is corrupt; acquiring a new baseline could capture a stale status color.");
        }

        if (loadResult.Status == BaselineLoadStatus.UnsupportedVersion)
        {
            return New(
                BaselineRecoveryAction.RefuseUnsupportedVersion,
                null,
                "The saved baseline uses an unsupported schema version.");
        }

        BaselineRecord baseline = loadResult.Baseline
            ?? throw new ArgumentException("A loaded result must contain a baseline.", nameof(loadResult));
        if (!baseline.Ownership.IsOwned)
        {
            return New(
                BaselineRecoveryAction.CaptureNewBaseline,
                baseline,
                "The previous baseline was released normally.");
        }

        if (!string.Equals(
                baseline.Device.DeviceIdentity,
                currentDevice.DeviceIdentity,
                StringComparison.Ordinal))
        {
            return New(
                BaselineRecoveryAction.RefuseDeviceIdentityMismatch,
                baseline,
                "The connected device identity does not match the owned baseline.");
        }

        if (!string.Equals(
                baseline.Device.TransportProfileId,
                currentDevice.TransportProfileId,
                StringComparison.Ordinal))
        {
            return New(
                BaselineRecoveryAction.RefuseTransportProfileMismatch,
                baseline,
                "The active transport profile does not match the owned baseline.");
        }

        if (!string.Equals(
                baseline.Device.InterfaceFingerprint,
                currentDevice.InterfaceFingerprint,
                StringComparison.Ordinal))
        {
            return New(
                BaselineRecoveryAction.RefuseInterfaceFingerprintMismatch,
                baseline,
                "The HID interface fingerprint does not match the owned baseline.");
        }


        if (baseline.CurrentMode != currentMode)
        {
            return New(
                BaselineRecoveryAction.RefuseCurrentModeMismatch,
                baseline,
                $"The active current mode {currentMode} does not match the owned baseline mode {baseline.CurrentMode}.");
        }

        return New(
            BaselineRecoveryAction.RestoreBeforeAcquire,
            baseline,
            "An unreleased matching ownership marker requires restoration before reacquisition.");
    }

    private static BaselineRecoveryDecision New(
        BaselineRecoveryAction action,
        BaselineRecord? baseline,
        string reason)
    {
        return new BaselineRecoveryDecision(action, baseline, reason);
    }
}
