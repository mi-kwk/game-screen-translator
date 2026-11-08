# game-screen-translator

Windows + Shift + Sで撮ったゲーム画面の英語を日本語に翻訳するWindowsアプリ。
英文の読み取り(Windows OCR)も翻訳(Ollama のローカル AI)もPCの中で行い、インターネットには送信しません。

## 準備(初回のみ)
1. [Ollama](https://ollama.com) をインストールします(`winget install --id Ollama.Ollama -e` でも可)。
2. 翻訳に使うモデルをダウンロードします。GPU のメモリに合わせて選びます。
   ```powershell
   ollama pull gemma3:12b   # GPU メモリ 12GB 前後(例: RTX 3060)
   ollama pull gemma3:27b   # GPU メモリ 24GB 前後(例: RTX 3090)
   ```
3. Windowsの英語OCR言語パックが必要です(英語の言語を追加しておく)。

Ollama のアカウント登録は不要です。名前に `cloud` が付くモデルはインターネット上で動くため使いません。

## 起動
.NET 10 SDKを用意し、リポジトリで以下を実行します。
```powershell
dotnet run --project src/GameScreenTranslator
```
## 使い方
1. Windows + Shift + Sで英文の範囲を撮影。本文だけを選ぶと、ボタン名などが混ざらず訳がきれいになります。
2. アプリを選び、Ctrl + Vまたは「画像を貼り付け」。
3. 英語と日本語訳を確認。英文を修正して「日本語に翻訳」で再翻訳できます。

PNG/JPEG/BMPの読み込み、ドラッグ＆ドロップにも対応。
最初の1回はモデルの読み込みに30秒ほどかかります。2回目以降は短い文で数秒です。

翻訳中は「日本語訳」の横に動き続けるバーと経過秒数が出て、訳はできたところから順に表示されます。
「中止 (Esc)」ボタンか Esc キーで翻訳を止められます。途中までの訳は残ります。

訳は見出し・箇条書き・太字に整えて表示します(Q&A の質問は青い太字)。
文字の大きさは「A−」「A＋」か Ctrl + − / Ctrl + ＋ で変えられます。
「訳をコピー」では、見出しや太字の記号を外した文章をコピーします。

Ollama が止まっているときは、アプリが自動で起動します(アプリの起動時と翻訳の前)。
アプリが起動した Ollama は、アプリを閉じると止まります。

## ゲームの切り替え
画面右上の「ゲーム」で、翻訳するゲームを選びます。
選んだゲームの単語辞書と、共通の単語辞書の両方を使って翻訳します(同じ語はゲームのほうを優先)。

## 翻訳の設定
画面右上の「設定…」で開き、「保存」を押します。

| 項目 | 内容 |
|---|---|
| モデル | 使う Ollama のモデル。初期値は `gemma3:12b` |
| アドレス | 初期値は `http://127.0.0.1:11434`(この PC の Ollama) |
| 自動で起動する | Ollama が止まっていたら起動する。初期値はオン |
| 共通の単語辞書 | どのゲームでも使う単語辞書。初期値にゲームでよく使う語(パッチノート、バフ、クールダウンなど)が入っています |
| ゲームごとの設定 | ゲーム名・説明(ジャンルなど)・そのゲームの単語辞書。「追加」「削除」で管理します |

単語辞書は 1 行に 1 語、`英語 = 訳語` の形で書きます。
例は [docs/glossary-common.txt](docs/glossary-common.txt)(共通)と [docs/glossary-activematter.txt](docs/glossary-activematter.txt)(Active Matter)にあります。

## 配布用ビルド
```powershell
dotnet publish src/GameScreenTranslator -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```
出力フォルダのGameScreenTranslator.exeを実行します。フォルダ全体を配布してください。
使う PC にも Ollama とモデルが必要です。

## データの扱い
OCRと翻訳はどちらもPC内で実行し、外部のサービスには送信しません。
設定は LocalAppData/GameScreenTranslator/settings.json に保存します(秘密の情報は含みません)。
画像・翻訳履歴は保存しません。

## 技術
C# / .NET 10 / WPF / Windows.Media.Ocr / Ollama(ローカル LLM)。
設計はdocs/design.md、動作確認手順はdocs/verification.mdを参照。

## 自動検証
`dotnet run --project tests/Smoke -c Release`で画面初期化、実OCR、OCRの後処理、設定の保存、Ollamaとのやり取りのモックテストを実行します。
Ollama が無くても実行できます。

## プロジェクト記録
[Notion 設計・開発記録](https://app.notion.com/p/3f4232d7afe481cb9c2cf7265ab5903f)
