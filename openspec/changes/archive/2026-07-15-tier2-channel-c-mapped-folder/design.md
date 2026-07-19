## Context

DC-017（2026-07-11）はTier2向けMCP外部制御を3チャネルに分けた。チャネルA（対話的ライブ制御・ウィンドウへの`SendInput`）とチャネルB（オートパイロット）は`SandboxControlChannel`（Windows SandboxのMappedFolder＋`control/`配下のJSONファイルを250msポーリングする、DC-010由来の「IPC・デーモンなし」の思想）で実装済み。チャネルC（ファイル転送・サンドボックス内プロセスのリアルタイムモニタリング専用）はHyper-V Sockets（AF_HYPERV）を前提としていたが、「ゲスト内からカスタムサービスGUIDを開けるか」のPoCが未実施のまま保留（DC-017 review_trigger）。

その後、DC-013（outbox検疫モデル）とDC-020/021（`srm diag`のdiag-request.json/diag-result.json）が、HvSocketを使わずにチャネルA/Bと同じMappedFolder＋request/result JSONポーリングのidiomで、チャネルCが担うはずだった目的をそれぞれ個別に部分実現していた。`SandboxControlChannel`には現在、次の3つの構造的に同型なrequest/resultペアが存在する:

- `scenario.json` / `scenario-result.json`（チャネルB・1回限り）
- `focus-request.json` / `focus-result.json`（チャネルA・requestIdベースの都度上書き）
- `diag-request.json` / `diag-result.json`（診断チャネル・requestIdベースの都度上書き）

後者2つは、要求側（ホスト）が`RequestId`を発行してファイルへ上書きし、応答側（ゲスト）が`RequestId`の変化を都度検出して処理し、応答も同じパスへ上書きするという、コピー＆ペーストに近い同一パターンの実装になっている。

## Goals / Non-Goals

**Goals:**
- チャネルCの実現方式を「HvSocket・PoC待ち」から「MappedFolder＋request/result JSONポーリングidiomの一般化」へ設計レベルで置き換える
- `SandboxControlChannel`内で3回重複しているrequest/result idiomを、1つの再利用可能なプリミティブとして整理する設計を示す
- このプリミティブの上に、(a) 実行中Tier2ゲストとのオンデマンド双方向ファイル転送、(b) 継続的なプロセス/リソースモニタリング、の2capabilityを設計する
- 既存のoutbox検疫モデル（DC-013）・`srm diag`のCLI/権限モデル（DC-020/021）とのセキュリティ境界の整合性を保つ設計にする

**Non-Goals:**
- 本changeでの実装（`tasks.md`の実行）は行わない。設計のみ
- 低遅延・対話的な制御はチャネルAの領域のまま。本capabilityは250msポーリング前提であり、それを高速化する設計は含まない
- ディレクトリ単位の転送・進捗報告付きの大容量転送・転送履歴の保持は将来の拡張として扱い、本designでは単一ファイル・完了通知のみを設計する
- `tier2-process-monitor`のMCPツール公開可否（`srm diag --stacktrace`同様CLI限定にするか）は本designでは決めず、Open Questionsに残す

## Decisions

### 1. HvSocketではなくMappedFolder＋request/result JSONポーリングを正式なチャネルC実現方式とする
**代替案として検討:** (a) 当初案通りHyper-V Sockets（AF_HYPERV）を実装する、(b) 名前付きパイプ等の別IPC機構を新規導入する。
**採用しない理由:** (a)は「ゲスト内からカスタムサービスGUIDを開けるか」自体が未検証で、実現可否のリスクが最も高い。(b)はDC-016で名前付きパイプがAppContainer環境下で未解決の問題を抱えていることが判明したばかりであり（本セッションの別調査）、新規にIPC機構を追加する動機として弱い。
**採用理由:** MappedFolder＋JSONポーリングは、チャネルA/B・outbox・diag-request/resultの4箇所で既に実機検証済みの、実績のある方式である。新しい失敗モードを持ち込まずに済む。

### 2. request/result idiomを汎用プリミティブとして`SandboxControlChannel`に切り出す
scenario/focus/diagの3パターンに共通する構造（`RequestId`ベースの都度上書き、蓄積しないファイルペア、ホスト側`WaitForXxxResult(requestId, timeout)`、ゲスト側`BlockUntilStop`ループ内での変化検出）を、汎用メソッド対（例: `WriteRequest<TRequest>(string kind, TRequest request)` / `TryReadRequest<TRequest>(string kind)` / `WriteResult<TResult>(string kind, TResult result)` / `WaitForResult<TResult>(string kind, string requestId, TimeSpan timeout)`）として抽出する設計とする。ファイル名は`{kind}-request.json`/`{kind}-result.json`の命名規則に統一する。
**代替案として検討:** 既存3パターンと同じく、新capabilityごとにメソッド対をコピー＆ペーストする。
**採用しない理由:** 既に3回重複しているパターンを4回目・5回目も複製すると、`BlockUntilStop`のオーバーロード地獄（現状でも3階層のオーバーロードがある）がさらに悪化する。汎用化により`BlockUntilStop`は「登録されたrequestハンドラの辞書を毎周回す」形に単純化できる。
**トレードオフ:** 既存のscenario/focus/diagを汎用プリミティブへ移行するリファクタリングが実装コストとして発生する（本changeのtasks.mdでスコープを明確にする）。移行せず新capabilityだけ新プリミティブを使い、既存3パターンは現状維持という段階的な選択肢も残す。

### 3. ファイル転送は既存のInput/Outboxマップフォルダを再利用し、新規マウントを追加しない
`tier2-file-transfer`は新しいMappedFolderを追加せず、既存の`Input`（ホスト→ゲスト方向）・`Outbox`（ゲスト→ホスト方向）配下に`transfer/<requestId>/`サブフォルダを作り、実体ファイルをそこに置く。`control/file-transfer-request.json`にはメタデータ（`RequestId`・`Direction`・転送元/転送先の相対パス・完了フラグ）のみを載せ、実データはInput/Outbox経由で運ぶ。
**代替案として検討:** `control/`フォルダ自体にファイル実体を置く。
**採用しない理由:** `control/`はJSON専用の薄いシグナル用フォルダという既存の役割分担（DC-010）を崩さないため。Input/Outboxは元々バルクデータ用に用意されており、責務が合致する。

### 4. ゲスト→ホスト方向のファイル転送結果もDC-013の検疫モデルに従う
`tier2-file-transfer`でゲストからホストへ転送されたファイルは、`outbox/transfer/<requestId>/`に置かれた時点では`srm evidence`コマンドで参照可能になるだけで、ユーザーの作業ディレクトリへ自動反映されない。DC-013の「VM停止確定後、人間による明示的昇格」という二段構えのゲートを、実行中のオンデマンド転送でも維持する。
**トレードオフ:** 「今すぐこのファイルが欲しい」という利用感からは一段階余計に見えるが、DC-013が二段構えにした理由（スキャン結果を自動ゲートにしない）は転送のタイミングに関わらず妥当なため、一貫性を優先する。

### 5. `tier2-process-monitor`は履歴を保持しない単一スナップショット上書き方式にする
`diag-request.json`/`diag-result.json`と同じく、`monitor-result.json`は最新スナップショット1つだけを保持し、ホストが必要な頻度でポーリングして時系列を自前で構築する。ゲスト側でスナップショットを蓄積・保持することはしない。
**代替案として検討:** タイムスタンプ付きの連番ファイル（`monitor-result-0001.json`等）で履歴を残す。
**採用しない理由:** DC-017が却下した`alt-percommand-file-queue`（コマンド単位でファイルが際限なく積み上がる設計）と同じ問題を再導入することになる。`guest-host-diagnostics-channel`スペックの既存requirement「蓄積しないファイルペアで実装する」との一貫性も保てる。

## Risks / Trade-offs

- [MappedFolder経由の大容量ファイル転送のスループット・レイテンシが未検証] → 実装時にWindows実機で計測し、極端に遅い場合は本designのNon-Goals通り分割転送・進捗報告は将来の拡張として切り出す
- [`tier2-file-transfer`のパス指定を悪用した任意ファイル読み書き（パストラバーサル等）] → `SourcePath`/`DestPath`は既存のfilesystem ACLポリシー（`allow_paths`）の許可範囲内に制限し、`..`を含む相対パスや許可範囲外の絶対パスを拒否するバリデーションを実装時に必須とする
- [汎用プリミティブへの移行がscenario/focus/diagの既存の実機検証済み挙動を壊すリスク] → 移行は本designのDecision 2で述べた通り任意（段階的）とし、既存3パターンを壊さないことをtasks.md側で受け入れ基準に含める
- [`tier2-process-monitor`のポーリング間隔（250ms）では捕捉できない短時間のイベントがある] → 本designのGoalsは「継続的なモニタリング」であり、`srm diag --stacktrace`（DC-021、ETWベースの高頻度カーネルサンプリング）が既に担っている高精度なユースケースを置き換える意図はないと明記する

## Migration Plan

本changeは新規capability設計であり、既存の稼働中コンポーネントを置き換えるものではない。既存のscenario/focus/diagパターンをDecision 2の汎用プリミティブへ移行するかどうかは、実装時にtasks.mdで段階的に判断する（移行しない場合でも新capabilityの追加自体は成立する）。ロールバックは、追加したcapability・ファイルを削除するのみで完結する。

## Open Questions

- `tier2-file-transfer`・`tier2-process-monitor`をMCPツールとして公開するか、`srm diag --stacktrace`同様CLI限定（管理者権限・調査用途）にとどめるかは未決定。利用シーンが具体化してから別changeで判断する
- Decision 2の汎用プリミティブへ既存3パターン（scenario/focus/diag）を移行するかどうかは、実装コストとリスクを見て実装時に判断する
- ディレクトリ単位の転送・大容量ファイルの分割転送は将来必要になった時点で別途設計する
