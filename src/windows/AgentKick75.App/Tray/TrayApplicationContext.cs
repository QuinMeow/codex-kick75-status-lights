// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Drawing;
using AgentKick75.App.Hosting;

namespace AgentKick75.App.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly HostCoordinator coordinator;
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem pauseItem;
    private readonly Uri? controlPageUri;
    private bool paused;
    private bool exiting;

    public TrayApplicationContext(HostCoordinator coordinator, Uri? controlPageUri = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.controlPageUri = controlPageUri;

        var openItem = new ToolStripMenuItem("打开控制页", null, OpenControlPage)
        {
            Enabled = controlPageUri is not null,
        };
        pauseItem = new ToolStripMenuItem("暂停接管", null, TogglePause);
        var restoreItem = new ToolStripMenuItem("恢复原灯效", null, RestoreBaseline);
        var hardwareTestItem = new ToolStripMenuItem("硬件测试…", null, OpenControlPage)
        {
            Enabled = controlPageUri is not null,
        };
        var startupItem = new ToolStripMenuItem("开机启动（安装阶段配置）")
        {
            Enabled = false,
        };
        var exitItem = new ToolStripMenuItem("退出", null, ExitRequested);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            openItem,
            pauseItem,
            restoreItem,
            hardwareTestItem,
            startupItem,
            new ToolStripSeparator(),
            exitItem,
        ]);

        notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AgentKick75 Codex 状态灯",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += OpenControlPage;
        coordinator.StatusChanged += CoordinatorStatusChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            coordinator.StatusChanged -= CoordinatorStatusChanged;
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CoordinatorStatusChanged(object? sender, HostStatusSnapshot status)
    {
        if (Application.MessageLoop)
        {
            pauseItem.Owner?.BeginInvoke(new Action(() => ApplyStatus(status)));
        }
    }

    private void ApplyStatus(HostStatusSnapshot status)
    {
        paused = status.Paused;
        pauseItem.Text = paused ? "恢复接管" : "暂停接管";
        notifyIcon.Text = status.Paused
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
            if (paused)
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

    private async void RestoreBaseline(object? sender, EventArgs eventArgs)
    {
        try
        {
            await coordinator.RestoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
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

    private static void ShowError(string message)
    {
        MessageBox.Show(
            message,
            "AgentKick75",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
