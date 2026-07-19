## 参照レコード

- [DC-017](../../../records/DC-017.yaml) — MCPサーバーによる外部制御アーキテクチャ（本changeの中心決定）
- [DC-008](../../../records/DC-008.yaml) — IPC・デーモンなし方針（JSONファイルレジストリ方式）
- [DC-010](../../../records/DC-010.yaml) — Tier2実行アーキテクチャ・host↔guest制御チャネル
- [DC-013](../../../records/DC-013.yaml) — outbox検疫〜昇格パイプライン（チャネルBの証跡が乗る）
- [DC-002](../../../records/DC-002.yaml) — Tier1/Tier2二段構えの原方針

## プロジェクト構造（追加分）

```
src/
├── Srm.Mcp/
│   ├── Program.cs                     # stdio transportでMCPサーバーを起動
│   ├── Tools/
│   │   ├── RuntimeTools.cs            # run/stop/list/logs/validate相当のMCPツール
│   │   ├── EvidenceTools.cs           # evidence list/show/promote/reject相当
│   │   ├── InteractiveInputTools.cs   # send_key/send_mouse/screenshot（チャネルA）
│   │   └── AutopilotTools.cs          # run_scenario/get_scenario_result（チャネルB）
│   ├── WindowGuard.cs                 # ウィンドウ実体の再検証（Tier1/Tier2共通、DC-017 decision 4）
│   ├── Tier1WindowResolver.cs         # AppContainer化プロセスのPID→HWND解決
│   └── Tier2SandboxWindowResolver.cs  # WindowsSandbox.exeのレンダリングウィンドウ解決
├── Srm.Runtime/
│   └── Sandbox/
│       └── SandboxControlChannel.cs   # scenario.json / scenario-result.json の読み書きを追加
└── Srm.Cli/
    └── Commands/RunCommand.cs         # --nested のメインループにシナリオ実行ディスパッチを追加
```

`Srm.Mcp`は`Srm.Runtime`/`Srm.PolicyEngine`/`Srm.PolicyIntegrity`をプロジェクト参照
する形にし、既存CLIロジック（`RunningAppRegistry`・`AppContainerLauncher`・
`SandboxLauncher`等）をそのまま再利用する。`Srm.Cli`をサブプロセスとして起動して
標準出力をパースする方式は採らない（DC-017 decision 2）。

## MCPツール一覧（案）

| ツール名 | チャネル | 説明 |
|---|---|---|
| `srm_run` / `srm_stop` / `srm_list` / `srm_logs` / `srm_validate` | — | 既存CLI相当 |
| `srm_evidence_*` | — | Tier2検疫データの確認・昇格・却下 |
| `send_key` / `send_mouse` / `screenshot` | A（対話的ライブ制御） | 探索的なGUI操作。Tier1は直接、Tier2はウィンドウ経由 |
| `run_scenario` / `get_scenario_result` | B（オートパイロット、Tier2専用） | 手順の並びを1回投入し、完了まで自律実行させる |

すべてのツールは`srm_run`が返す実行ID（`app`名+`runId`）を必須引数に取り、SRMが
管理していない任意のプロセス・ウィンドウは対象にできないようインターフェース
レベルで制約する（DC-017 decision 5）。

## チャネルA: 対話的ライブ制御

**Tier1** — ホストとAppContainer化プロセスは同一Windowsセッション上で動作するため、
ブリッジ機構なしで完結する。

1. `RunningAppRegistry`から対象`app`のPID（Job Object配下の全PID）を取得する
2. `EnumWindows`+`GetWindowThreadProcessId`で対象PIDに属する可視ウィンドウを列挙する
3. `SendInput`（キー/マウス）または`PrintWindow`（スクリーンショット）を実行する

**Tier2** — 対象アプリのウィンドウには直接届かないが、`SandboxLauncher`が起動する
`WindowsSandbox.exe`はホスト上にゲストデスクトップをRDPベースでレンダリングする
ウィンドウを開く。このウィンドウへ`SendInput`すると、既存のRDP転送機構がそのまま
ゲストへ届ける。**ゲスト内`--nested`側の変更は不要。**

1. `runId`起動時に記録した`SandboxLauncher`のホストプロセスから、レンダリング
   ウィンドウのHWNDを解決する
2. `SendInput`（キー/マウス）を対象ウィンドウに送る。マウス座標はウィンドウの
   クライアント領域内での相対位置からホストスクリーン座標へ変換する
3. `screenshot`は同じウィンドウを`PrintWindow`/`BitBlt`でキャプチャする

### ガード（`WindowGuard`、Tier1/Tier2共通、DC-017 decision 4）

`SendInput`/`PrintWindow`の**直前に毎回**、以下をすべて満たすことを再検証し、
1つでも失敗したら**発行せず中断**する（fail-closed）。HWNDはOSに再利用されるため、
起動時に一度だけ解決した値をキャッシュして使い回すことはしない。

1. 対象ウィンドウの所有PIDが、この`runId`に紐づく起動時記録済みプロセスに属している
   （Tier1: Job Object配下PID集合。Tier2: `SandboxLauncher`が起動したプロセス）
2. 所有PIDの実行ファイルパスが期待するバイナリと一致する
   （Tier2は`%SystemRoot%\System32\WindowsSandbox*.exe`）
3. ウィンドウクラス名が期待値と一致する
4. （マウス操作のみ）変換後のホスト座標が、送信直前に取得した対象ウィンドウの
   クライアント領域内に収まっている
5. `SetForegroundWindow`後、実際に対象ウィンドウがフォアグラウンドになったことを
   確認できる

拒否した場合は`StructuredLogger`に`guard_rejected`イベント（理由付き）として記録する。

## チャネルB: オートパイロット（Tier2専用）

既存の`ready.signal`/`stop.signal`/`stopped.signal`と同じ「ファイルの存在・内容を
ポーリングする」という運用モデルを踏襲しつつ、単位を「コマンド1件」ではなく
「シナリオ1件」にする。

```
control/
 ├─ policy.yaml           host→guest（既存）
 ├─ ready.signal            guest→host（既存）
 ├─ stop.signal             host→guest（既存）
 ├─ stopped.signal          guest→host（既存）
 ├─ scenario.json           host→guest（新規。run_scenarioツール呼び出し時に書く）
 └─ scenario-result.json    guest→host（新規。シナリオ完了時に一度だけ書く）
```

```json
// scenario.json（実装済みのスキーマ。ScenarioModel/ScenarioStep参照）
{
  "steps": [
    { "type": "send_key", "text": "Hello, world!" },
    { "type": "screenshot", "label": "after-typing" },
    { "type": "send_mouse", "x": 100, "y": 20 }
  ]
}
```

- **実装時に判明した順序の制約**: `scenario.json`は`policy.yaml`と違い「VM起動前に配置」
  できない。`srm_run`（Tier2）はVM起動〜`ready.signal`受信まで同期的にブロックして
  戻るため、MCPクライアントが`run_scenario`を呼べるのは`srm_run`が返った**後**であり、
  その時点でゲスト内`--nested`は既に起動シーケンスを終えている。そのため
  `SandboxControlChannel.BlockUntilStop`に「`stop.signal`を待つ間ずっと
  `scenario.json`の出現を監視し、現れた時点で一度だけコールバックを呼ぶ」
  オーバーロードを追加し、起動直後の単発チェックではなく継続監視にした
  （`scenario.json`が最後まで現れない場合は従来の`BlockUntilStop()`と完全に等価）
- ゲスト内`--nested`と対象アプリは同一セッション上で動くため、各ステップの実行は
  チャネルAのTier1と全く同じ「同一セッション内`SendInput`＋`WindowGuard`」ロジックを
  ゲスト内でそのまま再利用する（`ScenarioExecutor`が`Tier1WindowResolver`/
  `WindowGuard`/`InputSender`/`WindowCapture`をそのまま呼ぶ）。新規のウィンドウ解決
  トリックは不要
- 各ステップのスクリーンショット等の証跡は`outbox/`に書き、新規の保持期限機構を
  作らず既存のevidence検疫パイプライン（DC-013、`EvidenceQuarantineStore`の
  `retention_days`）にそのまま乗せる
- `scenario-result.json`はステップごとの成否サマリのみを持つ小さな1ファイルで、
  詳細な証跡は`outbox`側に置く
- host↔guest間の常時ラウンドトリップに依存しないため、投入後は通信が不安定でも
  自律的に完走できる
- MCPツールは`run_scenario`（投入、非同期）と`get_scenario_result`（結果取得、
  `waitSeconds`指定でポーリング待機も可能）に分かれる

## チャネルC: 将来オプション（Tier2専用、本changeのスコープ外）

Hyper-V Sockets（AF_HYPERV）を、入力操作ではなく**ファイル転送・サンドボックス内
プロセスのリアルタイムモニタリング**専用のチャネルとして温存する。ゲスト内から
カスタムサービスGUIDを開けるか等、実現可否が未検証のためPoC次第とする
（DC-017 review_trigger参照）。実現しない場合もチャネルA/Bの設計には影響しない。
本changeでは実装しない。

## 実装上の注意

- `Srm.Mcp`は`SendInput`等Windows専用APIに依存するため`net8.0-windows`ターゲットに
  なる。Linux開発環境ではMCPツール定義・JSONスキーマ・`scenario.json`のシリアライズ/
  デシリアライズ部分のみユニットテスト可能で、実際の入力注入・ウィンドウ列挙・
  `WindowGuard`の実機動作はこれまでのTier1/Tier2実装と同様Windows実機でのみ検証できる
- README/`spec-srm-architecture`には「GUIアプリ対応はv0.2以降」という記述があるが、
  `notepad-test`ポリシーで実証済みの通りGUIアプリの**起動**自体は既にv0.1から可能。
  本changeが追加するのはその先の**操作**（チャネルA/B）であり、対象を混同しないよう
  仕様書側の記述更新を実装タスクに含める
- `--nested`のシナリオ実行ディスパッチは、`scenario.json`が存在しない場合（チャネルB
  未使用のTier2ポリシー）は従来通り`stop.signal`のみを待つ挙動と完全に等価になるよう
  実装し、既存Tier2の後方互換を壊さない
- `WindowGuard`はTier1・チャネルA(Tier2)・チャネルBのゲスト内実行の3箇所すべてから
  呼ばれる共通コンポーネントとして実装し、ガードロジックの重複・食い違いを防ぐ
- 実装中に判明した詳細: 既存`JobObjectManager.Create`が作る Job Object は無名
  （`CreateJobObject(IntPtr.Zero, null)`）で、`srm run`プロセスがハンドルを閉じた
  後は別プロセスから再アクセスする手段がなかった。`WindowGuard`のTier1 PID所属確認は
  別プロセス（`Srm.Mcp`）から行う必要があるため、`JobObjectManager.Create`に名前を
  付けられるよう拡張し（`KillOnJobClose`は付けない既存方針は変更なし）、
  `RunningApp`に`job_name`を追加して永続化、`JobObjectManager.TryGetProcessIds`で
  `OpenJobObject`→`QueryInformationJobObject(JobObjectBasicProcessIdList)`により
  再オープンできるようにした
- スコープ外: MCPサーバーの常駐化、SRM管理外の任意ウィンドウに対する操作、リモート
  ネットワーク越しのMCP接続（stdio以外のtransportは本changeでは扱わない）、
  チャネルC（HvSocket）の実装
