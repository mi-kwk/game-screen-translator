using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
namespace GameScreenTranslator;
public static class OcrService
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
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }
}
public sealed class TranslationService(HttpClient client)
{
    public async Task<string> TranslateAsync(string text, string key, bool pro)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("翻訳する英語を入力してください。");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("DeepL APIキーを設定してください。文字認識だけならキーなしで利用できます。");
        using var request = new HttpRequestMessage(HttpMethod.Post, pro ? "https://api.deepl.com/v2/translate" : "https://api-free.deepl.com/v2/translate");
        request.Headers.Add("Authorization", "DeepL-Auth-Key " + key.Trim());
        request.Content = JsonContent.Create(new { text = new[] { text }, source_lang = "EN", target_lang = "JA" });
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch {
                HttpStatusCode.Forbidden => "APIキーまたはFree / Proの設定を確認してください。",
                HttpStatusCode.TooManyRequests => "翻訳リクエストが集中しています。少し待って再試行してください。",
                (HttpStatusCode)456 => "DeepL APIの利用上限に達しました。",
                _ => $"翻訳に失敗しました（HTTP {(int)response.StatusCode}）。"
            });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }
}
public record AppSettings(string Key, bool Pro);
public static class SettingsStore
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameScreenTranslator", "settings.bin");
    public static AppSettings Load()
    {
        if (!File.Exists(PathName)) return new("", false);
        return JsonSerializer.Deserialize<AppSettings>(ProtectedData.Unprotect(File.ReadAllBytes(PathName), null, DataProtectionScope.CurrentUser)) ?? new("", false);
    }
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllBytes(PathName, ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)), null, DataProtectionScope.CurrentUser));
    }
}