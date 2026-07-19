## Why

Tier2（Windows Sandbox）はDC-002で方針決定されたのみで実行パスが存在しない。
`RunCommand.cs` は `policy.Tier` を見ておらず、常にTier1（AppContainer）で起動する。
`tier: 2` を指定しても `WindowsEditionDetector.ThrowIfTier2OnHome` によるHomeエディション
ガード以外は何も起きず、実質的にTier1として動いてしまう（サイレントな権限低下）。

DC-010〜DC-013で、多層防御構成・ネットワーク制御・provisioning方針・evidence検疫
パイプラインの設計判断が固まったため、これを実装に落とし込む。

## What Changes

- **ポリシースキーマ拡張**: `provision`（toolchain/steps/network）・`evidence`
  （source_path/retention_days）セクションを追加（`Srm.PolicyEngine`）
- **Sandbox実行基盤**: `.wsb`設定生成・ホスト側事前ACL付与・host↔guest制御チャネル
  （ready/stop/stopped signal）・VM起動オーケストレーション（`Srm.Runtime`）
- **Evidence検疫パイプライン**: マニフェスト生成・サニタイズ・検疫ストア・
  昇格/却下操作（`Srm.Runtime`）
- **CLIコマンド**: `srm run` のTier2分岐、`--nested`内部モード、
  `srm evidence list/show/promote/reject`（`Srm.Cli`）

## Capabilities

### New Capabilities

- `Srm.Runtime.Sandbox`: Windows Sandbox (.wsb) 設定生成・起動オーケストレーション・
  host↔guest制御チャネル
- `Srm.Runtime.Evidence`: outbox検疫・マニフェスト・昇格/却下

### Modified Capabilities

- `Srm.PolicyEngine`: `PolicyModel`/`PolicyValidator` に `provision`/`evidence` を追加
- `Srm.Cli`: `RunCommand` をTier1/Tier2で分岐、`evidence` サブコマンドを追加

## Impact

- 既存Tier1の挙動・スキーマ・CLIコマンドへの破壊的変更なし（`provision`/`evidence`は
  ともに省略可能なオプションセクション）
- 新規実装のうち、実際のゲストOS内での起動・Hyper-V Firewallでのフィルタリング・
  Windows Defenderスキャン呼び出しはWindows実機でしか検証できない
  （このリポジトリの開発環境はLinuxのため、コンパイル・ユニットテストまでを本changeの
  スコープとし、実機検証は別途まとめて実施する。tasks.mdの「実機確認要」セクション参照）
