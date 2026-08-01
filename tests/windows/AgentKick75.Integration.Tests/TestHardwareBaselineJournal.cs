// SPDX-License-Identifier: MIT
using AgentKick75.Hid.Windows;

namespace AgentKick75.Integration.Tests;

internal sealed class TestHardwareBaselineJournal : IHardwareTestBaselineJournal
{
    private HardwareTestBaselineLease? activeLease;

    public int AcquireCount { get; private set; }

    public int ReleaseCount { get; private set; }

    public bool IsOwned => activeLease is not null;

    public ValueTask<HardwareTestBaselineLease> AcquireAsync(
        HidInterfaceDescriptor device,
        HidTransportProfile profile,
        ReadOnlyMemory<byte> baseline,
        byte currentMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (activeLease is not null)
        {
            throw new InvalidOperationException("The test baseline is already owned.");
        }

        Assert.Equal(8, baseline.Length);
        Assert.InRange(currentMode, (byte)0, (byte)1);
        Assert.True(HidTransportProfiles.IsWritableAllowlisted(profile));
        activeLease = new HardwareTestBaselineLease(
            $"test:{Guid.NewGuid():N}",
            currentMode);
        AcquireCount++;
        return ValueTask.FromResult(activeLease);
    }

    public ValueTask ReleaseAsync(
        HardwareTestBaselineLease lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(activeLease, lease);
        activeLease = null;
        ReleaseCount++;
        return ValueTask.CompletedTask;
    }
}
