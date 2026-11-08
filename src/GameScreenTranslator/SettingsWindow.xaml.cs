using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
namespace GameScreenTranslator;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<GameItem> games;
    private readonly AppSettings original;
    /// <summary>メイン画面で選んでいたゲーム(名前を変えても追えるように、名前ではなく項目で持つ)。</summary>
    private readonly GameItem? current;
    private bool loading;

    /// <summary>保存したときの設定。キャンセルしたときは null。</summary>
    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        original = settings;
        Model.Text = settings.Model; OllamaUrl.Text = settings.OllamaUrl; AutoStart.IsChecked = settings.AutoStartOllama;
        CommonGlossary.Text = settings.CommonGlossary;
        games = new(settings.Games.Select(g => new GameItem { Name = g.Name, Description = g.Description, Glossary = g.Glossary }));
        Games.ItemsSource = games;
        current = games.FirstOrDefault(g => g.Name == settings.SelectedGame);
        Games.SelectedItem = current ?? games.FirstOrDefault();
        ShowSelected();
    }

    private GameItem? Selected => Games.SelectedItem as GameItem;

    private void ShowSelected()
    {
        loading = true;
        var game = Selected;
        GameEditor.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        NoGame.Visibility = game is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveGame.IsEnabled = game is not null;
        GameName.Text = game?.Name ?? ""; GameDescription.Text = game?.Description ?? ""; GameGlossary.Text = game?.Glossary ?? "";
        loading = false;
    }

    private void GamesSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    private void GameFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || Selected is not { } game) return;
        game.Name = GameName.Text; game.Description = GameDescription.Text; game.Glossary = GameGlossary.Text;
    }

    private void AddGameClick(object sender, RoutedEventArgs e)
    {
        var name = "新しいゲーム";
        for (var i = 2; games.Any(g => g.Name == name); i++) name = $"新しいゲーム {i}";
        var game = new GameItem { Name = name };
        games.Add(game);
        Games.SelectedItem = game;
        GameName.Focus(); GameName.SelectAll();
    }

    private void RemoveGameClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } game) return;
        if (MessageBox.Show(this, $"「{game.Name}」を削除しますか?\n単語辞書も一緒に削除されます。", "ゲームの削除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var index = games.IndexOf(game);
        games.Remove(game);
        Games.SelectedItem = games.Count == 0 ? null : games[Math.Min(index, games.Count - 1)];
        ShowSelected();
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var names = games.Select(g => g.Name.Trim()).ToList();
        if (names.Any(n => n.Length == 0)) { Message.Text = "ゲーム名が空のゲームがあります。"; return; }
        if (names.Distinct().Count() != names.Count) { Message.Text = "同じ名前のゲームがあります。名前を変えてください。"; return; }
        if (string.IsNullOrWhiteSpace(Model.Text)) { Message.Text = "モデルを入力してください(例: gemma3:12b)。"; return; }
        if (!Uri.TryCreate(OllamaUrl.Text.Trim(), UriKind.Absolute, out _)) { Message.Text = "Ollama のアドレスが正しくありません(例: http://127.0.0.1:11434)。"; return; }
        var list = games.Select(g => new GameProfile(g.Name.Trim(), g.Description.Trim(), g.Glossary.Trim())).ToList();
        // 選んでいたゲームが残っていればそのまま(名前を変えていても追う)。無ければ、ゲームが 1 つだけのときはそれを選ぶ。
        var selected = current is not null && games.Contains(current) ? current.Name.Trim() : list.Count == 1 ? list[0].Name : "";
        Result = original with
        {
            Model = Model.Text.Trim(), OllamaUrl = OllamaUrl.Text.Trim(), AutoStartOllama = AutoStart.IsChecked == true,
            CommonGlossary = CommonGlossary.Text.Trim(), Games = list, SelectedGame = selected,
        };
        DialogResult = true;
    }

    private sealed class GameItem : INotifyPropertyChanged
    {
        private string name = "";
        public string Name { get => name; set { name = value; Changed(); } }
        public string Description { get; set; } = "";
        public string Glossary { get; set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    }
}
