# Changelog

このプロジェクトの変更点をまとめます。フォーマットは
[Keep a Changelog](https://keepachangelog.com/) に緩く準拠します。

## 0.1.0.0 - 2026-07-19

初期リリース。Tier1（AppContainer）・Tier2（Windows Sandbox）によるポリシー
ベースのサンドボックス実行、WFPネットワークフィルタ、Job Object管理、MCP
サーバー（`Srm.Mcp.exe`、チャネルA/B）、evidence検疫パイプライン、Srm.PolicyEditor
（WPF）を含む。

### Added

- AppContainer隔離（Tier 1）— ポリシーで指定したパスのみにファイルシステムアクセスを制限
- WFPネットワークフィルタ — アウトバウンド通信を`allow_hosts`に列挙したホストのみ許可
- Job Objectによるプロセスグループ管理 — 起動したプロセスと子プロセスを一括終了
- Windows Sandbox隔離（Tier 2）— AppContainerを重ねた多層防御、ツールチェーンのオフライン展開（`provision`）、実行結果の検疫〜人間による明示的な昇格（`srm evidence`）
- 専用アカウント＋Low Integrity Level隔離（`tier2.app_container: false`）— AppContainer非互換アプリ向けのTier2代替経路
- Tier2向けオンデマンドファイル転送（`srm transfer put/get`）・プロセス監視（`srm diag --watch`）
- MCPサーバー（`Srm.Mcp.exe`）— `srm run/stop/list/logs/validate/evidence`相当の構造化ツール呼び出し、キー・マウス入力送信、スクリーンショット取得、Tier2向けテストシナリオ自動実行
- サンドボックス内エージェントからホスト側MCPサーバーを利用できるチャネルD（`mcp.allow_servers`による許可リスト強制＋監査ログ記録）
- ポリシー整合性検証（SHA256サイドカー方式、`srm validate --sign`）
- Srm.PolicyEditor（WPF）— YAMLを手書きせずポリシーファイルをネイティブフォームで作成・編集
- CLIコマンド一式（`run` / `stop` / `list` / `logs` / `validate` / `evidence` / `diag` / `transfer` / `cleanup-account`）

### Fixed

### Changed

### Known Limitations

詳細は`README.md`の「既知の制限事項」および`views/records/`配下のDecisionRecord/
InvestigationRecordを参照。主なもの:

- AppContainer非互換のCLIツール（Bunコンパイルバイナリ等）はTier1では動作しない
  場合がある。回避策は`tier2.app_container: false`（Tier2限定）
- `tier2.app_container: false`は書き込みバリアのみを保証し、読み取りは制限しない
- Tier2のネットワーク遮断はVM境界ではなくゲスト内部のWFPエンジンによるもの
- `allow_tools`はMCPツール名のみを検証し、引数の内容までは検証しない
- AppContainerプロセスに対するハンドル数/スレッド数/メモリ情報の取得は、OS内部の
  制約により最小値しか得られない（CPU時間は正しく取得できる）
