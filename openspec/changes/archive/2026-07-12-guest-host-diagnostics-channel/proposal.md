## Why

DC-016（`records/DC-016.yaml`）は、`claude-code` CLIをTier1（AppContainer）で`-p`実行すると原因不明のカーネルモードCPU張り付き（ビジーループ）が発生する未解決バグを記録している。review_triggerには「WPR+WPAでのスタックトレース採取」「`process.allow_child_processes`/`network.allow_hosts`を変えた際の症状変化確認」「コンテキストスイッチ数/秒・ページフォールト数/秒の計測」が次の調査ステップとして挙げられているが、これらはいずれもProcmon・Get-Counter・WPRを都度手動でRDP/コンソール越しに操作する必要があり、再現の度に手間がかかる。

Tier1（ホストと同一セッション）とTier2（Windows Sandbox、別VM）は到達可能性が異なるため、単一の仕組みでは両方をカバーできない。tier2-channel-a-focus-guarantee（DC-019）で、Tier2向けにゲスト内`--nested`常駐プロセスとホストが`SandboxControlChannel`（DC-010）経由でリクエスト/レスポンスをやり取りする軽量プロトコル（focus-request.json/focus-result.json）を確立済みであり、同じパターンをCPU時間・ページフォールト数の採取にも応用できる。

## What Changes

- 対象PIDのCPU時間（ユーザー/カーネル）・ページフォールト数を指定した経過時間サンプリングし、秒あたりレート・比率を返す共有ロジック`DiagCollector`（`Srm.Runtime.Diagnostics`）を追加する。
- `SandboxControlChannel`に`diag-request.json`/`diag-result.json`を追加する（focus-request/resultと同じ、固定パス2ファイルを都度上書きしrequestIdの不一致は無視するプロトコル）。
- Tier2: ゲスト内`--nested`（`RunCommand.RunNested`）の`BlockUntilStop`ループに`onDiagRequestDetected`コールバックを追加し、ホストからの診断要求を検出して`DiagCollector`をその場で呼び出し、結果を`diag-result.json`へ書く。
- Tier1: ホストと同一セッションのため、ゲスト↔ホストの往復は行わずホスト側から`DiagCollector`を直接呼び出す。
- ホスト側の入口として`DiagOperation`（`Srm.Runtime.Operations`）を追加し、`RunningAppRegistry`からTierを判定してTier1直接呼び出し/Tier2委譲を振り分ける。
- 新規CLIサブコマンド`srm diag <app> [--duration-ms]`を追加する（DC-016のような実機調査時にのみ使う想定のため、MCPツールとしては公開しない）。

## Capabilities

### New Capabilities
- `guest-host-diagnostics-channel`: Tier1/Tier2の両方で、実行中アプリのCPU時間内訳・ページフォールト率を`srm diag`から短時間サンプリングできるようにする診断機構

### Modified Capabilities
（既存のspec化されたcapabilityは無し。DC-016はInvestigationRecordとして`records/DC-016.yaml`に記録されており、`openspec/specs/`には対応するspecファイルが存在しないため、変更対象なし）

## Impact

- `src/Srm.Runtime/Diagnostics/DiagCollector.cs`（新規: CPU時間・ページフォールト数のサンプリング本体）
- `src/Srm.Runtime/Sandbox/DiagModel.cs`（新規: diag-request/resultのモデル）
- `src/Srm.Runtime/Sandbox/SandboxControlChannel.cs`（diag-request.json/diag-result.jsonの読み書き・`BlockUntilStop`への`onDiagRequestDetected`追加）
- `src/Srm.Cli/Commands/RunCommand.cs`のゲスト内`--nested`メインループ（`HandleDiagRequest`追加）
- `src/Srm.Runtime/Operations/DiagOperation.cs`（新規: Tier1/Tier2振り分けの入口）
- `src/Srm.Cli/Commands/DiagCommand.cs`・`src/Srm.Cli/Program.cs`（新規CLIサブコマンド`srm diag`）
- `records/DC-016.yaml`のreview_trigger（本changeで追加した調査ツールへの参照を追記する場合）
