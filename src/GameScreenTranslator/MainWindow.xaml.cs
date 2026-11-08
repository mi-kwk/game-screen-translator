using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
namespace GameScreenTranslator;
public partial class MainWindow : Window
{
    // 訳が届き始めるまで(モデルの初回読み込みを含む)の上限。届き始めた後は中止ボタンで止める。
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly DispatcherTimer workingTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private BitmapSource? image;
    private bool busy;
    private CancellationTokenSource? translation;
    private DateTime workingSince;
    private string workingLabel = "";
    public MainWindow()
    {
        InitializeComponent();
        var settings = AppSettings.Default;
        try { settings = SettingsStore.Load(); }
        catch { Status.Text = "保存済みの設定を読み込めませんでした。初期設定で起動しました。"; }
        ShowSettings(settings);
        workingTimer.Tick += (_, _) => UpdateWorkingText();
        Closed += (_, _) => { translation?.Cancel(); workingTimer.Stop(); client.Dispose(); };
    }
    private void ShowSettings(AppSettings settings)
    {
        Model.Text = settings.Model; OllamaUrl.Text = settings.OllamaUrl;
        GameName.Text = settings.GameName; Glossary.Text = settings.Glossary;
    }
    private AppSettings CurrentSettings() => new(OllamaUrl.Text.Trim(), Model.Text.Trim(), GameName.Text.Trim(), Glossary.Text.Trim());

    /// <summary>処理中の表示(動き続けるバーと経過秒数)を出す。所要時間は分からないので、進み具合ではなく「動いていること」を示す。</summary>
    private void ShowWorking(string label)
    {
        workingLabel = label;
        if (Working.Visibility != Visibility.Visible) { workingSince = DateTime.Now; Working.Visibility = Visibility.Visible; workingTimer.Start(); }
        UpdateWorkingText();
    }
    private void UpdateWorkingText() => WorkingText.Text = $"{workingLabel}… {(DateTime.Now - workingSince).TotalSeconds:F0} 秒";
    private void HideWorking() { workingTimer.Stop(); Working.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Collapsed; }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; Actions.IsEnabled = false;
        try { await action(); }
        catch (TaskCanceledException) { Status.Text = "Ollama から訳が届かないため中止しました。英文を短くするか、小さいモデルを試してください。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { HideWorking(); busy = false; Actions.IsEnabled = true; }
    }
    private async Task SetImageAsync(BitmapSource bitmap)
    {
        image = bitmap; Preview.Source = bitmap; English.Clear(); Japanese.Clear();
        await ReadAsync();
    }
    private async Task ReadAsync()
    {
        if (image is null) throw new InvalidOperationException("先に画像を貼り付けるか、画像ファイルを開いてください。");
        Japanese.Clear(); Status.Text = "英語を読み取っています…";
        English.Text = await OcrService.ReadAsync(image);
        if (string.IsNullOrWhiteSpace(English.Text)) { Status.Text = "文字を検出できませんでした。文字が大きく写るように撮影してください。"; return; }
        Status.Text = "読み取り完了。英文を修正してから翻訳することもできます。";
        if (AutoTranslate.IsChecked == true) await TranslateAsync();
    }
    private async Task TranslateAsync()
    {
        Japanese.Clear();
        using var cancel = new CancellationTokenSource();
        translation = cancel;
        var receiving = false;
        Status.Text = "日本語に翻訳しています。最初の 1 回はモデルの読み込みに時間がかかります。";
        ShowWorking("モデルを準備しています");
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            var result = await new TranslationService(client).TranslateAsync(English.Text, CurrentSettings(), piece =>
            {
                if (!receiving) { receiving = true; ShowWorking("翻訳しています"); }
                Japanese.AppendText(piece); Japanese.ScrollToEnd();
            }, cancel.Token);
            Japanese.Text = result;
            Status.Text = $"翻訳が完了しました({(DateTime.Now - workingSince).TotalSeconds:F0} 秒)。";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Status.Text = Japanese.Text.Length > 0 ? "翻訳を中止しました。途中までの訳を表示しています。" : "翻訳を中止しました。";
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
    private Task PasteAsync() => RunAsync(async () => {
        if (!Clipboard.ContainsImage()) throw new InvalidOperationException("クリップボードに画像がありません。Windows + Shift + Sで撮影してください。");
        await SetImageAsync(Clipboard.GetImage() ?? throw new InvalidOperationException("画像を取得できませんでした。撮影し直してください。"));
    });
    private async void PasteClick(object sender, RoutedEventArgs e) => await PasteAsync();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && translation is not null) { e.Handled = true; CancelTranslation(); }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
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
        try { if (!string.IsNullOrEmpty(Japanese.Text)) { Clipboard.SetText(Japanese.Text); Status.Text = "日本語訳をコピーしました。"; } }
        catch { Status.Text = "クリップボードを使用できません。再試行してください。"; }
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { SettingsStore.Save(CurrentSettings()); Status.Text = "設定を保存しました。"; }
        catch { Status.Text = "設定を保存できませんでした。"; }
    }
}
