## Why

DC-017の実機検証（2026-07-12、`TESTING.md`「MCPサーバー実機テスト」9.3節）で、Tier2の
チャネルA（対話的ライブ制御、`send_key`/`send_mouse`/`screenshot`がホスト側MCPサーバーから
`WindowsSandboxClient.exe`ウィンドウへ直接SendInputする経路）に構造的な限界があることが
判明した。ホスト側`WindowGuard`のフォアグラウンド検証は`WindowsSandboxClient.exe`
ウィンドウ（ホスト側の1枚のRDP描画面）についてのみ行われ、その内部でゲストのどの
ウィンドウが実際に入力フォーカスを持っているかをホスト側は検証・保証する手段を持たない。
guardが入力発行を許可しても、ゲスト内で意図したアプリケーションにキー入力が届かないことが
実機で確認されている。

現状の暫定対応（コミット6eea85e）は、`send_key`のツール説明文に「Tier2では送信前後で
screenshotによる目視確認を徹底すること」という運用上の注意書きを追加しただけで、
コードによる恒久対策は行っていない（`records/DC-017.yaml`のreview_triggerに
「恒久対策は引き続き検討課題」として記録済み）。AIエージェントが目視確認を徹底する
運用に頼る設計は、確認漏れ・誤操作のリスクを利用者に転嫁するものであり、恒久対策とは
言えない。

## What Changes

- チャネルA（Tier2）の`send_key`/`send_mouse`が、送信前に対象ウィンドウへ確実に
  フォーカスを設定できる仕組みを追加する。
- 既存のゲスト内`--nested`プロセス（チャネルBがすでに使っている、DC-010の
  ファイルベースhost↔guest同期`SandboxControlChannel`を利用する常駐相当プロセス）を
  活用し、チャネルAの入力発行の直前に、ゲスト内で実際にフォーカスを確認・設定する
  ステップを挟む設計を検討・実装する（DC-017 decision 1の非常駐方針・decision 3の
  fail-closedガードとの整合性を保つ）。
- チャネルAの低遅延な対話性（「操作→結果を見る→次を決める」の応答性）とのトレードオフを
  設計段階で比較評価し、レイテンシへの影響を許容範囲に収める方式を選定する。
- 恒久対策の設計・実装後、`records/DC-017.yaml`のreview_triggerを解消し、必要であれば
  新しいDecisionRecordとして記録する。

## Capabilities

### New Capabilities
- `tier2-channel-a-focus-guarantee`: Tier2チャネルAの入力操作（`send_key`/`send_mouse`）が
  ゲスト内対象アプリの入力フォーカスを確実に持たせてから送信するための保証機構

### Modified Capabilities
（既存のspec化された capability は無し。DC-017はDecisionRecordとして`records/DC-017.yaml`に
記録されており、`openspec/specs/`には対応するspecファイルが存在しないため、変更対象なし）

## Impact

- `src/Srm.Mcp/Tools/InteractiveInputTools.cs`（チャネルAのTier2経路: `send_key`/`send_mouse`）
- `src/Srm.Runtime/Sandbox/SandboxControlChannel.cs`（host↔guest同期の既存ファイルベース機構。
  新しい軽量コマンドを追加する場合はここを拡張する）
- `src/Srm.Cli/Commands/RunCommand.cs`のゲスト内`--nested`メインループ（新しいコマンド種別を
  監視・処理する場合はここを拡張する）
- `records/DC-017.yaml`のreview_trigger（本changeで解消する項目）
- `manual/usage.md`のチャネルA説明（挙動が変わる場合は更新する）
