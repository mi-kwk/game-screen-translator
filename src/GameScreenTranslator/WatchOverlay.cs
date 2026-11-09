using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace GameScreenTranslator;

/// <summary>
/// ゲームの上に出す小窓。常に手前・半透明で、クリックはゲームに通す。
/// Ctrl + Shift を押している間だけ、ドラッグで動かしたり右下で大きさを変えたりできる。
/// 上端に見張りの状態(見張り中・読み取り中・翻訳中)を出す。
/// </summary>
public sealed class WatchOverlay : Window
{
    private readonly Border frame = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
    private readonly Ellipse dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock stateText = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock hintText = new() { Text = "Ctrl + Shift で移動・大きさ変更 / Ctrl + Shift + T で OFF", Foreground = Brush("#64748B"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly FlowDocumentScrollViewer body = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false };
    private readonly DispatcherTimer keyTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool movable;
    private string markdown = "";
    private string label = "";
    private DateTime since;
    private double fontSize;

    public WatchOverlay(WindowBounds? bounds, double opacity, double fontSize)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Title = "ゲーム画面翻訳(見張り)";
        MinWidth = 260; MinHeight = 120;
        var area = SystemParameters.WorkArea;
        Width = bounds?.Width ?? 520; Height = bounds?.Height ?? 300;
        Left = bounds?.Left ?? area.Right - Width - 24; Top = bounds?.Top ?? area.Bottom - Height - 24;
        this.fontSize = fontSize;

        var header = new DockPanel { Margin = new Thickness(12, 8, 12, 4), Background = Brushes.Transparent };
        DockPanel.SetDock(hintText, Dock.Right);
        header.Children.Add(hintText);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, stateText } });
        header.MouseLeftButtonDown += (_, e) => { if (movable && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(body);
        frame.Child = layout;
        Content = frame;
        SetOpacity(opacity);
        SetState(WatchState.Watching, "");
        ShowTranslation("");

        SourceInitialized += (_, _) => ScreenCapture.SetClickThrough(this, true);
        keyTimer.Tick += (_, _) => UpdateMovable();
        renderTimer.Tick += (_, _) => { renderTimer.Stop(); Render(scrollToEnd: true); };
        elapsedTimer.Tick += (_, _) => UpdateStateText();
        IsVisibleChanged += (_, _) => { if (IsVisible) keyTimer.Start(); else { keyTimer.Stop(); SetMovable(false); } };
    }

    public WindowBounds Bounds => new(Left, Top, Width, Height);

    public void SetOpacity(double opacity) =>
        frame.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, 0.4, 1) * 255), 0xF8, 0xFA, 0xFC));

    public void SetFontSize(double size) { fontSize = size; Render(); }

    public void SetState(WatchState state, string note)
    {
        (label, var color) = state switch
        {
            WatchState.Reading => ("読み取り中", "#F59E0B"),
            WatchState.Translating => ("翻訳中", "#3B82F6"),
            WatchState.Off => ("停止", "#94A3B8"),
            _ => ("見張り中", "#22C55E"),
        };
        if (note.Length > 0) label += " ― " + note;
        dot.Fill = Brush(color);
        since = DateTime.Now;
        if (state == WatchState.Translating) elapsedTimer.Start(); else elapsedTimer.Stop();
        UpdateStateText();
    }

    private void UpdateStateText() =>
        stateText.Text = label + (elapsedTimer.IsEnabled ? $"… {(DateTime.Now - since).TotalSeconds:F0} 秒" : "");

    /// <summary>訳全体を表示する(翻訳の完了時は、先頭から読めるように一番上に戻す)。</summary>
    public void ShowTranslation(string text)
    {
        markdown = text; renderTimer.Stop(); Render();
        Dispatcher.BeginInvoke(() => FindScrollViewer(body)?.ScrollToTop(), DispatcherPriority.Loaded);
    }
    public void AppendTranslation(string piece) { markdown += piece; if (!renderTimer.IsEnabled) renderTimer.Start(); }

    private void Render(bool scrollToEnd = false)
    {
        var shown = markdown.Length > 0 ? markdown : "_画面に新しい英文が出ると、ここに訳が出ます。_";
        var document = MarkdownView.ToDocument(shown, fontSize);
        document.Background = Brushes.Transparent;
        body.Document = document;
        if (scrollToEnd) Dispatcher.BeginInvoke(() => FindScrollViewer(body)?.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void UpdateMovable() => SetMovable(ScreenCapture.IsKeyDown(ScreenCapture.VK_CONTROL) && ScreenCapture.IsKeyDown(ScreenCapture.VK_SHIFT));

    private void SetMovable(bool value)
    {
        if (movable == value) return;
        movable = value;
        ScreenCapture.SetClickThrough(this, !value);
        frame.BorderBrush = value ? Brush("#3B82F6") : Brushes.Transparent;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
