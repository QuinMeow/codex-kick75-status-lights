// SPDX-License-Identifier: MIT
using AgentKick75.Core.Protocol;
using AgentKick75.Hid.Windows;

namespace AgentKick75.App.Lighting;

/// <summary>
/// Production adapter from the M2 worker contract to the descriptor-filtered
/// Windows HID stack. It opens exactly one selected profile and never dual-writes.
/// </summary>
public sealed class WindowsLightingTransport : ILightingTransport
{
    private readonly IHidDeviceEnumerator enumerator;
    private readonly HidDeviceSelector selector;
    private readonly IHidConnectionFactory connectionFactory;
    private Kick75HidProtocolClient? client;
    private bool disposed;

    public WindowsLightingTransport(
        IHidDeviceEnumerator enumerator,
        HidDeviceSelector selector,
        IHidConnectionFactory connectionFactory)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public static WindowsLightingTransport CreateDefault()
    {
        return new WindowsLightingTransport(
            new Win32HidDeviceEnumerator(),
            new HidDeviceSelector(),
            new Win32HidConnectionFactory());
    }

    public ValueTask<LightingDeviceInspection?> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (client is not null)
        {
            throw new InvalidOperationException(
                "Descriptor inspection is only available without an active HID session.");
        }

        HidDeviceSelection inspected = selector.Select(
            enumerator.Enumerate());
        if (inspected.Device is null || inspected.Profile is null)
        {
            return ValueTask.FromResult<LightingDeviceInspection?>(null);
        }

        LightingDeviceSupport? support = inspected switch
        {
            {
                State: HidDeviceState.Present,
                Profile.Support: HidProfileSupport.Writable,
            } when inspected.Profile.MatchesWritableDescriptor(inspected.Device) =>
                LightingDeviceSupport.Writable,
            {
                State: HidDeviceState.DiagnosticOnly,
                Profile.Support: HidProfileSupport.DiagnosticOnly,
            } when inspected.Profile.MatchesDiagnosticDescriptor(inspected.Device) =>
                LightingDeviceSupport.DiagnosticOnly,
            _ => null,
        };
        if (support is null)
        {
            return ValueTask.FromResult<LightingDeviceInspection?>(null);
        }

        return ValueTask.FromResult<LightingDeviceInspection?>(new(
            inspected.Device.DeviceIdentity,
            inspected.Profile.Id,
            inspected.Device.InterfaceFingerprint,
            support.Value,
            DescriptorMetadata(inspected.Device)));
    }

    public async ValueTask<LightingDeviceSession> ConnectAsync(
        LightingConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (client is not null)
        {
            throw new InvalidOperationException("The Windows lighting transport is already connected.");
        }

        IHidReportConnection? connection = null;
        Kick75HidProtocolClient? pendingClient = null;
        try
        {
            ValidateProfile(request.RequiredTransportProfileId);
            HidDeviceSelection selection = selector.Select(enumerator.Enumerate());
            if (!selection.IsWritable || selection.Device is null || selection.Profile is null)
            {
                throw SelectionFailure(selection);
            }

            connection = await connectionFactory.OpenAsync(selection, cancellationToken).ConfigureAwait(false);
            pendingClient = new Kick75HidProtocolClient(connection);
            connection = null; // Ownership transferred to the protocol client.
            await pendingClient.InitializeAsync(cancellationToken).ConfigureAwait(false);
            client = pendingClient;
            pendingClient = null;
            return new LightingDeviceSession(
                selection.Device.DeviceIdentity,
                selection.Profile.Id,
                selection.Device.InterfaceFingerprint,
                client.CurrentMode,
                DescriptorMetadata(selection.Device));
        }
        catch (Exception exception) when (exception is not LightingTransportException)
        {
            LightingTransportException translated = Translate(exception);
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (pendingClient is not null)
            {
                await pendingClient.DisposeAsync().ConfigureAwait(false);
            }

            throw translated;
        }
    }

    public async ValueTask<byte[]> ReadSideLightAsync(
        CancellationToken cancellationToken = default)
    {
        Kick75HidProtocolClient active = GetClient();
        try
        {
            return await active.ReadSideLightStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Translate(exception);
        }
    }

    public async ValueTask WriteSideLightAsync(
        ReadOnlyMemory<byte> sideLightState,
        CancellationToken cancellationToken = default)
    {
        InMemoryBaselineOwnershipStore.ValidateSideLight(sideLightState.Span);
        Kick75HidProtocolClient active = GetClient();
        try
        {
            await active.WriteSideLightFullStateAsync(sideLightState, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Translate(exception);
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Kick75HidProtocolClient? active = client;
        client = null;
        if (active is not null)
        {
            await active.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }

    private Kick75HidProtocolClient GetClient()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return client ?? throw new LightingTransportException(
            LightingTransportFailureKind.DeviceDisconnected,
            "The Windows HID lighting transport is not connected.");
    }

    private static LightingTransportException SelectionFailure(HidDeviceSelection failedSelection)
    {
        LightingTransportFailureKind kind = failedSelection.State switch
        {
            HidDeviceState.Busy => LightingTransportFailureKind.DeviceBusy,
            HidDeviceState.Unsupported or HidDeviceState.DiagnosticOnly =>
                LightingTransportFailureKind.ProtocolViolation,
            _ => LightingTransportFailureKind.DeviceDisconnected,
        };
        return new LightingTransportException(kind, failedSelection.Message);
    }

    private static void ValidateProfile(string? requiredTransportProfileId)
    {
        if (requiredTransportProfileId is null)
        {
            return;
        }

        if (string.Equals(
                requiredTransportProfileId,
                HidTransportProfiles.Kick75Usb.Id,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new LightingTransportException(
            LightingTransportFailureKind.BaselineMismatch,
            $"Owned baseline requires unknown transport profile '{requiredTransportProfileId}'; " +
            "automatic fallback is blocked.");
    }

    private static LightingDeviceDescriptorMetadata DescriptorMetadata(
        HidInterfaceDescriptor device)
    {
        return new LightingDeviceDescriptorMetadata(
            device.SanitizedProduct,
            device.SanitizedManufacturer,
            device.HidDescriptorVersionNumber);
    }

    private static LightingTransportException Translate(Exception exception)
    {
        LightingTransportFailureKind kind = exception switch
        {
            HidDeviceBusyException => LightingTransportFailureKind.DeviceBusy,
            HidDeviceDisconnectedException => LightingTransportFailureKind.DeviceDisconnected,
            TimeoutException => LightingTransportFailureKind.Timeout,
            Kick75ProtocolException => LightingTransportFailureKind.ProtocolViolation,
            HidTransportException => LightingTransportFailureKind.DeviceDisconnected,
            InvalidOperationException => LightingTransportFailureKind.ProtocolViolation,
            _ => LightingTransportFailureKind.ProtocolViolation,
        };
        return new LightingTransportException(kind, exception.Message, exception);
    }
}
