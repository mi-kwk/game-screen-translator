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
        var pieces = new List<string>();
        var result = await new TranslationService(http).TranslateAsync("Hello", Settings, pieces.Add);
        if (result != "こんにちは") throw new Exception("Translation parsing failed: " + result);
        if (!pieces.SequenceEqual(new[] { "こん", "にちは\n" })) throw new Exception("Streaming pieces mismatch: " + string.Join("|", pieces));
        Console.WriteLine("PASS: Ollama streaming request and response (mock)");

        using var broken = new HttpClient(new Stub(streamError: true));
        await ExpectError(() => new TranslationService(broken).TranslateAsync("Hello", Settings), "out of memory");
        Console.WriteLine("PASS: error in the middle of the stream (mock)");

        using var slow = new HttpClient(new Slow());
        using var cancel = new CancellationTokenSource();
        var received = new List<string>();
        var task = new TranslationService(slow).TranslateAsync("Hello", Settings, piece => { received.Add(piece); cancel.Cancel(); }, cancel.Token);
        try { await task; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        if (!received.SequenceEqual(new[] { "途中" })) throw new Exception("Cancel: unexpected pieces " + string.Join("|", received));
        Console.WriteLine("PASS: cancel while translating keeps the received part (mock)");

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

    private sealed class Stub(HttpStatusCode code = HttpStatusCode.OK, bool streamError = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var system = body["messages"]![0]!["content"]!.GetValue<string>();
            var user = body["messages"]![1]!["content"]!.GetValue<string>();
            if (request.RequestUri!.ToString() != "http://127.0.0.1:11434/api/chat" || body["model"]!.GetValue<string>() != "test-model"
                || !body["stream"]!.GetValue<bool>() || !system.Contains("Active Matter") || !system.Contains("raid = レイド") || !user.Contains("Hello"))
                throw new Exception("Unexpected translation request: " + body.ToJsonString());
            var content = code != HttpStatusCode.OK
                ? "{\"error\":\"model 'test-model' not found\"}"
                : streamError
                    ? "{\"message\":{\"content\":\"こん\"},\"done\":false}\n{\"error\":\"out of memory\"}\n"
                    : "{\"message\":{\"content\":\"こん\"},\"done\":false}\n{\"message\":{\"content\":\"にちは\\n\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true}\n";
            return new(code) { Content = new StringContent(content, Encoding.UTF8, "application/x-ndjson") };
        }
    }

    /// <summary>1 行目を返したあと、止められるまで次の行を返さない Ollama の代わり。</summary>
    private sealed class Slow : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream()) });

        private sealed class SlowStream : Stream
        {
            private readonly byte[] first = Encoding.UTF8.GetBytes("{\"message\":{\"content\":\"途中\"},\"done\":false}\n");
            private bool sent;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!sent) { sent = true; first.CopyTo(buffer); return first.Length; }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
