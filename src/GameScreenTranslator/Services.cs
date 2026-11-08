using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
namespace GameScreenTranslator;
public static partial class OcrService
{
    public static async Task<string> ReadAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? throw new InvalidOperationException("英語のOCRが利用できません。Windowsの設定で英語の言語パックを追加してください。");
        var limit = (double)OcrEngine.MaxImageDimension;
        var scale = Math.Min(1, limit / Math.Max(image.PixelWidth, image.PixelHeight));
        if (scale < 1) image = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        { writer.WriteBytes(buffer.ToArray()); await writer.StoreAsync(); await writer.FlushAsync(); }
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return Cleanup(string.Join(Environment.NewLine, result.Lines.Select(line => line.Text)));
    }

    /// <summary>OCR がよく間違える、数字に挟まれた英字の O を 0 に直す(例: 0.4.O.192 → 0.4.0.192)。</summary>
    public static string Cleanup(string text) => DigitO().Replace(text, "0");

    [GeneratedRegex(@"(?<=\d\.?)O(?=\.?\d)")]
    private static partial Regex DigitO();
}

public sealed class TranslationService(HttpClient client)
{
    private const string BasePrompt = """
        あなたはゲームの英語テキストを日本語に翻訳する翻訳者です。
        - 自然で読みやすい日本語にする。ゲームのニュースやパッチノートの文体に合わせる。
        - 固有名詞(ゲーム名、地名、マップ名、組織名、アイテム名、人名)は原文の英語のまま残す。
        - 見出し・箇条書き・段落の構成は保つ。
        - 画面の UI の断片(ボタン名、日付、数字だけの行、ゲームのロゴ、意味のない文字列)は省く。
        - 原文の意味を足したり減らしたりしない。否定・肯定を取り違えない。
        - 翻訳文だけを出力し、説明や前置きは書かない。
        """;

    public static string BuildSystemPrompt(AppSettings settings)
    {
        var prompt = BasePrompt;
        if (!string.IsNullOrWhiteSpace(settings.GameName)) prompt += $"\n\n対象のゲーム:{settings.GameName.Trim()}";
        if (!string.IsNullOrWhiteSpace(settings.Glossary)) prompt += "\n\n次の用語集に必ず従って訳す(左が英語、右が訳語):\n" + settings.Glossary.Trim();
        return prompt;
    }

    public async Task<string> TranslateAsync(string text, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("翻訳する英語を入力してください。");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("翻訳に使うモデルを設定してください(例: gemma3:12b)。");
        if (!Uri.TryCreate(settings.OllamaUrl.Trim().TrimEnd('/') + "/api/chat", UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("Ollama のアドレスが正しくありません(例: http://127.0.0.1:11434)。");
        var model = settings.Model.Trim();
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt(settings) },
                new JsonObject { ["role"] = "user", ["content"] = "次の英文を日本語に翻訳してください。\n\n" + text.Trim() }),
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = 8192 },
        };
        HttpResponseMessage response;
        try { response = await client.PostAsJsonAsync(endpoint, body, cancellationToken); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("Ollama に接続できません。Ollama が起動しているか、設定のアドレスを確認してください。"); }
        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode == HttpStatusCode.NotFound
                    ? $"モデル「{model}」が見つかりません。コマンドプロンプトで「ollama pull {model}」を実行してください。"
                    : $"翻訳に失敗しました(HTTP {(int)response.StatusCode})。{ErrorMessage(content)}");
            var answer = JsonNode.Parse(content)?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("翻訳結果が空でした。もう一度翻訳してください。");
            return answer.Trim();
        }
    }

    private static string ErrorMessage(string content)
    {
        try { return JsonNode.Parse(content)?["error"]?.GetValue<string>() ?? ""; }
        catch (JsonException) { return ""; }
    }
}

public record AppSettings(string OllamaUrl, string Model, string GameName, string Glossary)
{
    public static AppSettings Default { get; } = new("http://127.0.0.1:11434", "gemma3:12b", "", "");
}

public static class SettingsStore
{
    private static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameScreenTranslator", "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return AppSettings.Default;
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? AppSettings.Default;
        return new(
            string.IsNullOrWhiteSpace(loaded.OllamaUrl) ? AppSettings.Default.OllamaUrl : loaded.OllamaUrl,
            string.IsNullOrWhiteSpace(loaded.Model) ? AppSettings.Default.Model : loaded.Model,
            loaded.GameName ?? "", loaded.Glossary ?? "");
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}
