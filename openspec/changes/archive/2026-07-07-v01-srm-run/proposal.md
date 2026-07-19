## Why

SRM v0.1 のゴールは「node.exe（Claude Code）をAppContainer内で隔離実行する」を実現すること。
承認済みスペック（spec-v01-srm-run / spec-policy-engine / spec-policy-integrity）に基づき、
C# (.NET 8) による最初の動作する実装を `dist/` に配置する。

## What Changes

- **C# ソリューション構築**: `dist/` 配下に `Srm.sln` と各プロジェクトを配置
- **Policy Engine 実装**: YAMLローダー・スキーマバリデーター・環境変数展開（`Srm.PolicyEngine`）
- **Policy Integrity 実装**: SHA256サイドカー生成・検証（`Srm.PolicyIntegrity`）
- **AppContainer 隔離実行**: SID生成・ACL設定・CreateProcess・Job Object（`Srm.Runtime`）
- **WFP ネットワークフィルタリング**: アウトバウンドホワイトリスト制御（`Srm.Runtime`）
- **CLIコマンド実装**: `srm run / list / stop / logs / validate`（`Srm.Cli`）
- **ロギング**: JSON Lines 構造化ログ・7日ローテーション
- **Windowsエディション検出**: Home でTier2指定時のエラー処理
- **スモークテスト**: `cmd.exe /c echo hello` のAppContainer内実行確認

## Capabilities

### New Capabilities

- `Srm.PolicyEngine`: YAMLポリシーの読み込み・検証・環境変数展開
- `Srm.PolicyIntegrity`: SHA256サイドカーによるポリシー整合性保証
- `Srm.Runtime`: AppContainer起動・Job Object・WFPネットワークフィルタリング
- `Srm.Cli`: `srm` コマンドラインインターフェース

### Modified Capabilities

なし（新規実装）

## Impact

`dist/` に初めて実行可能ファイルが配置される。既存のドキュメント・レジストリへの影響はない。
spec-srm-architecture の制約レコード欄が「DC-001〜DC-006参照」に更新される。
