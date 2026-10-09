using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameScreenTranslator;

internal static class Program
{
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
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes("test-key"), null, DataProtectionScope.CurrentUser);
            if (Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser)) != "test-key") throw new Exception("DPAPI failed");
            Console.WriteLine("PASS: Windows DPAPI round trip");
            VerifyTranslationAsync().GetAwaiter().GetResult();
            window.Close();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task VerifyTranslationAsync()
    {
        using var http = new HttpClient(new Stub());
        var result = await new TranslationService(http).TranslateAsync("Hello", "fake-key", false);
        if (result != "こんにちは") throw new Exception("Translation parsing failed");
        Console.WriteLine("PASS: translation request and response (mock)");
        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, (HttpStatusCode)456 })
        {
            using var failedHttp = new HttpClient(new Stub(code));
            try { await new TranslationService(failedHttp).TranslateAsync("Hello", "fake-key", false); throw new Exception("Expected API failure"); }
            catch (InvalidOperationException) { }
        }
        Console.WriteLine("PASS: authentication/rate/quota errors (mock)");
    }
    private sealed class Stub(HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.Host != "api-free.deepl.com" || request.Headers.GetValues("Authorization").Single() != "DeepL-Auth-Key fake-key" || !body.Contains("JA") || !body.Contains("Hello"))
                throw new Exception("Unexpected translation request");
            return new(code) { Content = new StringContent("{\"translations\":[{\"text\":\"こんにちは\"}]}", Encoding.UTF8, "application/json") };
        }
    }
}
