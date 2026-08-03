// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Drawing;
using AgentKick75.App.Hosting;

namespace AgentKick75.App.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly HostCoordinator coordinator;
    private readonly NotifyIcon notifyIcon;
    private readonly Icon appIcon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly Uri? controlPageUri;
    private ApplicationLifecycleState lifecycleState;
    private bool exiting;

    public TrayApplicationContext(HostCoordinator coordinator, Uri? controlPageUri = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.controlPageUri = controlPageUri;

        var openItem = new ToolStripMenuItem("打开控制页", null, OpenControlPage)
        {
            Enabled = controlPageUri is not null,
        };
        pauseItem = new ToolStripMenuItem("暂停并恢复", null, TogglePause);
        var hardwareTestItem = new ToolStripMenuItem("硬件测试…", null, OpenControlPage)
        {
            Enabled = controlPageUri is not null,
        };
        startupItem = new ToolStripMenuItem("登录时启动", null, ToggleStartup)
        {
            Checked = coordinator.StartAtLogin,
        };
        var exitItem = new ToolStripMenuItem("退出", null, ExitRequested);

        menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            openItem,
            pauseItem,
            hardwareTestItem,
            startupItem,
            new ToolStripSeparator(),
            exitItem,
        ]);
        _ = menu.Handle;

        appIcon = LoadTrayIcon();
        notifyIcon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "AgentKick75 Codex 状态灯",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += OpenControlPage;
        coordinator.StatusChanged += CoordinatorStatusChanged;
        coordinator.ShutdownRequested += CoordinatorShutdownRequested;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            coordinator.StatusChanged -= CoordinatorStatusChanged;
            coordinator.ShutdownRequested -= CoordinatorShutdownRequested;
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            menu.Dispose();
            appIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CoordinatorStatusChanged(object? sender, HostStatusSnapshot status)
    {
        if (Application.MessageLoop)
        {
            menu.BeginInvoke(new Action(() => ApplyStatus(status)));
        }
    }

    private void ApplyStatus(HostStatusSnapshot status)
    {
        lifecycleState = status.LifecycleState;
        pauseItem.Text = lifecycleState == ApplicationLifecycleState.Paused
            ? "恢复接管"
            : "暂停并恢复";
        pauseItem.Enabled = lifecycleState is ApplicationLifecycleState.Running or
            ApplicationLifecycleState.Paused;
        startupItem.Checked = coordinator.StartAtLogin;
        notifyIcon.Text = lifecycleState == ApplicationLifecycleState.Paused
            ? "AgentKick75（已暂停）"
            : $"AgentKick75（{status.AggregateState}）";
    }

    private void OpenControlPage(object? sender, EventArgs eventArgs)
    {
        if (controlPageUri is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(controlPageUri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }

    private async void TogglePause(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (lifecycleState == ApplicationLifecycleState.Paused)
            {
                await coordinator.ResumeAsync().ConfigureAwait(true);
            }
            else
            {
                await coordinator.PauseAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void ToggleStartup(object? sender, EventArgs eventArgs)
    {
        startupItem.Enabled = false;
        try
        {
            await coordinator.UpdateSettingsAsync(
                coordinator.LightingSettings,
                !coordinator.StartAtLogin).ConfigureAwait(true);
            startupItem.Checked = coordinator.StartAtLogin;
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            startupItem.Enabled = true;
        }
    }

    private void ExitRequested(object? sender, EventArgs eventArgs)
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        notifyIcon.Visible = false;
        ExitThread();
    }

    private void CoordinatorShutdownRequested(object? sender, EventArgs eventArgs)
    {
        if (Application.MessageLoop)
        {
            menu.BeginInvoke(new Action(() => ExitRequested(sender, eventArgs)));
        }
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(
            message,
            "AgentKick75",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static Icon LoadTrayIcon()
    {
        using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream(
            "AgentKick75.Assets.AgentKick75Tray.ico");
        if (stream is not null)
        {
            using var icon = new Icon(stream);
            return (Icon)icon.Clone();
        }

        return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
    }
}
