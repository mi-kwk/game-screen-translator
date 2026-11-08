using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace GameScreenTranslator;
public partial class MainWindow : Window
{
    // モデルの初回読み込みと長文の翻訳に時間がかかるため、長めに待つ。
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private BitmapSource? image;
    private bool busy;
    public MainWindow()
    {
        InitializeComponent();
        var settings = AppSettings.Default;
        try { settings = SettingsStore.Load(); }
        catch { Status.Text = "保存済みの設定を読み込めませんでした。初期設定で起動しました。"; }
        ShowSettings(settings);
        Closed += (_, _) => client.Dispose();
    }
    private void ShowSettings(AppSettings settings)
    {
        Model.Text = settings.Model; OllamaUrl.Text = settings.OllamaUrl;
        GameName.Text = settings.GameName; Glossary.Text = settings.Glossary;
    }
    private AppSettings CurrentSettings() => new(OllamaUrl.Text.Trim(), Model.Text.Trim(), GameName.Text.Trim(), Glossary.Text.Trim());
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; Actions.IsEnabled = false;
        try { await action(); }
        catch (TaskCanceledException) { Status.Text = "翻訳に時間がかかりすぎたため中止しました。英文を短くするか、小さいモデルを試してください。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { busy = false; Actions.IsEnabled = true; }
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
        Japanese.Clear(); Status.Text = "日本語に翻訳しています…(最初の 1 回はモデルの読み込みに時間がかかります)";
        var started = DateTime.Now;
        Japanese.Text = await new TranslationService(client).TranslateAsync(English.Text, CurrentSettings());
        Status.Text = $"翻訳が完了しました({(DateTime.Now - started).TotalSeconds:F0} 秒)。";
    }
    private Task PasteAsync() => RunAsync(async () => {
        if (!Clipboard.ContainsImage()) throw new InvalidOperationException("クリップボードに画像がありません。Windows + Shift + Sで撮影してください。");
        await SetImageAsync(Clipboard.GetImage() ?? throw new InvalidOperationException("画像を取得できませんでした。撮影し直してください。"));
    });
    private async void PasteClick(object sender, RoutedEventArgs e) => await PasteAsync();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
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
