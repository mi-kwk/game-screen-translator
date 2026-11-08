using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
namespace GameScreenTranslator;

public enum WatchState { Off, Watching, Reading, Translating }

/// <summary>
/// 画面の見張り: 決めた範囲を一定間隔で撮り、変化があって落ち着いたら読み取り、新しい英文なら翻訳する。
/// - 画面が変わっていなければ読み取りも翻訳もしない(負荷を抑える)。
/// - 文字が出ている途中(動きのある画面)は 1 回待ってから読む。動き続ける画面でも 2 回目には読む。
/// - 前と同じ英文(9 割以上同じ単語)なら翻訳しない。
/// - 翻訳中に新しい英文が出たら、古い翻訳を止めて新しいほうを訳す。
/// </summary>
public sealed partial class ScreenWatcher(
    Func<Task<BitmapSource>> captureAsync,
    Func<BitmapSource, Task<OcrArrangement>> readAsync,
    Func<string, Action<string>, CancellationToken, Task<string>> translateAsync)
{
    public const double ChangeThreshold = 0.004;
    public const double SameTextThreshold = 0.9;

    private CancellationTokenSource? watching;
    private CancellationTokenSource? translating;
    private string lastText = "";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);
    public bool IsRunning => watching is not null;

    /// <summary>状態が変わったとき(状態, 補足の文)。</summary>
    public event Action<WatchState, string>? StateChanged;
    /// <summary>新しい英文の翻訳を始めたとき(英文)。</summary>
    public event Action<string>? TranslationStarted;
    /// <summary>訳が少し届いたとき。</summary>
    public event Action<string>? Chunk;
    /// <summary>翻訳が終わったとき(訳全体)。</summary>
    public event Action<string>? TranslationCompleted;

    public void Start()
    {
        if (watching is not null) return;
        watching = new CancellationTokenSource();
        lastText = "";
        _ = RunAsync(watching.Token);
    }

    public void Stop()
    {
        translating?.Cancel(); translating = null;
        watching?.Cancel(); watching = null;
        StateChanged?.Invoke(WatchState.Off, "");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[]? previous = null;
        var pending = true;
        var changingStreak = 0;
        StateChanged?.Invoke(WatchState.Watching, "");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var frame = await captureAsync();
                if (cancellationToken.IsCancellationRequested) break;
                var pixels = Pixels(frame);
                if (previous is not null && previous.Length == pixels.Length)
                {
                    var changed = FrameDifference(previous, pixels, frame.PixelWidth, frame.PixelHeight) > ChangeThreshold;
                    if (changed) { pending = true; changingStreak++; }
                    else changingStreak = 0;
                    // 変化した直後は、文字が出きるのを 1 回待つ(動き続ける画面なら 2 回目に読む)
                    if (changed && changingStreak < 2) { previous = pixels; await Task.Delay(Interval, cancellationToken); continue; }
                }
                previous = pixels;
                if (pending)
                {
                    pending = false; changingStreak = 0;
                    await ProcessAsync(frame, cancellationToken);
                }
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                StateChanged?.Invoke(WatchState.Watching, ex.Message);
                try { await Task.Delay(Interval, cancellationToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessAsync(BitmapSource frame, CancellationToken cancellationToken)
    {
        if (translating is null) StateChanged?.Invoke(WatchState.Reading, "");
        var read = await readAsync(frame);
        if (cancellationToken.IsCancellationRequested) return;
        var text = read.Text.Trim();
        if (text.Length == 0)
        {
            if (translating is null) StateChanged?.Invoke(WatchState.Watching, "英語の文字が見つかりません");
            return;
        }
        if (TextSimilarity(text, lastText) >= SameTextThreshold)
        {
            if (translating is null) StateChanged?.Invoke(WatchState.Watching, "");
            return;
        }
        lastText = text;
        translating?.Cancel();
        _ = TranslateAsync(text, cancellationToken);
    }

    private async Task TranslateAsync(string text, CancellationToken watchToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(watchToken);
        translating = cancel;
        TranslationStarted?.Invoke(text);
        StateChanged?.Invoke(WatchState.Translating, "");
        try
        {
            var result = await translateAsync(text, piece => { if (!cancel.IsCancellationRequested) Chunk?.Invoke(piece); }, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            TranslationCompleted?.Invoke(result);
            StateChanged?.Invoke(WatchState.Watching, "");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lastText = ""; // 失敗したら、次に同じ英文を見たときにもう一度訳す
            if (!watchToken.IsCancellationRequested) StateChanged?.Invoke(WatchState.Watching, ex.Message);
        }
        finally { if (translating == cancel) translating = null; }
    }

    private static byte[] Pixels(BitmapSource frame)
    {
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>2 枚の画像(BGRA)の違いの割合。3 ピクセルおきに明るさを比べ、大きく変わった点の割合を返す。</summary>
    public static double FrameDifference(byte[] a, byte[] b, int width, int height)
    {
        if (a.Length != b.Length) return 1;
        long sampled = 0, changed = 0;
        for (var y = 0; y < height; y += 3)
        for (var x = 0; x < width; x += 3)
        {
            var i = (y * width + x) * 4;
            var ga = (a[i] + 2 * a[i + 1] + a[i + 2]) / 4;
            var gb = (b[i] + 2 * b[i + 1] + b[i + 2]) / 4;
            sampled++;
            if (Math.Abs(ga - gb) > 30) changed++;
        }
        return sampled == 0 ? 0 : (double)changed / sampled;
    }

    /// <summary>2 つの英文の似ている度合い(単語の重なり、0〜1)。OCR の小さな読み違いでは訳し直さないために使う。</summary>
    public static double TextSimilarity(string a, string b)
    {
        var wa = Words(a); var wb = Words(b);
        if (wa.Count == 0 && wb.Count == 0) return 1;
        if (wa.Count == 0 || wb.Count == 0) return 0;
        var counts = wa.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var common = 0;
        foreach (var w in wb) if (counts.TryGetValue(w, out var c) && c > 0) { common++; counts[w] = c - 1; }
        return (double)common / Math.Max(wa.Count, wb.Count);
    }

    private static List<string> Words(string text) => WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();

    [GeneratedRegex(@"[A-Za-z0-9']+")]
    private static partial Regex WordPattern();
}
