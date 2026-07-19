# Srm.PolicyEditor

`policies/*.yaml` を手書きせずにネイティブのフォームで作成・編集するためのローカル専用ツール。
`srm.exe` には組み込まれていない独立したWPFアプリで、`dotnet run` で単独起動する（DC-014）。

## 起動方法

### ソースから (`dotnet run`)

```powershell
dotnet run --project src/Srm.PolicyEditor
```

### ビルド済みバイナリから

[Releases](../../../releases) から `srm-<version>.zip` をダウンロードして展開すると、
`bin\` フォルダに `srm.exe` と `Srm.PolicyEditor.exe` が両方同梱されている（別途ダウンロード
不要）。単一exeではなく `dotnet publish` の通常出力をそのままzip化したもの。展開後のフォルダ
構成を変えないこと。

```powershell
srm-0.1.0.0\
  bin\
    srm.exe
    Srm.PolicyEditor.exe
    （+ 依存DLL一式）
  policies\
  setup.ps1

cd bin
.\Srm.PolicyEditor.exe
```

`policies/` ディレクトリは実行ファイルからの相対解決（`Srm.Cli`と同じ順序）のため、
`bin\` から実行すれば1階層上の `policies\`（パッケージ直下）がそのまま解決される。

---

起動するとネイティブウィンドウ（`MainWindow`）が直接表示される。旧バージョンのような
HTTPサーバー・ブラウザ自動起動は無い（DC-014でBlazor Serverから純粋なWPFへ書き直した）。

## 何をするか

- `Srm.Cli` と同じ順序で `policies/` ディレクトリを解決し、既存ポリシーの一覧・編集・新規作成を行う。
  一覧画面の「対象ディレクトリ」欄から任意のフォルダに切り替えることもできる
  （`dotnet run` 時の作業ディレクトリの都合で解決先が想定と違う場合の回避策にもなる）。
- 新規作成時は「保存先フォルダ」を自由に指定できる（既定は起動時に解決されたディレクトリ）。
  存在しないフォルダを指定した場合は保存時に自動作成される。
- 対象アプリのインストールフォルダを指定すると、フォルダ構造とEXE/DLL内の文字列を軽量スキャンして
  実行ファイル候補・`filesystem.allow_paths`・`network.allow_hosts` の候補を提案する
  （PEインポート解析は行わない。あくまで下書きであり、必ず内容を確認すること）。
- フォルダ・ファイルパスを入力する欄（対象ディレクトリ・保存先フォルダ・対象フォルダから生成・
  `application.executable`・`application.working_directory`・`filesystem.allow_paths` の各行）には
  すべて「参照...」ボタンがあり、.NET 8 WPF標準の`Microsoft.Win32.OpenFolderDialog`/
  `OpenFileDialog`から選べる（テキスト欄への手入力も引き続き可能。`%VAR%` のような環境変数展開
  記法を直接書きたい場合は手入力を使うこと）。`application.executable` の参照ボタンのみファイル
  選択ダイアログ（フィルタ: 実行ファイル `*.exe`）で、それ以外はすべてフォルダ選択ダイアログになる。
- 保存時は `Srm.PolicyEngine.PolicyValidator` でバリデーションし、成功した場合のみYAMLを書き込み、
  `Srm.PolicyIntegrity.IntegrityWriter` で `.sha256` サイドカーを再生成する
  （`srm validate <policy> --sign` と同等の効果）。バリデーションに失敗した場合は既存ファイルを
  上書きしない。

## 制限事項

- ローカル開発者向けの内部ツールであり、認証機構は持たない（HTTPサーバー自体が無いため、
  旧バージョンにあった「ループバック限定バインド」という前提も不要になった）。
- フォルダ解析の候補（実行ファイル・パス・ホスト名）はヒューリスティックによる推測であり、正確性を
  保証しない。必ず手動で確認・修正してから保存すること。
- このリポジトリのLinux開発環境では`Microsoft.NET.Sdk.WindowsDesktop`ワークロードが無いため
  `Srm.PolicyEditor`単体は`dotnet build`できない（WPF/WinFormsに共通の制約。DC-014）。
  ビルド確認・動作確認はWindows実機で行うこと。

## アーキテクチャ

```
Srm.PolicyEditor/
├── App.xaml(.cs)              # エントリポイント。MainWindowを生成して表示するのみ
├── MainWindow.xaml(.cs)        # ルートウィンドウ。PolicyListView/PolicyEditViewを差し替える
├── Views/                     # XAML + コードビハインド（DataContext設定のみ）
├── ViewModels/                # 画面ロジック（MVVM）。Srm.PolicyEngineのモデルを直接編集する
├── Mvvm/                      # ObservableObject/RelayCommandの自前実装
│                                 （CommunityToolkit.Mvvm等の新規依存は追加しない、DC-014）
└── Services/                  # UIフレームワーク非依存のロジック層（変更なし）
    ├── PolicyDirectoryResolver.cs
    ├── PolicyEditorService.cs   # 読み込み・バリデーション・保存＋サイドカー再署名
    ├── PolicyScaffoldService.cs # フォルダからの下書き生成
    └── NativeDialogService.cs   # ネイティブダイアログ（WPF標準API、DC-014でWinFormsから移行）
```

DIコンテナは使わず、`MainWindow`が各サービスをフィールドとして直接保持する（規模に見合う
最小構成）。
