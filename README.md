# game-screen-translator

Windows + Shift + Sで撮ったゲーム画面の英語を日本語に翻訳するWindowsアプリ。

## 起動
.NET 10 SDKを用意し、リポジトリで以下を実行します。
```powershell
dotnet run --project src/GameScreenTranslator
```
## 使い方
1. Windows + Shift + Sで英文の範囲を撮影。
2. アプリを選び、Ctrl + Vまたは「画像を貼り付け」。
3. 初回は「DeepL APIの設定」にAPIキーを入力し、API Free / Proを選んで保存。
4. 英語と日本語訳を確認。英文を修正して「日本語に翻訳」で再翻訳できます。

PNG/JPEG/BMPの読み込み、ドラッグ＆ドロップにも対応。APIキーがなくてもOCRは使用可能。
Windowsの英語OCR言語パックが必要です。装飾文字や細かい文字では誤認識があります。

## 配布用ビルド
```powershell
dotnet publish src/GameScreenTranslator -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```
出力フォルダのGameScreenTranslator.exeを実行します。フォルダ全体を配布してください。

## データの扱い
OCRはPC内で実行。翻訳時は英文をDeepLに送信します。API利用料金・上限は契約プランに従います。
APIキーはWindows DPAPIで現在のユーザー向けに暗号化し、LocalAppData/GameScreenTranslator/settings.binに保存。
画像・翻訳履歴は保存しません。APIキーをGitHubやNotionに記載しないでください。

## 技術
C# / .NET 10 / WPF / Windows.Media.Ocr / DeepL REST API。
設計はdocs/design.md、動作確認手順はdocs/verification.mdを参照。

## 自動検証
`dotnet run --project tests/Smoke -c Release`で画面初期化、実OCR、DPAPI、翻訳APIのモックテストを実行します。

## プロジェクト記録
[Notion 設計・開発記録](https://app.notion.com/p/3f4232d7afe481cb9c2cf7265ab5903f)
