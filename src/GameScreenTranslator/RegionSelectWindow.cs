using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace GameScreenTranslator;

/// <summary>画面全体を薄暗くして、見張る範囲をドラッグで選ぶ窓。Esc でやめる。</summary>
public sealed class RegionSelectWindow : Window
{
    private readonly Canvas canvas = new();
    private readonly Rectangle selection = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 135, 206, 250)), Visibility = Visibility.Collapsed };
    private Point startPoint;
    private (int X, int Y) startCursor;
    private bool dragging;

    /// <summary>選んだ範囲(物理ピクセル)。やめたときは null。</summary>
    public ScreenRect? Result { get; private set; }

    public RegionSelectWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        Topmost = true; ShowInTaskbar = false; Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 30, 41, 59)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 16, 10),
            Child = new TextBlock { Text = "見張る範囲をドラッグで囲んでください(Esc でやめる)", Foreground = Brushes.White, FontSize = 18 },
        };
        Canvas.SetLeft(hint, SystemParameters.PrimaryScreenWidth / 2 - 220 - Left); Canvas.SetTop(hint, 40 - Top);
        canvas.Children.Add(hint);
        canvas.Children.Add(selection);
        Content = canvas;
        Loaded += (_, _) => { Activate(); Focus(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
        MouseLeftButtonDown += (_, e) =>
        {
            dragging = true; startPoint = e.GetPosition(canvas); startCursor = ScreenCapture.CursorPosition();
            selection.Visibility = Visibility.Visible; UpdateSelection(startPoint); CaptureMouse();
        };
        MouseMove += (_, e) => { if (dragging) UpdateSelection(e.GetPosition(canvas)); };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!dragging) return;
            dragging = false; ReleaseMouseCapture();
            var end = ScreenCapture.CursorPosition();
            var rect = new ScreenRect(Math.Min(startCursor.X, end.X), Math.Min(startCursor.Y, end.Y), Math.Abs(end.X - startCursor.X), Math.Abs(end.Y - startCursor.Y));
            if (rect.Width < 40 || rect.Height < 20) { selection.Visibility = Visibility.Collapsed; return; } // 小さすぎるときは選び直し
            Result = rect;
            DialogResult = true;
        };
    }

    private void UpdateSelection(Point current)
    {
        Canvas.SetLeft(selection, Math.Min(startPoint.X, current.X)); Canvas.SetTop(selection, Math.Min(startPoint.Y, current.Y));
        selection.Width = Math.Abs(current.X - startPoint.X); selection.Height = Math.Abs(current.Y - startPoint.Y);
    }
}
