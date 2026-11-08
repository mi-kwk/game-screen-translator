using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
namespace GameScreenTranslator;

/// <summary>
/// 翻訳履歴の画面。上の欄に言葉を入れると、意味が近い履歴と、言葉がそのまま含まれる履歴を探す。
/// 履歴は PC の中だけに保存している。
/// </summary>
public sealed class HistoryWindow : Window
{
    private const string AllGames = "(すべてのゲーム)";
    private readonly HistoryStore store;
    private readonly Func<AppSettings> settings;
    private readonly Func<string, Task<float[]>> embedQuery;
    private readonly Func<Task> embedPending;
    private readonly TextBox query = new() { FontSize = 15, Padding = new Thickness(8, 6, 8, 6), ToolTip = "例: 敵が強くなる話 / 脱出できる場所 / Icarus" };
    private readonly ComboBox games = new() { Width = 200, Margin = new Thickness(10, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox saveHistory = new() { Content = "翻訳を履歴に保存する", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock count = new() { Foreground = Brush("#526075"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly ListBox results = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly FlowDocumentScrollViewer japanese = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false };
    private readonly TextBox english = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13, Padding = new Thickness(8) };
    private readonly TextBlock detailInfo = new() { Foreground = Brush("#526075"), Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock bestPartText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#1E293B") };
    private readonly Border bestPart = new() { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock note = new() { Foreground = Brush("#385777"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button copy = new() { Content = "訳をコピー", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button delete = new() { Content = "この履歴を削除", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private int searchVersion;

    public HistoryWindow(HistoryStore store, Func<AppSettings> settings, Func<string, Task<float[]>> embedQuery, Func<Task> embedPending, Action<bool> setSaveHistory)
    {
        this.store = store; this.settings = settings; this.embedQuery = embedQuery; this.embedPending = embedPending;
        Title = "翻訳履歴"; Width = 1100; Height = 720; MinWidth = 760; MinHeight = 480; Background = Brush("#F4F6FA");

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(games); right.Children.Add(saveHistory); right.Children.Add(count);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(query);

        var detail = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(copy); buttons.Children.Add(delete);
        bestPart.Child = bestPartText;
        DockPanel.SetDock(detailInfo, Dock.Top); DockPanel.SetDock(bestPart, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        var englishBox = new GroupBox { Header = "英文", Content = english, Height = 170, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(englishBox, Dock.Bottom);
        detail.Children.Add(detailInfo); detail.Children.Add(bestPart); detail.Children.Add(buttons); detail.Children.Add(englishBox);
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        detail.Children.Add(new Border { Background = Brushes.White, BorderBrush = Brush("#ABADB3"), BorderThickness = new Thickness(1), Child = japanese });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) });
        body.Children.Add(results);
        Grid.SetColumn(detail, 2); body.Children.Add(detail);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var clear = new Button { Content = "履歴を全部削除…", Padding = new Thickness(12, 5, 12, 5) };
        DockPanel.SetDock(clear, Dock.Right);
        bottom.Children.Add(clear); bottom.Children.Add(note);

        var layout = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top); layout.Children.Add(bottom); layout.Children.Add(body);
        Content = layout;

        saveHistory.IsChecked = settings().SaveHistory;
        saveHistory.Click += (_, _) => { setSaveHistory(saveHistory.IsChecked == true); ShowNote(saveHistory.IsChecked == true ? "これからの翻訳を履歴に保存します。" : "これからの翻訳は履歴に保存しません(今ある履歴は残ります)。"); };
        query.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { debounce.Stop(); RefreshResults(); } };
        debounce.Tick += (_, _) => { debounce.Stop(); RefreshResults(); };
        games.SelectionChanged += (_, _) => RefreshResults();
        results.SelectionChanged += (_, _) => ShowSelected();
        copy.Click += (_, _) => { if (Selected is { } h) { try { Clipboard.SetText(MarkdownView.ToPlainText(h.Entry.Japanese)); ShowNote("訳をコピーしました。"); } catch { ShowNote("クリップボードを使用できません。"); } } };
        delete.Click += (_, _) => DeleteSelected();
        clear.Click += (_, _) => ClearAll();
        Loaded += (_, _) => { RefreshGames(); RefreshResults(); query.Focus(); ShowNote("言葉を入れると、意味が近い翻訳と、言葉がそのまま含まれる翻訳を探します。履歴は PC の中だけに保存しています。"); };
    }

    private HistoryHit? Selected => (results.SelectedItem as ListBoxItem)?.Tag as HistoryHit;

    public void ShowNote(string text) => Dispatcher.Invoke(() => note.Text = text);

    private void RefreshGames()
    {
        var current = games.SelectedItem as string;
        games.Items.Clear();
        games.Items.Add(AllGames);
        foreach (var g in store.Games()) games.Items.Add(g);
        games.SelectedItem = current is not null && games.Items.Contains(current) ? current : AllGames;
    }

    /// <summary>今の検索の言葉で、結果を出し直す。</summary>
    public async void RefreshResults()
    {
        var version = ++searchVersion;
        var text = query.Text.Trim();
        var game = games.SelectedItem as string is { } g && g != AllGames ? g : null;
        float[]? vector = null;
        if (text.Length > 0)
        {
            try { await embedPending(); vector = await embedQuery(text); }
            catch (InvalidOperationException ex) { ShowNote(ex.Message); }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException) { ShowNote("意味で探せないため、言葉そのままで探しています。"); }
        }
        if (version != searchVersion) return; // 新しい検索が始まっていたら捨てる
        var hits = store.Search(text, vector, game);
        var previous = Selected?.Entry.Id;
        results.Items.Clear();
        foreach (var hit in hits) results.Items.Add(new ListBoxItem { Tag = hit, Content = ItemView(hit, text.Length > 0), Padding = new Thickness(8, 6, 8, 6) });
        count.Text = $"全 {store.Count()} 件" + (text.Length > 0 ? $" / 見つかった {hits.Count} 件" : "");
        if (games.Items.Count - 1 != store.Games().Count) RefreshGames();
        results.SelectedItem = results.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as HistoryHit)?.Entry.Id == previous) ?? (results.Items.Count > 0 ? results.Items[0] : null);
        if (results.Items.Count == 0) ShowSelected();
    }

    private static FrameworkElement ItemView(HistoryHit hit, bool searching)
    {
        var e = hit.Entry;
        var head = new DockPanel();
        if (searching)
        {
            var badge = new Border
            {
                Background = Brush(hit.KeywordMatch ? "#FEF3C7" : "#DBEAFE"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0),
                Child = new TextBlock { FontSize = 11, Text = hit.KeywordMatch ? "言葉が一致" : $"意味が近い {Math.Min(hit.Score, 1) * 100:F0}%" },
            };
            DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge);
        }
        head.Children.Add(new TextBlock { FontSize = 11, Foreground = Brush("#64748B"), Text = $"{e.Created:yyyy/MM/dd HH:mm}  {(e.Game.Length > 0 ? e.Game : "ゲームなし")}  ・{e.Source}" });
        var firstLines = string.Join(" ", MarkdownView.ToPlainText(e.Japanese).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(2));
        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock { Text = firstLines, TextWrapping = TextWrapping.Wrap, MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        // 検索したときは、いちばん近かった部分も見せる(どこが見つかったのか分かるように)
        if (searching && hit.BestPart.Length > 0)
            panel.Children.Add(new Border
            {
                BorderBrush = Brush(hit.KeywordMatch ? "#F59E0B" : "#3B82F6"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 2, 0, 2), Margin = new Thickness(0, 4, 0, 0),
                Child = new TextBlock { Text = hit.BestPart.Replace('\n', ' '), TextWrapping = TextWrapping.Wrap, MaxHeight = 62, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("#334155"), FontSize = 12 },
            });
        return panel;
    }

    private void ShowSelected()
    {
        var hit = Selected;
        copy.IsEnabled = delete.IsEnabled = hit is not null;
        japanese.Document = MarkdownView.ToDocument(hit?.Entry.Japanese ?? "", Math.Max(13, settings().FontSize - 1));
        english.Text = hit?.Entry.English ?? "";
        detailInfo.Text = hit is null ? "" : $"{hit.Entry.Created:yyyy/MM/dd HH:mm}  {(hit.Entry.Game.Length > 0 ? hit.Entry.Game : "ゲームなし")}  ・{hit.Entry.Source}";
        // 検索で見つかった部分を、訳の上に目立たせて出す
        var found = hit is not null && hit.BestPart.Length > 0 && query.Text.Trim().Length > 0;
        bestPart.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        if (found)
        {
            bestPart.BorderBrush = Brush(hit!.KeywordMatch ? "#F59E0B" : "#3B82F6");
            bestPart.Background = Brush(hit.KeywordMatch ? "#FFFBEB" : "#EFF6FF");
            bestPartText.Text = (hit.KeywordMatch ? "言葉が一致した部分:\n" : $"意味が近い部分({Math.Min(hit.Score, 1) * 100:F0}%):\n") + hit.BestPart;
        }
    }

    private void DeleteSelected()
    {
        if (Selected is not { } hit) return;
        if (MessageBox.Show(this, "この履歴を削除しますか?\n削除すると元に戻せません。", "履歴の削除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        store.Delete(hit.Entry.Id);
        ShowNote("履歴を 1 件削除しました。");
        RefreshResults();
    }

    private void ClearAll()
    {
        var total = store.Count();
        if (total == 0) return;
        if (MessageBox.Show(this, $"翻訳履歴 {total} 件をすべて削除しますか?\n削除すると元に戻せません。", "履歴を全部削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        store.Clear();
        ShowNote($"翻訳履歴 {total} 件をすべて削除しました。");
        RefreshGames(); RefreshResults();
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
