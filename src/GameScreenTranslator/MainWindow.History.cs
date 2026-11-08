using System.Windows;
namespace GameScreenTranslator;

/// <summary>メイン画面のうち、翻訳履歴(保存・意味の数値づくり・履歴の画面)に関わる部分。</summary>
public partial class MainWindow
{
    private HistoryStore? history;
    private bool embedding;
    private HistoryWindow? historyWindow;

    private HistoryStore? History
    {
        get
        {
            if (history is not null) return history;
            try { history = new HistoryStore(); }
            catch (Exception ex) { Status.Text = "翻訳履歴のデータベースを開けませんでした: " + ex.Message; }
            return history;
        }
    }

    /// <summary>翻訳が終わったら履歴に保存し、裏で意味の数値を作る。</summary>
    private void SaveToHistory(string english, string japanese, string source)
    {
        if (!settings.SaveHistory || string.IsNullOrWhiteSpace(english) || string.IsNullOrWhiteSpace(japanese) || History is not { } store) return;
        try { store.Add(settings.CurrentGame?.Name ?? "", source, english, japanese); }
        catch (Exception ex) { Status.Text = "翻訳履歴に保存できませんでした: " + ex.Message; return; }
        historyWindow?.RefreshResults();
        _ = EmbedPendingAsync();
    }

    /// <summary>意味の数値がまだ無い履歴に、順番に意味の数値を作る(同時には 1 つだけ動かす)。</summary>
    private async Task EmbedPendingAsync()
    {
        if (embedding || History is not { } store) return;
        embedding = true;
        try
        {
            var service = new EmbeddingService(client);
            for (var round = 0; round < 100; round++)
            {
                var pending = store.PendingEmbeddings(settings.EmbedModel, 20);
                if (pending.Count == 0) break;
                foreach (var entry in pending)
                {
                    var parts = HistoryStore.Chunk(entry.Japanese);
                    if (parts.Count == 0) parts = [entry.English];
                    store.SetEmbeddings(entry.Id, parts, await service.EmbedManyAsync(settings, parts), settings.EmbedModel);
                }
            }
        }
        catch (ObjectDisposedException) { /* 終了中 */ }
        catch (InvalidOperationException ex) { historyWindow?.ShowNote(ex.Message); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException) { /* Ollama が止まっている。次の機会に作る */ }
        finally { embedding = false; }
    }

    private void HistoryClick(object sender, RoutedEventArgs e)
    {
        if (History is not { } store) return;
        if (historyWindow is { IsLoaded: true }) { historyWindow.Activate(); return; }
        historyWindow = new HistoryWindow(store, () => settings, query => new EmbeddingService(client).EmbedAsync(settings, query), EmbedPendingAsync,
            enabled => { settings = settings with { SaveHistory = enabled }; SaveSettings(); });
        historyWindow.Show();
        _ = EmbedPendingAsync();
    }
}
