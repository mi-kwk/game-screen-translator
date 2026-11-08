using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameScreenTranslator;

internal static class Program
{
    private static readonly AppSettings Settings = new("http://127.0.0.1:11434", "test-model", "Active Matter", "raid = レイド");

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new App(); app.InitializeComponent();
            var window = new MainWindow();
            Console.WriteLine("PASS: WPF window initializes");
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 150));
                drawing.DrawText(new FormattedText("Hello world. Open the door.", CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, new Typeface("Arial"), 40, Brushes.Black, 1), new Point(24, 40));
            }
            var image = new RenderTargetBitmap(900, 150, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual); image.Freeze();
            var recognized = OcrService.ReadAsync(image).GetAwaiter().GetResult();
            if (!recognized.Contains("Hello", StringComparison.OrdinalIgnoreCase) || !recognized.Contains("door", StringComparison.OrdinalIgnoreCase))
                throw new Exception("OCR mismatch: " + recognized);
            Console.WriteLine("PASS: local English OCR: " + recognized);
            VerifyOcrCleanup();
            VerifySettings();
            VerifyTranslationAsync().GetAwaiter().GetResult();
            window.Close();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void VerifyOcrCleanup()
    {
        var cases = new Dictionary<string, string>
        {
            ["Update 0.4.O.192"] = "Update 0.4.0.192",
            ["1O0 AM"] = "100 AM",
            ["Open the door. OK"] = "Open the door. OK",
            ["Ozernoe"] = "Ozernoe",
        };
        foreach (var (input, expected) in cases)
            if (OcrService.Cleanup(input) != expected) throw new Exception($"OCR cleanup: {input} -> {OcrService.Cleanup(input)}");
        Console.WriteLine("PASS: OCR cleanup of O/0 confusion");
    }

    private static void VerifySettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameScreenTranslatorSmoke", Guid.NewGuid() + ".json");
        try
        {
            if (SettingsStore.Load(path) != AppSettings.Default) throw new Exception("Default settings mismatch");
            SettingsStore.Save(Settings, path);
            if (SettingsStore.Load(path) != Settings) throw new Exception("Settings round trip failed");
            Console.WriteLine("PASS: settings save and load");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static async Task VerifyTranslationAsync()
    {
        using var http = new HttpClient(new Stub());
        var result = await new TranslationService(http).TranslateAsync("Hello", Settings);
        if (result != "こんにちは") throw new Exception("Translation parsing failed: " + result);
        Console.WriteLine("PASS: Ollama request and response (mock)");

        using var missing = new HttpClient(new Stub(HttpStatusCode.NotFound));
        await ExpectError(() => new TranslationService(missing).TranslateAsync("Hello", Settings), "ollama pull test-model");
        using var offline = new HttpClient(new Offline());
        await ExpectError(() => new TranslationService(offline).TranslateAsync("Hello", Settings), "Ollama に接続できません");
        Console.WriteLine("PASS: missing model / Ollama not running errors (mock)");
    }

    private static async Task ExpectError(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(expected)) { return; }
        throw new Exception("Expected error containing: " + expected);
    }

    private sealed class Stub(HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var system = body["messages"]![0]!["content"]!.GetValue<string>();
            var user = body["messages"]![1]!["content"]!.GetValue<string>();
            if (request.RequestUri!.ToString() != "http://127.0.0.1:11434/api/chat" || body["model"]!.GetValue<string>() != "test-model"
                || body["stream"]!.GetValue<bool>() || !system.Contains("Active Matter") || !system.Contains("raid = レイド") || !user.Contains("Hello"))
                throw new Exception("Unexpected translation request: " + body.ToJsonString());
            var content = code == HttpStatusCode.OK
                ? "{\"message\":{\"role\":\"assistant\",\"content\":\"こんにちは\\n\"}}"
                : "{\"error\":\"model 'test-model' not found\"}";
            return new(code) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
