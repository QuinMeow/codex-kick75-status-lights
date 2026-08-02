// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.Core.Baseline;
using AgentKick75.Hid.Windows;

namespace AgentKick75.App.Commands;

public sealed record HardwareTestCommandResult(
    bool Succeeded,
    string Outcome,
    string? Transport = null,
    string? DeviceState = null,
    int CompletedCycles = 0,
    bool AllBaselinesRestored = false,
    ushort? NativeInputReportLength = null,
    ushort? NativeOutputReportLength = null,
    string? InterfaceFingerprint = null,
    HardwareTestCycleResult? LastCycle = null);

public interface IHardwareTestCommand
{
    ValueTask<HardwareTestCommandResult> RunAsync(
        HardwareTestArguments arguments,
        CancellationToken cancellationToken = default);
}

public sealed class GuardedHardwareTestCommand : IHardwareTestCommand
{
    private readonly GuardedHardwareTestService service;

    public GuardedHardwareTestCommand(GuardedHardwareTestService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public static GuardedHardwareTestCommand CreateWindowsDefault(BaselineStore? baselineStore = null)
    {
        baselineStore ??= new BaselineStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentKick75",
            "baseline.json"));
        return new GuardedHardwareTestCommand(new GuardedHardwareTestService(
            new Win32HidDeviceEnumerator(),
            new HidDeviceSelector(),
            new Win32HidConnectionFactory(),
            new CoreHardwareTestBaselineJournal(baselineStore)));
    }

    public async ValueTask<HardwareTestCommandResult> RunAsync(
        HardwareTestArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var options = new HardwareTestOptions
        {
            Cycles = arguments.Cycles,
            GreenDuration = arguments.GreenDuration,
        };

        HardwareTestResult result = await service.RunAsync(options, cancellationToken).ConfigureAwait(false);
        bool passed = result.Outcome == HardwareTestOutcome.Passed;
        string outcome = $"{result.Outcome}: {result.Message}";
        return new HardwareTestCommandResult(
            passed,
            outcome,
            result.TransportProfileId,
            result.DeviceState.ToString(),
            result.Cycles.Count,
            result.AllBaselinesRestored,
            result.NativeInputReportLength,
            result.NativeOutputReportLength,
            result.InterfaceFingerprint,
            LastCycle: result.Cycles.LastOrDefault());
    }
}

public sealed class SafeUnavailableHardwareTestCommand : IHardwareTestCommand
{
    public ValueTask<HardwareTestCommandResult> RunAsync(
        HardwareTestArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new HardwareTestCommandResult(
            false,
            "Refused: no guarded hardware-test implementation was explicitly injected."));
    }
}

public static class StatusCommand
{
    public static async Task<int> ExecuteAsync(
        IPipeRequestClient pipeClient,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeClient);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            PipeEnvelope? response = await pipeClient.SendAsync(
                PipeEnvelope.Create(PipeMessageKinds.StatusRequest, new { }),
                expectResponse: true,
                TimeSpan.FromMilliseconds(500),
                cancellationToken).ConfigureAwait(false);

            if (response is null || response.Kind != PipeMessageKinds.StatusResponse)
            {
                await output.WriteLineAsync("{\"host\":\"unavailable\"}").ConfigureAwait(false);
                return 1;
            }

            if (!PipeStatusResponseDto.TryReadSafe(response.Payload, out PipeStatusResponseDto? status))
            {
                await output.WriteLineAsync("{\"host\":\"unavailable\"}").ConfigureAwait(false);
                return 1;
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(status, PipeJson.Options))
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"host\":\"offline\"}").ConfigureAwait(false);
            return 1;
        }
    }
}
