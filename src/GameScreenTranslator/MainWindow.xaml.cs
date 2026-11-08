using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
namespace GameScreenTranslator;
public partial class MainWindow : Window
{
    private const string NoGame = "(ゲームを選ばない)";
    // 訳が届き始めるまで(モデルの初回読み込みを含む)の上限。届き始めた後は中止ボタンで止める。
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly DispatcherTimer workingTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // 訳が少しずつ届く間、表示の作り直しを間引く。
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private AppSettings settings = AppSettings.Default;
    private BitmapSource? image;
    private bool busy;
    private bool updatingSelector;
    private CancellationTokenSource? translation;
    private DateTime workingSince;
    private string workingLabel = "";
    private string translated = "";

    public MainWindow()
    {
        InitializeComponent();
        try { settings = SettingsStore.Load(); }
        catch { Status.Text = "保存済みの設定を読み込めませんでした。初期設定で起動しました。"; }
        RefreshGameSelector();
        RenderTranslation();
        workingTimer.Tick += (_, _) => UpdateWorkingText();
        renderTimer.Tick += (_, _) => { renderTimer.Stop(); RenderTranslation(scrollToEnd: true); };
        Loaded += async (_, _) => await StartOllamaInBackgroundAsync();
        Closed += (_, _) => { translation?.Cancel(); workingTimer.Stop(); renderTimer.Stop(); client.Dispose(); OllamaLauncher.StopStarted(); };
    }

    // ---- 設定とゲームの切り替え ----
    private void SaveSettings()
    {
        try { SettingsStore.Save(settings); }
        catch { Status.Text = "設定を保存できませんでした。"; }
    }
    private void RefreshGameSelector()
    {
        updatingSelector = true;
        GameSelector.Items.Clear();
        GameSelector.Items.Add(NoGame);
        foreach (var game in settings.Games) GameSelector.Items.Add(game.Name);
        GameSelector.SelectedItem = settings.CurrentGame?.Name ?? NoGame;
        updatingSelector = false;
    }
    private void GameSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSelector || GameSelector.SelectedItem is not string name) return;
        settings = settings with { SelectedGame = name == NoGame ? "" : name };
        SaveSettings();
        Status.Text = settings.CurrentGame is { } game ? $"「{game.Name}」の単語辞書と共通の単語辞書で翻訳します。" : "共通の単語辞書だけで翻訳します。";
    }
    private async void SettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        settings = dialog.Result;
        SaveSettings();
        RefreshGameSelector();
        Status.Text = "設定を保存しました。";
        await StartOllamaInBackgroundAsync();
    }

    // ---- Ollama の自動起動 ----
    private async Task StartOllamaInBackgroundAsync()
    {
        if (!settings.AutoStartOllama || !OllamaLauncher.IsLocal(settings.OllamaUrl)) return;
        try { if (await OllamaLauncher.EnsureRunningAsync(client, settings.OllamaUrl)) Status.Text = "Ollama を起動しました。画像を貼り付けてください。"; }
        catch (InvalidOperationException ex) { Status.Text = ex.Message; }
    }
    private async Task EnsureOllamaAsync(CancellationToken cancellationToken)
    {
        if (!settings.AutoStartOllama || await OllamaLauncher.IsRunningAsync(client, settings.OllamaUrl, cancellationToken)) return;
        ShowWorking("Ollama を起動しています");
        await OllamaLauncher.EnsureRunningAsync(client, settings.OllamaUrl, cancellationToken);
    }

    // ---- 処理中の表示 ----
    /// <summary>処理中の表示(動き続けるバーと経過秒数)を出す。所要時間は分からないので、進み具合ではなく「動いていること」を示す。</summary>
    private void ShowWorking(string label)
    {
        workingLabel = label;
        if (Working.Visibility != Visibility.Visible) { workingSince = DateTime.Now; Working.Visibility = Visibility.Visible; workingTimer.Start(); }
        UpdateWorkingText();
    }
    private void UpdateWorkingText() => WorkingText.Text = $"{workingLabel}… {(DateTime.Now - workingSince).TotalSeconds:F0} 秒";
    private void HideWorking() { workingTimer.Stop(); Working.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Collapsed; }

    // ---- 訳の表示 ----
    private void SetTranslation(string text) { translated = text; renderTimer.Stop(); RenderTranslation(); }
    private void RenderTranslation(bool scrollToEnd = false)
    {
        Japanese.Document = MarkdownView.ToDocument(translated, settings.FontSize);
        if (scrollToEnd) Dispatcher.BeginInvoke(() => FindScrollViewer(Japanese)?.ScrollToEnd(), DispatcherPriority.Loaded);
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
    private void ChangeFontSize(double delta)
    {
        settings = settings with { FontSize = Math.Clamp(settings.FontSize + delta, 12, 32) };
        RenderTranslation();
        SaveSettings();
        Status.Text = $"訳の文字の大きさ: {settings.FontSize:F0}";
    }
    private void SmallerClick(object sender, RoutedEventArgs e) => ChangeFontSize(-1);
    private void LargerClick(object sender, RoutedEventArgs e) => ChangeFontSize(+1);

    // ---- 読み取りと翻訳 ----
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; Actions.IsEnabled = false; GameSelector.IsEnabled = false;
        try { await action(); }
        catch (TaskCanceledException) { Status.Text = "Ollama から訳が届かないため中止しました。英文を短くするか、小さいモデルを試してください。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { HideWorking(); busy = false; Actions.IsEnabled = true; GameSelector.IsEnabled = true; }
    }
    private async Task SetImageAsync(BitmapSource bitmap)
    {
        image = bitmap; Preview.Source = bitmap; English.Clear(); SetTranslation("");
        await ReadAsync();
    }
    private async Task ReadAsync()
    {
        if (image is null) throw new InvalidOperationException("先に画像を貼り付けるか、画像ファイルを開いてください。");
        SetTranslation(""); Status.Text = "英語を読み取っています…";
        English.Text = await OcrService.ReadAsync(image);
        if (string.IsNullOrWhiteSpace(English.Text)) { Status.Text = "文字を検出できませんでした。文字が大きく写るように撮影してください。"; return; }
        Status.Text = "読み取り完了。英文を修正してから翻訳することもできます。";
        if (AutoTranslate.IsChecked == true) await TranslateAsync();
    }
    private async Task TranslateAsync()
    {
        SetTranslation("");
        using var cancel = new CancellationTokenSource();
        translation = cancel;
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            await EnsureOllamaAsync(cancel.Token);
            var receiving = false;
            Status.Text = settings.CurrentGame is { } game
                ? $"「{game.Name}」の単語辞書で翻訳しています。最初の 1 回はモデルの読み込みに時間がかかります。"
                : "日本語に翻訳しています。最初の 1 回はモデルの読み込みに時間がかかります。";
            ShowWorking("モデルを準備しています");
            var result = await new TranslationService(client).TranslateAsync(English.Text, settings, piece =>
            {
                if (!receiving) { receiving = true; ShowWorking("翻訳しています"); }
                translated += piece;
                if (!renderTimer.IsEnabled) renderTimer.Start();
            }, cancel.Token);
            SetTranslation(result);
            Status.Text = $"翻訳が完了しました({(DateTime.Now - workingSince).TotalSeconds:F0} 秒)。";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            SetTranslation(translated);
            Status.Text = translated.Length > 0 ? "翻訳を中止しました。途中までの訳を表示しています。" : "翻訳を中止しました。";
        }
        finally { translation = null; HideWorking(); }
    }
    private void CancelTranslation()
    {
        if (translation is null) return;
        translation.Cancel();
        WorkingText.Text = "中止しています…";
    }
    private void CancelClick(object sender, RoutedEventArgs e) => CancelTranslation();

    // ---- 入力 ----
    private Task PasteAsync() => RunAsync(async () => {
        if (!Clipboard.ContainsImage()) throw new InvalidOperationException("クリップボードに画像がありません。Windows + Shift + Sで撮影してください。");
        await SetImageAsync(Clipboard.GetImage() ?? throw new InvalidOperationException("画像を取得できませんでした。撮影し直してください。"));
    });
    private async void PasteClick(object sender, RoutedEventArgs e) => await PasteAsync();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.Escape && translation is not null) { e.Handled = true; CancelTranslation(); }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add) { e.Handled = true; ChangeFontSize(+1); }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { e.Handled = true; ChangeFontSize(-1); }
        else if (e.Key == Key.V && ctrl && Clipboard.ContainsImage())
        { e.Handled = true; await PasteAsync(); }
    }
    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog() == true) await RunAsync(() => LoadFileAsync(dialog.FileName));
    }
    private async Task LoadFileAsync(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        await SetImageAsync(bitmap);
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            await RunAsync(() => LoadFileAsync(paths[0]));
    }
    private async void OcrClick(object sender, RoutedEventArgs e) => await RunAsync(ReadAsync);
    private async void TranslateClick(object sender, RoutedEventArgs e) => await RunAsync(TranslateAsync);
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        try { if (translated.Length > 0) { Clipboard.SetText(MarkdownView.ToPlainText(translated)); Status.Text = "日本語訳をコピーしました(見出しや太字の記号は外しています)。"; } }
        catch { Status.Text = "クリップボードを使用できません。再試行してください。"; }
    }
}
