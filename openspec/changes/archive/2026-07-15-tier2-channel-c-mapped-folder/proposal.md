## Why

DC-017（2026-07-11）は、Tier2向けのMCP外部制御を目的別に3チャネルへ分割した。チャネルA（対話的ライブ制御）・チャネルB（オートパイロット）は`SandboxControlChannel`（Windows SandboxのMappedFolder＋JSONファイルの250msポーリング、DC-010）で実装済みだが、チャネルC（ファイル転送・サンドボックス内プロセスのリアルタイムモニタリング専用）はHyper-V Sockets（AF_HYPERV）を前提とし、「ゲスト内からカスタムサービスGUIDを開けるか」の実機PoCが未実施のまま保留になっている（DC-017 review_trigger）。

その後の実装（DC-013のoutbox検疫モデル、DC-020/021のdiag-request/diag-resultポーリング）は、チャネルCが本来担うはずだった「ファイル転送」「リアルタイムモニタリング」という2つの目的を、HvSocketではなくチャネルA/Bと同じMappedFolder＋request/result JSONポーリングのidiomで、個別・アドホックに部分実現していた。この事実は、チャネルCをHvSocketなしで正式に設計・一般化できることを示している。本changeは、この延長線上でチャネルCを設計し、DC-017が残したHvSocket PoC待ちの前提を置き換える。

## What Changes

- チャネルCの実現方式を「Hyper-V Sockets（PoC未実施）」から「`SandboxControlChannel`のrequest/result JSONポーリングidiomの一般化」へ変更する（設計方針の転換。DC-017 review_triggerの前提を置き換える新しいDecisionRecordを伴う）
- 既存のrequest/result idiom（focus-request/focus-result、diag-request/diag-result）に共通する構造（requestIdベースの都度上書き・都度検出、蓄積しないファイルペア、タイムアウト付きポーリング）を、`SandboxControlChannel`内の再利用可能な汎用プリミティブとして整理する（新規コード追加ではなく、既存3パターンの重複除去を伴うリファクタリング設計）
- 新規capability `tier2-file-transfer`: 実行中のTier2ゲストに対し、任意の1ファイルをオンデマンドでホスト⇄ゲスト双方向に転送できるようにする。既存のoutbox（DC-010/013）は「VM停止後にまとめて検疫→人間が明示的に昇格」というバッチ・片方向（ゲスト→ホスト）モデルのままとし、本capabilityはそれとは別に「実行中に、任意のタイミングで、双方向に」という用途を担う
- 新規capability `tier2-process-monitor`: `srm diag`のCPU診断（DC-020/021）に閉じていたdiag-request/diag-resultパターンを一般化し、プロセス一覧・リソース使用状況等を継続的にポーリングできるようにする
- **BREAKING**ではない: 既存のチャネルA/B・outbox・`srm diag`の外部インターフェース（CLI・MCPツール定義）は変更しない。内部実装の共通化のみ

## Capabilities

### New Capabilities
- `tier2-mapped-folder-channel`: `SandboxControlChannel`のrequest/result JSONポーリングidiomを一般化した、チャネルCの基盤プリミティブ。DC-017が温存していたHvSocketベースの設計を置き換える
- `tier2-file-transfer`: `tier2-mapped-folder-channel`上に構築する、実行中Tier2ゲストとのオンデマンド双方向ファイル転送
- `tier2-process-monitor`: `tier2-mapped-folder-channel`上に構築する、継続的なプロセス/リソースモニタリング（`srm diag`のCPU診断を包含する一般化）

### Modified Capabilities
（なし。既存の`guest-host-diagnostics-channel`・`tier2-channel-a-focus-guarantee`はrequirement変更を伴わない。内部実装が新しい共通プリミティブへ委譲される可能性はあるが、外部から見た振る舞い・CLIインターフェースは変わらない）

## Impact

- `src/Srm.Runtime/Sandbox/SandboxControlChannel.cs`: request/result JSONポーリングの共通プリミティブを追加（既存のscenario/focus/diagの3パターンはこの上に再構成される設計候補。本changeでは設計のみで実装しない）
- `src/Srm.Runtime/Sandbox/SandboxLauncher.cs`・`SandboxRunPaths`: ファイル転送用のマップフォルダ構成の要否を検討
- DC-017: review_triggerの「チャネルC（HvSocket）」前提を置き換える新しいDecisionRecordを追加する
- `Srm.Mcp`（MCPツール定義）: `tier2-file-transfer`・`tier2-process-monitor`を将来MCPツールとして公開する場合の設計余地を残すが、本changeではCLIレベルの設計にとどめ、MCPツール化は別changeのスコープとする
