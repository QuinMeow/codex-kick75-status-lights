// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentKick75.App.Commands;
using AgentKick75.App.Hosting;
using AgentKick75.App.Lighting;
using AgentKick75.Core.Installation;
using AgentKick75.Core.Lighting;
using AgentKick75.Core.State;

namespace AgentKick75.App.Web;

/// <summary>
/// Maps the Host domain to the deliberately privacy-trimmed loopback API.
/// </summary>
public sealed class HostControlPlaneAdapter : IControlPlane, IDisposable
{
    private const int SubscriberCapacity = 64;
    private const string UsbTransportProfile = "kick75-usb";
    private const string DongleTransportProfile = "kick75-u1-dongle";
    private const string HighDiagnosticTransportProfile = "kick75-high-diagnostic";

    private readonly HostCoordinator coordinator;
    private readonly HookRegistrationManager? hookRegistrationManager;
    private readonly CodexNotificationRegistrationManager? notificationRegistrationManager;
    private readonly string? executablePath;
    private readonly object subscribersGate = new();
    private readonly Dictionary<long, Channel<ControlEventDto>> subscribers = [];
    private long nextSubscriberId;
    private long sequence;
    private bool disposed;

    public HostControlPlaneAdapter(
        HostCoordinator coordinator,
        HookRegistrationManager? hookRegistrationManager = null,
        CodexNotificationRegistrationManager? notificationRegistrationManager = null,
        string? executablePath = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        if ((hookRegistrationManager is null) != (executablePath is null)
            || (notificationRegistrationManager is null) != (executablePath is null))
        {
            throw new ArgumentException(
                "Hook registration manager and executable path must be supplied together.");
        }

        this.hookRegistrationManager = hookRegistrationManager;
        this.notificationRegistrationManager = notificationRegistrationManager;
        this.executablePath = executablePath;
        coordinator.StatusChanged += CoordinatorStatusChanged;
    }

    public ValueTask<ControlStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToStatus(coordinator.GetStatus()));
    }

    public ValueTask<ControlSettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToSettings(coordinator.LightingSettings, coordinator.StartAtLogin));
    }

    public async ValueTask<ControlSettingsDto> ApplySettingsAsync(
        ControlSettingsDto settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LightingSettings effective = FromSettings(settings);
        effective = await coordinator.UpdateSettingsAsync(
            effective,
            settings.LaunchAtSignIn,
            cancellationToken).ConfigureAwait(false);
        return ToSettings(effective, coordinator.StartAtLogin);
    }

    public ValueTask PreviewAsync(
        ControlPreviewState state,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        TaskVisualState visualState = state switch
        {
            ControlPreviewState.Thinking => TaskVisualState.Thinking,
            ControlPreviewState.RequiresInput => TaskVisualState.RequiresInput,
            ControlPreviewState.Complete => TaskVisualState.Complete,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        return coordinator.PreviewAsync(visualState, duration, cancellationToken);
    }

    public async ValueTask<ControlStatusDto> SetPausedAsync(
        bool isPaused,
        CancellationToken cancellationToken)
    {
        if (isPaused)
        {
            await coordinator.PauseAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await coordinator.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }

        return ToStatus(coordinator.GetStatus());
    }

    public ValueTask RestoreOriginalLightingAsync(CancellationToken cancellationToken)
    {
        return coordinator.RestoreAsync(cancellationToken);
    }

    public async ValueTask<HardwareTestResultDto> RunHardwareTestAsync(
        HardwareTestRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryParseTransport(request.Transport, out HardwareTransportChoice transport))
        {
            return new HardwareTestResultDto(false, "refused", "Unknown transport.", null);
        }

        HardwareTestCommandResult result = await coordinator.RunHardwareTestAsync(
            new HardwareTestArguments(transport),
            cancellationToken).ConfigureAwait(false);
        return new HardwareTestResultDto(
            result.Succeeded,
            result.Succeeded ? "passed" : "refused",
            result.Outcome,
            result.Transport);
    }

    public async ValueTask<HookInstallationResultDto> InstallCodexHooksAsync(
        CancellationToken cancellationToken)
    {
        if (hookRegistrationManager is null
            || notificationRegistrationManager is null
            || executablePath is null)
        {
            return new HookInstallationResultDto(
                false,
                false,
                0,
                "unavailable",
                "当前主程序不支持安装 Codex Hook。");
        }

        try
        {
            HookRegistrationResult result = await hookRegistrationManager
                .InstallAsync(executablePath, cancellationToken)
                .ConfigureAwait(false);
            CodexNotificationRegistrationResult notification = await notificationRegistrationManager
                .InstallAsync(executablePath, cancellationToken)
                .ConfigureAwait(false);
            bool succeeded = result.RegisteredHandlerCount ==
                HookRegistrationManager.RequiredHandlerCount
                && notification.Registered;
            if (succeeded &&
                coordinator.GetStatus().HookEnablement != HookEnablementState.Enabled)
            {
                coordinator.SetHookEnablement(HookEnablementState.Unconfirmed);
            }

            return new HookInstallationResultDto(
                succeeded,
                result.Changed || notification.Changed,
                result.RegisteredHandlerCount,
                succeeded ? "installed" : "incomplete",
                succeeded
                    ? result.Changed || notification.Changed
                        ? "Codex Hook 与完成通知已安装。请完全重启 Codex。"
                        : "Codex Hook 与完成通知已安装，无需修改。"
                    : "Codex 集成安装不完整，请重试。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            coordinator.SetHookEnablement(HookEnablementState.Disabled);
            return new HookInstallationResultDto(
                false,
                false,
                0,
                "failed",
                "Codex Hook 安装失败，请确认当前用户配置可写后重试。");
        }
    }

    public async ValueTask<BaselineRecoveryDispositionDto> AbandonMismatchedBaselineAsync(
        BaselineRecoveryDispositionRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BaselineMismatchRecoveryResult result = await coordinator
            .AbandonMismatchedBaselineAsync(
                request.ConfirmationId,
                request.Confirmed,
                cancellationToken)
            .ConfigureAwait(false);
        return new BaselineRecoveryDispositionDto(
            result.Succeeded,
            result.Status.ToString(),
            result.Message);
    }

    public IAsyncEnumerable<ControlEventDto> WatchEventsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Channel<ControlEventDto> subscription = CreateSubscription();
        long subscriberId;
        lock (subscribersGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            subscriberId = ++nextSubscriberId;
            subscribers.Add(subscriberId, subscription);
        }

        return ReadSubscriptionAsync(subscriberId, subscription, cancellationToken);
    }

    private async IAsyncEnumerable<ControlEventDto> ReadSubscriptionAsync(
        long subscriberId,
        Channel<ControlEventDto> subscription,
        CancellationToken subscriptionCancellation,
        [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
    {
        CancellationTokenSource? linkedCancellation = null;
        CancellationToken effectiveCancellation;
        if (!subscriptionCancellation.CanBeCanceled)
        {
            effectiveCancellation = enumerationCancellation;
        }
        else if (!enumerationCancellation.CanBeCanceled ||
                 subscriptionCancellation == enumerationCancellation)
        {
            effectiveCancellation = subscriptionCancellation;
        }
        else
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                subscriptionCancellation,
                enumerationCancellation);
            effectiveCancellation = linkedCancellation.Token;
        }

        try
        {
            await foreach (ControlEventDto item in subscription.Reader
                .ReadAllAsync(effectiveCancellation)
                .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            lock (subscribersGate)
            {
                subscribers.Remove(subscriberId);
            }

            subscription.Writer.TryComplete();
            linkedCancellation?.Dispose();
        }
    }

    public void Dispose()
    {
        Channel<ControlEventDto>[] subscriptions;
        lock (subscribersGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            subscriptions = subscribers.Values.ToArray();
            subscribers.Clear();
        }

        coordinator.StatusChanged -= CoordinatorStatusChanged;
        foreach (Channel<ControlEventDto> subscription in subscriptions)
        {
            subscription.Writer.TryComplete();
        }
    }

    private void CoordinatorStatusChanged(object? sender, HostStatusSnapshot _)
    {
        lock (subscribersGate)
        {
            if (disposed)
            {
                return;
            }

            // Concurrent callbacks can arrive out of order. Read the latest Host
            // snapshot only after taking the publication lock so sequence order
            // can never publish an older callback snapshot after a newer one.
            ControlStatusDto mappedStatus = ToStatus(coordinator.GetStatus());
            var controlEvent = new ControlEventDto(
                ++sequence,
                "status",
                DateTimeOffset.UtcNow,
                mappedStatus,
                mappedStatus.Device.LastErrorCode);
            foreach (Channel<ControlEventDto> subscription in subscribers.Values)
            {
                subscription.Writer.TryWrite(controlEvent);
            }
        }
    }

    private static ControlStatusDto ToStatus(HostStatusSnapshot status)
    {
        LightingWorkerSnapshot lighting = status.Lighting;
        return new ControlStatusDto(
            status.AggregateState.ToString(),
            status.SessionCount,
            status.LastEventAtUtc,
            status.Paused,
            status.IsPreviewActive,
            status.HookEnablement.ToString(),
            new DeviceDiagnosticsDto(
                DeviceModel(lighting),
                lighting.TransportProfile ?? "none",
                ReceiverStatus(lighting),
                KeyboardStatus(lighting),
                SupportStatus(lighting),
                HidDescriptorVersion(lighting),
                ControlPlanePrivacy.SafeDeviceIdentity(lighting.DeviceIdentity),
                lighting.LastFailure?.ToString(),
                lighting.InterfaceFingerprint),
            ToBaselineRecovery(lighting.BaselineMismatch));
    }

    private static string DeviceModel(LightingWorkerSnapshot lighting)
    {
        string? product = lighting.DescriptorMetadata?.Product;
        string? manufacturer = lighting.DescriptorMetadata?.Manufacturer;
        if (product is not null)
        {
            return manufacturer is null ||
                product.Contains(manufacturer, StringComparison.OrdinalIgnoreCase)
                ? product
                : $"{manufacturer} {product}";
        }

        if (manufacturer is not null)
        {
            return manufacturer;
        }

        return lighting.TransportProfile switch
        {
            UsbTransportProfile => "Kick75 USB HID device",
            DongleTransportProfile => "Kick75 U1 receiver",
            HighDiagnosticTransportProfile => "Kick75 High HID device",
            _ => "Unknown HID device",
        };
    }

    private static string? HidDescriptorVersion(LightingWorkerSnapshot lighting)
    {
        ushort? versionNumber = lighting.DescriptorMetadata?.HidDescriptorVersionNumber;
        return versionNumber is null
            ? null
            : $"HID descriptor bcdDevice 0x{versionNumber.Value:X4}";
    }

    private static BaselineRecoveryRiskDto? ToBaselineRecovery(
        BaselineIdentityMismatchNotice? mismatch)
    {
        return mismatch is null
            ? null
            : new BaselineRecoveryRiskDto(
                "DeviceIdentityMismatch",
                mismatch.ConfirmationId,
                "Automatic recovery was refused because the currently observed device does not match the owned baseline. Abandoning ownership never writes the old bytes and pauses lighting control.",
                ControlPlanePrivacy.SafeDeviceIdentity(mismatch.BaselineDeviceIdentity),
                ControlPlanePrivacy.SafeDeviceIdentity(mismatch.ObservedDeviceIdentity));
    }

    private static string SupportStatus(LightingWorkerSnapshot lighting)
    {
        if (lighting.DeviceSupport == LightingDeviceSupport.DiagnosticOnly &&
            (string.Equals(
                    lighting.TransportProfile,
                    DongleTransportProfile,
                    StringComparison.Ordinal) ||
                string.Equals(
                    lighting.TransportProfile,
                    HighDiagnosticTransportProfile,
                    StringComparison.Ordinal)))
        {
            return "DiagnosticOnly";
        }

        if (lighting.DeviceSupport == LightingDeviceSupport.Writable &&
            string.Equals(
                lighting.TransportProfile,
                UsbTransportProfile,
                StringComparison.Ordinal))
        {
            return lighting.DeviceObservation == LightingDeviceObservationKind.RuntimeSession
                ? "USB allowlisted; runtime session observed"
                : lighting.DeviceObservation == LightingDeviceObservationKind.Descriptor
                    ? "USB allowlisted; descriptor observed"
                    : "Unknown";
        }

        return "Unknown";
    }

    private static string ReceiverStatus(LightingWorkerSnapshot lighting)
    {
        if (string.Equals(
                lighting.TransportProfile,
                DongleTransportProfile,
                StringComparison.Ordinal))
        {
            if (lighting.LastFailure is LightingTransportFailureKind.ReceiverUnavailable)
            {
                return "Unavailable";
            }

            return lighting.LastFailure is LightingTransportFailureKind.DeviceDisconnected
                ? "Disconnected"
                : "Present";
        }

        return lighting.TransportProfile is null ? "Unknown" : "NotApplicable";
    }

    private static string KeyboardStatus(LightingWorkerSnapshot lighting)
    {
        return lighting.LastFailure switch
        {
            LightingTransportFailureKind.DeviceDisconnected => "Disconnected",
            LightingTransportFailureKind.KeyboardSleeping => "SleepingOrUnresponsive",
            LightingTransportFailureKind.DeviceBusy => "DeviceBusy",
            LightingTransportFailureKind.ProtocolViolation => "InvalidResponse",
            _ when lighting.State is LightingWorkerState.Active => "Ready",
            _ => "Unknown",
        };
    }

    private static ControlSettingsDto ToSettings(LightingSettings settings, bool startAtLogin)
    {
        return new ControlSettingsDto(
            ToStyle(settings.Thinking),
            ToStyle(settings.RequiresInput),
            ToStyle(settings.Complete),
            checked((int)settings.CompleteTtl.TotalSeconds),
            LaunchAtSignIn: startAtLogin);
    }

    private static ControlLightStyleDto ToStyle(LightStyle style)
    {
        return new ControlLightStyleDto(style.Color.ToString(), style.Brightness);
    }

    private static LightingSettings FromSettings(ControlSettingsDto settings)
    {
        if (settings.CompleteHoldSeconds is < 1 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Complete hold must be 1-3600 seconds.");
        }

        return new LightingSettings(
            FromStyle(settings.Thinking),
            FromStyle(settings.RequiresInput),
            FromStyle(settings.Complete),
            TimeSpan.FromSeconds(settings.CompleteHoldSeconds));
    }

    private static LightStyle FromStyle(ControlLightStyleDto style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new LightStyle(RgbColor.Parse(style.Color), style.Brightness);
    }

    private static bool TryParseTransport(string value, out HardwareTransportChoice transport)
    {
        transport = value switch
        {
            "auto" => HardwareTransportChoice.Auto,
            "usb" => HardwareTransportChoice.Usb,
            "dongle" => HardwareTransportChoice.Dongle,
            _ => default,
        };
        return value is "auto" or "usb" or "dongle";
    }

    private static Channel<ControlEventDto> CreateSubscription()
    {
        return Channel.CreateBounded<ControlEventDto>(new BoundedChannelOptions(SubscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    }
}
