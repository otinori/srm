## Why

SRMはAIネイティブ開発のハーネスエンジニアリングの一部として使うことを想定している。
常時起動し続けるサービスではなく、AIエージェント（Claude Code等）が人間の都度確認を
挟まずに設計書などの成果物を安全かつ高速に一気通貫で作成したり、環境を壊さずにテストを
安全に自動化したりする用途で使う。

現状SRMは`srm.exe`によるCLIとしてのみ操作可能であり、AIエージェントはシェル経由で
CLIを呼ぶしかない。標準出力のパースやプロセス起動のオーバーヘッドが介在するうえ、
GUIアプリの起動状態確認・キー/マウス操作といった構造化されていない操作をAIに安全に
渡す手段がない。SRMの機能をMCPサーバーとして公開すれば、AIエージェントが構造化された
ツール呼び出しでサンドボックス実行を直接オーケストレーションできるようになり、「AIが
安全にビルド・テスト・GUI動作確認を自律的に回す」というハーネス本来の目的に直結する。

## What Changes

- **MCPサーバー新設（`Srm.Mcp`）**: MCPクライアントがstdio経由で都度起動する薄い
  プロセスとして実装する。常駐デーモンにはしない（DC-008/DC-017）
- **CLI相当のMCPツール化**: `run`/`stop`/`list`/`logs`/`validate`/`evidence`相当を、
  既存`Srm.Runtime`/`Srm.PolicyEngine`をライブラリ参照する形でツール化する
- **チャネルA（対話的ライブ制御）**: `send_key`/`send_mouse`/`screenshot`を、Tier1は
  対象プロセス自身のウィンドウへ、Tier2は`WindowsSandbox.exe`がホスト上に開く
  ゲストデスクトップのRDPレンダリングウィンドウへ、それぞれ`SendInput`で直接実行する。
  Tier2でもゲスト内の変更は不要
- **チャネルB（オートパイロット、Tier2専用）**: 既存`SandboxControlChannel`
  （`control/`配下のシグナルファイル方式、DC-010）を拡張し、テストシナリオ
  （手順の並び）を`scenario.json`として1回投入し、ゲスト内`--nested`が自律的に
  最後まで実行して`scenario-result.json`を返す方式を追加する。証跡は既存の
  evidence検疫パイプラインに乗せる
- **チャネルC（将来オプション）**: Hyper-V Socketsをファイル転送・リアルタイム
  モニタリング用に温存する構想を記録するが、本changeでは実装しない（PoC待ち）
- **WindowGuard**: チャネルA・チャネルBのゲスト内実行が共通で使う、送信直前の
  対象ウィンドウ実体再検証ロジック（fail-closed）。誤って対象外（サンドボックス外の
  ウィンドウ等）に入力を送らないためのガード
- **操作対象の限定**: MCPツールが操作できるプロセス/ウィンドウは、SRMが起動・管理
  しているポリシー配下のものに限定する（汎用リモートデスクトップ操作はスコープ外）

## Capabilities

### New Capabilities

- `Srm.Mcp`: stdio MCPサーバー。CLI相当ツール＋チャネルA/Bのツールを公開する
- `Srm.Mcp.WindowGuard`: ウィンドウ実体の再検証ロジック（Tier1・チャネルA・
  チャネルBのゲスト内実行で共通利用）
- `Srm.Runtime.Sandbox`（拡張）: `SandboxControlChannel`のシナリオ投入/結果機構

### Modified Capabilities

- `Srm.Runtime.Sandbox.SandboxControlChannel`: signal方式に加えて
  `scenario.json`/`scenario-result.json`の読み書きを追加
- `Srm.Cli.RunCommand`（`--nested`）: `BlockUntilStop`のみだったメインループに
  シナリオ実行ディスパッチを追加

## Impact

- 既存Tier1/Tier2の起動・停止・ポリシースキーマへの破壊的変更なし。MCPサーバーは
  既存`Srm.Runtime`/`Srm.PolicyEngine`を参照する新規プロジェクトとして追加する
- `--nested`のメインループ変更はTier2の既存動作（`stop.signal`待機）に対して
  後方互換を保つ必要がある（`scenario.json`が無い場合は従来通り待機するのみ）
- チャネルBの証跡を既存evidence検疫パイプラインに統合することで、新規の保持期限
  ロジックを追加せずに済む。一方`%ProgramData%\SRM\run\<app>\<runId>\`自体の保持期限が
  そもそも未実装という既存の穴は本changeのスコープ外として残る（DC-017 context参照）
- 入力注入・画面キャプチャ・ガード拒否という新しい操作カテゴリの監査ログ設計は
  本changeのスコープに含める（DC-017 implications参照）
- Tier1/Tier2のウィンドウ解決、`WindowGuard`の実機動作、チャネルBのゲスト内自律実行は、
  これまでのchangeと同様Windows実機でしか検証できない（本changeではLinux開発環境での
  コンパイル・ユニットテストまでをスコープとし、実機検証は別途まとめて実施する）
