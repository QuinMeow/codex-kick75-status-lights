// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Installation;
using AgentKick75.App.Ipc;
using AgentKick75.App.Hosting;
using AgentKick75.Core.Baseline;
using AgentKick75.Core.Configuration;
using AgentKick75.Core.Installation;

namespace AgentKick75.App.Commands;

public sealed record InstallationCommandResult(
    bool Succeeded,
    string Operation,
    string Outcome,
    bool HooksChanged = false,
    bool NotificationChanged = false,
    bool StartupEnabled = false);

public static class InstallationCommand
{
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HostExitTimeout = TimeSpan.FromSeconds(10);

    private enum PrepareUninstallOutcome
    {
        Offline,
        Accepted,
        Rejected,
    }

    public static async Task<int> InstallAsync(
        string appExecutablePath,
        string hookExecutablePath,
        HookRegistrationManager hookManager,
        CodexNotificationRegistrationManager notificationManager,
        StartupRegistrationManager startupManager,
        ConfigurationStore configurationStore,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(appExecutablePath) || !File.Exists(hookExecutablePath))
        {
            return await WriteAsync(
                output,
                new(false, "install", "发布目录缺少 AgentKick75.exe 或 AgentKick75.Hook.exe。"));
        }

        try
        {
            HookRegistrationResult hooks = await hookManager
                .InstallAsync(hookExecutablePath, cancellationToken)
                .ConfigureAwait(false);
            CodexNotificationRegistrationResult notification = await notificationManager
                .InstallAsync(hookExecutablePath, cancellationToken)
                .ConfigureAwait(false);
            startupManager.SetEnabled(true, appExecutablePath);
            await SaveStartupPreferenceAsync(configurationStore, true, cancellationToken)
                .ConfigureAwait(false);
            return await WriteAsync(
                output,
                new(
                    true,
                    "install",
                    "已安装 Codex 集成与当前用户登录启动项；请完全重启 Codex。",
                    hooks.Changed,
                    notification.Changed,
                    StartupEnabled: true));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await WriteAsync(output, new(false, "install", exception.Message));
        }
    }

    public static async Task<int> UninstallAsync(
        string appExecutablePath,
        HookRegistrationManager hookManager,
        CodexNotificationRegistrationManager notificationManager,
        StartupRegistrationManager startupManager,
        ConfigurationStore configurationStore,
        LightingRestoreStore restoreStore,
        IPipeRequestClient pipeClient,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrepareUninstallOutcome prepare = await TryPrepareUninstallAsync(
                    pipeClient,
                    cancellationToken)
                .ConfigureAwait(false);
            if (prepare == PrepareUninstallOutcome.Rejected)
            {
                return await WriteAsync(
                    output,
                    new(
                        false,
                        "uninstall",
                        "Host 未能恢复并验证原灯效；卸载已取消，未修改外部配置。"));
            }

            if (prepare == PrepareUninstallOutcome.Accepted &&
                !await WaitForHostExitAsync(cancellationToken).ConfigureAwait(false))
            {
                return await WriteAsync(
                    output,
                    new(false, "uninstall", "Host 未在 10 秒内退出；卸载已取消。"));
            }

            if (prepare == PrepareUninstallOutcome.Offline && IsHostRunning())
            {
                return await WriteAsync(
                    output,
                    new(false, "uninstall", "Host 正在运行但未完成卸载握手；卸载已取消。"));
            }

            LightingRestoreRecord? pending = await restoreStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (pending is not null)
            {
                return await WriteAsync(
                    output,
                    new(
                        false,
                        "uninstall",
                        "检测到尚未恢复的灯效记录；请先启动 AgentKick75 完成恢复。"));
            }

            HookRegistrationResult hooks = await hookManager.UninstallAsync(cancellationToken)
                .ConfigureAwait(false);
            CodexNotificationRegistrationResult notification = await notificationManager
                .UninstallAsync(cancellationToken)
                .ConfigureAwait(false);
            startupManager.SetEnabled(false, appExecutablePath);
            await SaveStartupPreferenceAsync(configurationStore, false, cancellationToken)
                .ConfigureAwait(false);
            return await WriteAsync(
                output,
                new(
                    true,
                    "uninstall",
                    "已移除本项目的 Codex 集成与登录启动项，其他用户配置已保留。",
                    hooks.Changed,
                    notification.Changed,
                    StartupEnabled: false));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await WriteAsync(output, new(false, "uninstall", exception.Message));
        }
    }

    private static async Task<PrepareUninstallOutcome> TryPrepareUninstallAsync(
        IPipeRequestClient pipeClient,
        CancellationToken cancellationToken)
    {
        try
        {
            PipeEnvelope? response = await pipeClient.SendAsync(
                PipeEnvelope.Create(PipeMessageKinds.PrepareUninstallRequest, new { }),
                expectResponse: true,
                PrepareTimeout,
                cancellationToken).ConfigureAwait(false);
            return response?.Kind == PipeMessageKinds.Accepted
                ? PrepareUninstallOutcome.Accepted
                : PrepareUninstallOutcome.Rejected;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return PrepareUninstallOutcome.Offline;
        }
    }

    private static async Task<bool> WaitForHostExitAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + HostExitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (SingleInstanceLease.TryAcquire(out SingleInstanceLease? lease))
            {
                lease!.Dispose();
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsHostRunning()
    {
        if (!SingleInstanceLease.TryAcquire(out SingleInstanceLease? lease))
        {
            return true;
        }

        lease!.Dispose();
        return false;
    }

    private static async Task SaveStartupPreferenceAsync(
        ConfigurationStore store,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ConfigurationLoadResult loaded = await store.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        AgentKick75Configuration current = loaded.Configuration;
        await store.SaveAsync(
            new AgentKick75Configuration(
                current.Lighting,
                current.StaleSessionTtl,
                enabled,
                current.SchemaVersion),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> WriteAsync(
        TextWriter output,
        InstallationCommandResult result)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
        return result.Succeeded ? 0 : 2;
    }
}
