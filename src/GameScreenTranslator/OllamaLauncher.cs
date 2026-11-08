using System.Diagnostics;
using System.IO;
using System.Net.Http;
namespace GameScreenTranslator;

/// <summary>
/// この PC の Ollama が止まっているときに、裏で「ollama serve」を起動する。
/// このアプリが起動した Ollama だけを、アプリの終了時に止める。
/// </summary>
public static class OllamaLauncher
{
    private static Process? started;

    /// <summary>アドレスがこの PC(localhost / 127.0.0.1)を指しているか。ほかの PC の Ollama は起動できない。</summary>
    public static bool IsLocal(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;

    public static string? FindExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), "ollama.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<bool> IsRunningAsync(HttpClient client, string url, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await client.GetAsync(url.Trim().TrimEnd('/') + "/api/version", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Ollama が動いていなければ起動して、使えるようになるまで待つ。
    /// 戻り値: true = 起動した / false = もともと動いていた。起動できなければ例外。
    /// </summary>
    public static async Task<bool> EnsureRunningAsync(HttpClient client, string url, CancellationToken cancellationToken = default)
    {
        if (await IsRunningAsync(client, url, cancellationToken)) return false;
        if (!IsLocal(url)) throw new InvalidOperationException("Ollama に接続できません。設定のアドレスを確認してください(この PC 以外の Ollama は自動で起動できません)。");
        var exe = FindExecutable()
            ?? throw new InvalidOperationException("Ollama が見つかりません。Ollama をインストールしてください(winget install --id Ollama.Ollama -e)。");
        if (started is null || started.HasExited)
        {
            var info = new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            var uri = new Uri(url.Trim());
            if (uri.Port != 11434) info.Environment["OLLAMA_HOST"] = $"127.0.0.1:{uri.Port}";
            started = Process.Start(info) ?? throw new InvalidOperationException("Ollama を起動できませんでした。");
        }
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, cancellationToken);
            if (started.HasExited) throw new InvalidOperationException("Ollama を起動できませんでした。Ollama のアイコンから起動してください。");
            if (await IsRunningAsync(client, url, cancellationToken)) return true;
        }
        throw new InvalidOperationException("Ollama の起動に時間がかかっています。少し待ってからもう一度翻訳してください。");
    }

    /// <summary>このアプリが起動した Ollama を止める(もともと動いていた Ollama には触らない)。</summary>
    public static void StopStarted()
    {
        try { if (started is { HasExited: false }) started.Kill(entireProcessTree: true); }
        catch { /* 終了時なので、止められなくても何もしない */ }
        finally { started?.Dispose(); started = null; }
    }
}
