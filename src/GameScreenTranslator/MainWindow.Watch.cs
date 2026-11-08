using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
namespace GameScreenTranslator;

/// <summary>メイン画面のうち、画面の見張り(自動で訳す小窓)に関わる部分。</summary>
public partial class MainWindow
{
    private const int WatchHotKeyId = 0x4754; // 任意の番号
    private const uint VK_T = 0x54;
    private ScreenWatcher? watcher;
    private WatchOverlay? overlay;
    private bool hotKeyRegistered;

    private void InitializeWatch()
    {
        watcher = new ScreenWatcher(CaptureWatchRegionAsync, OcrService.ReadLayoutAsync, (text, onChunk, token) =>
            new TranslationService(client).TranslateAsync(text, settings with { Model = settings.WatchModel }, onChunk, token));
        watcher.StateChanged += (state, note) => { overlay?.SetState(state, note); UpdateWatchInfo(state, note); };
        watcher.TranslationStarted += _ => overlay?.ShowTranslation("");
        watcher.Chunk += piece => overlay?.AppendTranslation(piece);
        watcher.TranslationCompleted += result => overlay?.ShowTranslation(result);
        ApplyWatchSettings();
        UpdateWatchInfo(WatchState.Off, "");
        SourceInitialized += (_, _) =>
        {
            hotKeyRegistered = ScreenCapture.RegisterHotKey(this, WatchHotKeyId, ScreenCapture.MOD_CONTROL | ScreenCapture.MOD_SHIFT, VK_T);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            if (!hotKeyRegistered) Status.Text = "Ctrl + Shift + T がほかのアプリで使われているため、見張りの切り替えは画面のスイッチで行ってください。";
            UpdateWatchInfo(WatchState.Off, "");
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ScreenCapture.WM_HOTKEY && wParam.ToInt32() == WatchHotKeyId) { handled = true; _ = ToggleWatchAsync(); }
        return IntPtr.Zero;
    }

    private void ApplyWatchSettings()
    {
        if (watcher is not null) watcher.Interval = TimeSpan.FromSeconds(settings.WatchIntervalSeconds);
        overlay?.SetOpacity(settings.OverlayOpacity);
        overlay?.SetFontSize(settings.FontSize);
        UpdateWatchInfo(watcher?.IsRunning == true ? WatchState.Watching : WatchState.Off, "");
    }

    private void UpdateWatchInfo(WatchState state, string note)
    {
        var region = settings.WatchRegion is { } r ? $"範囲: {r.Width}×{r.Height}" : "範囲: 未選択";
        var hotKey = hotKeyRegistered || watcher is null ? "Ctrl + Shift + T で ON / OFF" : "スイッチで ON / OFF";
        WatchInfo.Text = state == WatchState.Off ? $"{region} / {hotKey}" : $"{region} / {(note.Length > 0 ? note : state switch { WatchState.Reading => "読み取り中", WatchState.Translating => "翻訳中", _ => "見張り中" })}";
        WatchToggle.IsChecked = watcher?.IsRunning == true;
        WatchToggle.Content = watcher?.IsRunning == true ? "ON" : "OFF";
    }

    private async void WatchToggleClick(object sender, RoutedEventArgs e) => await ToggleWatchAsync();
    private async void SelectRegionClick(object sender, RoutedEventArgs e)
    {
        var wasRunning = watcher?.IsRunning == true;
        if (wasRunning) await StopWatchAsync(unload: false);
        if (SelectRegion() && wasRunning) await StartWatchAsync();
    }

    private async Task ToggleWatchAsync()
    {
        if (watcher?.IsRunning == true) await StopWatchAsync(unload: true);
        else await StartWatchAsync();
    }

    /// <summary>見張る範囲を選ぶ。メイン画面は一度しまって、後ろのゲーム画面で選べるようにする。</summary>
    private bool SelectRegion()
    {
        var previous = WindowState;
        WindowState = WindowState.Minimized;
        var selector = new RegionSelectWindow();
        var ok = selector.ShowDialog() == true && selector.Result is not null;
        WindowState = previous;
        if (!ok) { UpdateWatchInfo(WatchState.Off, ""); return false; }
        settings = settings with { WatchRegion = selector.Result };
        SaveSettings();
        UpdateWatchInfo(watcher?.IsRunning == true ? WatchState.Watching : WatchState.Off, "");
        Status.Text = $"見張る範囲を選びました({selector.Result!.Width}×{selector.Result.Height})。";
        return true;
    }

    private async Task StartWatchAsync()
    {
        if (watcher is null || watcher.IsRunning) return;
        if (settings.WatchRegion is null && !SelectRegion()) { UpdateWatchInfo(WatchState.Off, ""); return; }
        try
        {
            if (settings.AutoStartOllama && OllamaLauncher.IsLocal(settings.OllamaUrl))
                await OllamaLauncher.EnsureRunningAsync(client, settings.OllamaUrl);
        }
        catch (InvalidOperationException ex) { Status.Text = ex.Message; UpdateWatchInfo(WatchState.Off, ""); return; }
        overlay ??= new WatchOverlay(settings.OverlayBounds, settings.OverlayOpacity, settings.FontSize);
        overlay.ShowTranslation("");
        overlay.Show();
        watcher.Start();
        UpdateWatchInfo(WatchState.Watching, "");
        Status.Text = $"画面の見張りを始めました。{settings.WatchModel} で訳し、小窓に出します(最初の 1 回はモデルの読み込みに時間がかかります)。";
    }

    private async Task StopWatchAsync(bool unload)
    {
        if (watcher is null || !watcher.IsRunning) return;
        watcher.Stop();
        if (overlay is not null)
        {
            settings = settings with { OverlayBounds = overlay.Bounds };
            SaveSettings();
            overlay.Hide();
        }
        UpdateWatchInfo(WatchState.Off, "");
        if (!unload) return;
        Status.Text = "画面の見張りを止めました。GPU のメモリを空けています…";
        await new TranslationService(client).UnloadAsync(settings, settings.WatchModel);
        Status.Text = "画面の見張りを止めました。GPU のメモリを空けました。";
    }

    private void CloseWatch()
    {
        if (watcher?.IsRunning == true)
        {
            watcher.Stop();
            if (overlay is not null) { settings = settings with { OverlayBounds = overlay.Bounds }; SaveSettings(); }
        }
        overlay?.Close();
        if (hotKeyRegistered) ScreenCapture.UnregisterHotKey(this, WatchHotKeyId);
    }

    /// <summary>見張る範囲を撮る。小窓が範囲に重なっているときは、自分の訳を読まないように一瞬隠してから撮る。</summary>
    private async Task<BitmapSource> CaptureWatchRegionAsync()
    {
        var region = settings.WatchRegion ?? throw new InvalidOperationException("見張る範囲を選んでください。");
        if (overlay is { IsVisible: true } && ScreenCapture.WindowRect(overlay) is { } rect && rect.IntersectsWith(region))
        {
            overlay.Opacity = 0;
            await Task.Delay(80);
            try { return ScreenCapture.Capture(region); }
            finally { overlay.Opacity = 1; }
        }
        return ScreenCapture.Capture(region);
    }
}
