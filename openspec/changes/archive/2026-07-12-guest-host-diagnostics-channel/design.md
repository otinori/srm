## Context

DC-016はTier1（AppContainer）の`claude-code -p`実行で発生するカーネルモードCPU張り付きの未解決バグを記録している。当初のユーザー要望は「ゲストとホストで通信する仕組みを実装し、その通信を使って情報採取を容易にする」であったが、`policies/claude-code.yaml`は`tier: 1`であり、Tier1にはTier2の`--nested`のような別プロセス（別VM）としての「ゲスト」は存在しない。対象アプリはホストと同一セッション上で直接動く、AppContainerトークンで隔離されたプロセスに過ぎない。

そのため設計は「本当にホスト↔ゲストの通信が要るのはTier2のみ」という前提に立ち、Tier1は既存のホストプロセスから直接`System.Diagnostics.Process`でサンプリングする（ユーザーの追加確認により「Tier1・Tier2両方やる」で合意済み）。

## Goals / Non-Goals

**Goals:**
- `srm diag <app>`で、Tier1・Tier2いずれの実行中アプリに対しても、CPU時間内訳（ユーザー/カーネル）・ページフォールト率・スレッド数・ハンドル数を短時間サンプリングして返す。
- Tier2はDC-019で確立した`SandboxControlChannel`の「固定パス2ファイルを都度上書きし、requestIdの不一致は無視する」プロトコルをそのまま踏襲し、新規のIPC機構を発明しない。
- Tier1/Tier2で同じサンプリングロジック（`DiagCollector`）を再利用し、実装・テストの二重化を避ける。

**Non-Goals:**
- WPR（Windows Performance Recorder）による完全なカーネルスタックトレース採取は行わない。`.etl`ファイルの転送はDC-017が将来オプションとして温存した「チャネルC（HvSocket）」の領域であり、本changeはそのPoC待ちにはしない代わりに、スタックトレースそのものの取得は引き続き手動のWPR操作に委ねる（review_trigger参照）。
- 真のコンテキストスイッチ数/秒の取得は行わない。`NtQuerySystemInformation`等の非公開APIに依存せず、`GetProcessMemoryInfo`（psapi.dll、公開API）で取得できるページフォールト数のレートで代替する。
- MCPツールとしての公開は行わない。通常のAIエージェント操作（`send_key`/`send_mouse`/`run_scenario`等）とは異なり、DC-016のような実機調査時にのみ人間（または調査を行うエージェント自身がCLIを直接叩く形）が使う想定のため。

## Decisions

1. **Tier1は往復させず直接呼び出す。** Tier1のAppContainer隔離はセキュリティトークンの制限であり、プロセスの可視性自体はホストから常に見える（`Process.GetProcessById`で直接アクセス可能）。ここに往復プロトコルを持ち込むと複雑さが増すだけで得るものがない。

2. **Tier2はfocus-request/resultと同じ「固定パス2ファイル上書き」方式を再利用する。** DC-017が却下した「1コマンド1ファイルのキュー」の問題（ファイルが際限なく積み上がる）を再導入しないため、`diag-request.json`/`diag-result.json`もこのパターンを踏襲する。`BlockUntilStop`の既存のオーバーロード連鎖に`onDiagRequestDetected`を追加する形で、`onFocusRequestDetected`と同じrequestId差分検出ロジックを流用する。

3. **サンプリングは同一プロセス内で前後2回スナップショットを取り、差分から秒あたりレートを計算する。** ホスト側で複数回`Get-Counter`相当を叩いて時系列を組み立てるのではなく、1回のリクエストで「指定した経過時間だけ待って差分を返す」設計にすることで、Tier2の1往復で完結させ、ポーリング頻度に依存する精度のブレを避ける。

4. **`DiagCollector`は対象PIDのみを受け取り、ウィンドウ解決を行わない。** `FocusRequestHandler`と異なり診断対象はプロセスであってウィンドウではないため、`Tier1WindowResolver`等は使わない。Tier2ゲスト側は起動時に記録済みの`appProcessId`をそのまま渡すだけで済み、`FocusRequestModel`のような`app`名の一致確認も不要（プロセスIDは起動時に一意に決まっているため）。

5. **ページフォールト数は`GetProcessMemoryInfo`（psapi.dll）へのP/Invokeで取得する。** `System.Diagnostics.PerformanceCounter`パッケージの追加は見送った。DC-018で顕在化したように、この`Srm.Runtime.csproj`は`tools/package.ps1`が複数の実行ファイルを同一binフォルダへpublishする都合上パッケージバージョンの整合性がシビアであり、新規パッケージ依存を増やすリスクを避け、BCL標準の`Process`クラスと単一の公開Win32 API呼び出しのみで完結させた。

## Risks / Trade-offs

- [サンプリング中の対象プロセス終了] → `DiagCollector.Handle`は`ArgumentException`（PID解決失敗）・`InvalidOperationException`（サンプリング中の終了）の両方を捕捉し、`Success=false`と理由を返す（例外を投げない）。
- [Tier2でのサンプリング中、ゲスト内`BlockUntilStop`ループが他のイベント（focus-request等）を検出できない] → `SampleWindowMs`は数百ms〜数秒程度の短時間を想定した一回限りの調査用操作であり、通常のチャネルA/B運用と時間的に競合しないことを前提に許容する。
- [真のコンテキストスイッチ数/秒が取得できない] → Non-Goalsで明示した通り、必要になった場合はWPRによる手動調査（チャネルCの領域）に委ねる。

## Migration Plan

新規追加のみで既存の挙動（チャネルA/B、Tier1/Tier2の起動・停止フロー）には影響しない。ロールバックは本changeで追加したファイル・CLIサブコマンドを削除するのみで完結する。

## Open Questions

- DC-016の根本原因特定に本ツールがどこまで寄与するかは、実際にTier1で`claude-code`の再現待ち状態に対して`srm diag`を使ってみるまで未知数。カーネル時間比率が高いことの再確認はできるが、具体的な関数特定にはWPRが引き続き必要になる可能性が高い。
