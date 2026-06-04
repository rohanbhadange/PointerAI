using System.Drawing;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
using ClickyClone.Core;
using ClickyClone.Services;
using Application = System.Windows.Application;

namespace ClickyClone.UI;

public sealed class TrayController : IDisposable
{
    private readonly CompanionManager companionManager;
    private readonly Action? openSetup;
    private readonly NotifyIcon notifyIcon;
    private CompanionPanelWindow? panelWindow;

    public TrayController(CompanionManager companionManager, Action? openSetup = null)
    {
        this.companionManager = companionManager;
        this.openSetup = openSetup;
        notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "ClickyClone",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        notifyIcon.MouseClick += HandleTrayIconClicked;
        companionManager.PropertyChanged += HandleCompanionPropertyChanged;
        ShowPanel();
    }

    private void HandleTrayIconClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowPanel();
        }
    }

    private void ShowPanel()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (panelWindow is { IsVisible: true })
            {
                panelWindow.Activate();
                return;
            }

            panelWindow = new CompanionPanelWindow(companionManager);
            panelWindow.Closed += (_, _) => panelWindow = null;
            panelWindow.ShowNearCursor();
        });
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open status", null, (_, _) => ShowPanel());
        menu.Items.Add("Setup", null, (_, _) => openSetup?.Invoke());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add("Quit", null, (_, _) => companionManager.Quit());
        return menu;
    }

    private static void OpenLog()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = AppLogger.LogPath,
                UseShellExecute = true
            };
            Process.Start(startInfo);
        }
        catch (Exception error)
        {
            AppLogger.Error("Opening log failed", error);
        }
    }

    private void HandleCompanionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var status = companionManager.Status;
        if (status.VoiceState != CompanionVoiceState.Error || string.IsNullOrWhiteSpace(status.LastError))
        {
            return;
        }

        notifyIcon.BalloonTipTitle = "ClickyClone needs attention";
        notifyIcon.BalloonTipText = $"{status.StatusText}: {status.LastError}";
        notifyIcon.ShowBalloonTip(5000);
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(45, 160, 255));
        var points = new[]
        {
            new PointF(16, 4),
            new PointF(6, 26),
            new PointF(27, 20)
        };
        graphics.FillPolygon(brush, points);
        using var pen = new Pen(Color.FromArgb(180, 230, 255), 2);
        graphics.DrawPolygon(pen, points);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        notifyIcon.MouseClick -= HandleTrayIconClicked;
        companionManager.PropertyChanged -= HandleCompanionPropertyChanged;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        panelWindow?.Close();
    }
}
