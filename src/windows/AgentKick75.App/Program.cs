// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Commands;
using AgentKick75.App.Diagnostics;
using AgentKick75.App.Hosting;
using AgentKick75.App.Hooks;
using AgentKick75.App.Ipc;
using AgentKick75.App.Lighting;
using AgentKick75.App.Tray;
using AgentKick75.App.Web;
using AgentKick75.Core.Baseline;
using AgentKick75.Core.Configuration;
using AgentKick75.Core.State;
using AgentKick75.Core.Storage;

namespace AgentKick75.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ParsedCommand command = CommandLine.Parse(args);
        if (command.Kind == AppCommandKind.HookCodex)
        {
            // Do not add logging or console output around this call. Codex command
            // hooks are deliberately silent and fail-open.
            return HookCommand.ExecuteAsync(
                    Console.In,
                    new LoopbackHookRequestClient())
                .GetAwaiter()
                .GetResult();
        }

        return command.Kind switch
        {
            AppCommandKind.Host => RunHost(),
            AppCommandKind.Status => StatusCommand.ExecuteAsync(
                    new NamedPipeRequestClient(),
                    Console.Out)
                .GetAwaiter()
                .GetResult(),
            AppCommandKind.HardwareTest => RunHardwareTest(command.HardwareTest!),
            AppCommandKind.Help => WriteUsage(Console.Out, null, 0),
            _ => WriteUsage(Console.Error, command.Error, 2),
        };
    }

    private static int RunHost()
    {
        if (!SingleInstanceLease.TryAcquire(out SingleInstanceLease? instance))
        {
            return 0;
        }

        using (instance)
        {
            ApplicationConfiguration.Initialize();
            string dataDirectory = EnsureUserDataDirectory();
            var configurationStore = new ConfigurationStore(Path.Combine(dataDirectory, "config.json"));
            ConfigurationLoadResult configurationLoad = configurationStore.LoadAsync()
                .GetAwaiter()
                .GetResult();
            AgentKick75Configuration configuration = configurationLoad.Configuration;
            SanitizedDiagnosticLog? diagnosticLog = TryCreateDiagnosticLog(dataDirectory);
            TryWriteConfigurationLoaded(diagnosticLog, configurationLoad.Status);
            var baselineStore = new BaselineStore(Path.Combine(dataDirectory, "baseline.json"));
            var worker = new HidLightingWorker(
                WindowsLightingTransport.CreateDefault(),
                new FileBaselineOwnershipStore(baselineStore));
            var coordinator = new HostCoordinator(
                new TaskStateReducer(
                    completeTtl: configuration.Lighting.CompleteTtl,
                    staleTimeout: configuration.StaleSessionTtl),
                worker,
                configuration.Lighting,
                GuardedHardwareTestCommand.CreateWindowsDefault(baselineStore),
                new CoreHostSettingsPersistence(configurationStore, configuration),
                configuration.StartAtLogin,
                diagnosticLog);
            var runtime = new HostRuntime(
                worker,
                coordinator,
                diagnosticLog: diagnosticLog);
            using var controlPlane = new HostControlPlaneAdapter(coordinator);
            AgentKick75ControlServer? controlServer = null;

            try
            {
                runtime.Start();
                try
                {
                    controlServer = AgentKick75ControlServer.StartAsync(
                            controlPlane,
                            diagnosticLog: diagnosticLog,
                            hookHandler: coordinator.HandlePipeMessageAsync,
                            hookEndpointPath: Path.Combine(
                                dataDirectory,
                                LoopbackHookEndpoint.FileName))
                        .GetAwaiter()
                        .GetResult();
                }
                catch (InvalidOperationException)
                {
                    // The Host and HID safety path remain available when the local
                    // control page cannot bind. Status reports still work via Pipe.
                }

                using var tray = new TrayApplicationContext(coordinator, controlServer?.BaseUri);
                Application.Run(tray);
                return 0;
            }
            finally
            {
                try
                {
                    controlServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    // HID restoration is the innermost shutdown guarantee even
                    // when Kestrel stop/dispose reports an unrelated failure.
                    runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
    }

    private static int RunHardwareTest(HardwareTestArguments arguments)
    {
        if (!SingleInstanceLease.TryAcquire(out SingleInstanceLease? instance))
        {
            var refused = new HardwareTestCommandResult(
                false,
                "Refused: the AgentKick75 Host or another hardware test owns the HID safety gate. " +
                "Use the Host control page, or exit the Host before running the CLI test.");
            Console.Out.WriteLine(JsonSerializer.Serialize(refused));
            return 2;
        }

        using (instance)
        {
            string dataDirectory = EnsureUserDataDirectory();
            var baselineStore = new BaselineStore(Path.Combine(dataDirectory, "baseline.json"));
            IHardwareTestCommand command = GuardedHardwareTestCommand.CreateWindowsDefault(baselineStore);
            HardwareTestCommandResult result = command.RunAsync(arguments)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            Console.Out.WriteLine(JsonSerializer.Serialize(result));
            return result.Succeeded ? 0 : 2;
        }
    }

    private static string EnsureUserDataDirectory()
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentKick75");
        return UserDataDirectorySecurity.EnsureSecureDirectory(dataDirectory);
    }

    private static SanitizedDiagnosticLog? TryCreateDiagnosticLog(string dataDirectory)
    {
        try
        {
            return new SanitizedDiagnosticLog(Path.Combine(dataDirectory, "diagnostics"));
        }
        catch (Exception)
        {
            // A diagnostics storage failure must not disable the Host or its HID
            // restoration guarantees. The sink contains no free-text fallback.
            return null;
        }
    }

    private static void TryWriteConfigurationLoaded(
        ISanitizedDiagnosticLog? diagnosticLog,
        ConfigurationLoadStatus status)
    {
        if (diagnosticLog is null)
        {
            return;
        }

        try
        {
            _ = diagnosticLog.WriteAsync(
                SanitizedDiagnosticEventType.ConfigurationLoaded,
                code: status is ConfigurationLoadStatus.Loaded or
                    ConfigurationLoadStatus.MissingUsingDefaults
                    ? SanitizedDiagnosticCode.Succeeded
                    : SanitizedDiagnosticCode.InvalidInput);
        }
        catch (Exception)
        {
        }
    }

    private static int WriteUsage(TextWriter writer, string? error, int exitCode)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            writer.WriteLine(error);
        }

        writer.WriteLine(CommandLine.Usage);
        return exitCode;
    }
}
