using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nudge.Core;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;

namespace Nudge.UI;

public sealed class CompanionPanelWindow : Window
{
    private readonly CompanionManager companionManager;
    private readonly TextBlock statusText = new();
    private readonly TextBlock lastTranscriptText = new();
    private readonly TextBlock lastResponseText = new();
    private readonly Border statusDot = new();

    public CompanionPanelWindow(CompanionManager companionManager)
    {
        this.companionManager = companionManager;
        companionManager.PropertyChanged += HandleCompanionPropertyChanged;

        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Deactivated += (_, _) => Hide();

        Content = BuildContent();
        UpdateStatus();
    }

    public void ShowNearCursor()
    {
        var cursor = Forms.Cursor.Position;
        Left = cursor.X - Width - 18;
        Top = Math.Max(12, cursor.Y + 18);
        Show();
        Activate();
    }

    private UIElement BuildContent()
    {
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(19, 21, 24)),
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 52, 59)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                ShadowDepth = 8,
                Opacity = 0.45
            }
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        root.Child = stack;

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        statusDot.Width = 9;
        statusDot.Height = 9;
        statusDot.CornerRadius = new CornerRadius(5);
        statusDot.Margin = new Thickness(0, 0, 8, 0);

        var title = new TextBlock
        {
            Text = "Nudge",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeButton = MakeButton("x", () => Hide(), 28);
        DockPanel.SetDock(closeButton, Dock.Right);
        header.Children.Add(closeButton);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(statusDot);
        titleRow.Children.Add(title);
        header.Children.Add(titleRow);
        stack.Children.Add(header);

        statusText.Foreground = new SolidColorBrush(Color.FromRgb(169, 177, 190));
        statusText.FontSize = 13;
        statusText.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(statusText);

        var hint = new TextBlock
        {
            Text = "Hold Control + Alt to talk.",
            Foreground = new SolidColorBrush(Color.FromRgb(223, 229, 237)),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 14)
        };
        stack.Children.Add(hint);

        stack.Children.Add(MakeSectionLabel("Last transcript"));
        lastTranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(138, 147, 161));
        lastTranscriptText.TextWrapping = TextWrapping.Wrap;
        lastTranscriptText.Margin = new Thickness(0, 2, 0, 12);
        stack.Children.Add(lastTranscriptText);

        stack.Children.Add(MakeSectionLabel("Last response"));
        lastResponseText.Foreground = new SolidColorBrush(Color.FromRgb(138, 147, 161));
        lastResponseText.TextWrapping = TextWrapping.Wrap;
        lastResponseText.Margin = new Thickness(0, 2, 0, 16);
        stack.Children.Add(lastResponseText);

        var footer = new DockPanel();
        var quitButton = MakeButton("Quit", companionManager.Quit, 70);
        DockPanel.SetDock(quitButton, Dock.Right);
        footer.Children.Add(quitButton);
        stack.Children.Add(footer);

        return root;
    }

    private static TextBlock MakeSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Foreground = new SolidColorBrush(Color.FromRgb(87, 96, 110)),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        };
    }

    private static System.Windows.Controls.Button MakeButton(string text, Action action, double width)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            Width = width,
            Height = 28,
            Background = new SolidColorBrush(Color.FromRgb(38, 43, 51)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 75)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void HandleCompanionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(UpdateStatus);
    }

    private void UpdateStatus()
    {
        var status = companionManager.Status;
        statusText.Text = status.LastError is not null
            ? $"{status.StatusText}: {status.LastError}"
            : status.StatusText;

        lastTranscriptText.Text = string.IsNullOrWhiteSpace(status.LastTranscript)
            ? "Nothing yet."
            : status.LastTranscript;

        lastResponseText.Text = string.IsNullOrWhiteSpace(status.LastResponse)
            ? "Nothing yet."
            : status.LastResponse;

        statusDot.Background = status.VoiceState switch
        {
            CompanionVoiceState.Listening => new SolidColorBrush(Color.FromRgb(63, 169, 255)),
            CompanionVoiceState.Processing => new SolidColorBrush(Color.FromRgb(108, 130, 255)),
            CompanionVoiceState.Responding => new SolidColorBrush(Color.FromRgb(63, 169, 255)),
            CompanionVoiceState.Error => new SolidColorBrush(Color.FromRgb(255, 93, 93)),
            _ => new SolidColorBrush(Color.FromRgb(68, 217, 127))
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        companionManager.PropertyChanged -= HandleCompanionPropertyChanged;
        base.OnClosed(e);
    }
}
