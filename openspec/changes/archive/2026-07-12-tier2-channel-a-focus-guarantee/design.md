## Context

DC-017（`records/DC-017.yaml`）は、Tier2のチャネルA（対話的ライブ制御）をホスト側MCPサーバーが
`WindowsSandboxClient.exe`ウィンドウ（RDP描画面）へ直接`SendInput`する方式として設計した
（decision 3）。この方式はゲスト内`--nested`側の変更を一切必要としないことが利点だったが、
2026-07-12の実機検証（`TESTING.md` 9.3節）で、ホスト側`WindowGuard`のフォアグラウンド検証は
`WindowsSandboxClient.exe`ウィンドウについてのみ行われ、ゲスト内のどのウィンドウが実際に
入力フォーカスを持っているかは検証できない構造的な限界が判明した。結果として、guardが
入力発行を許可しても、ゲスト内で意図したアプリケーションにキー入力が届かないことがある。

一方、チャネルB（オートパイロット）はゲスト内`--nested`プロセスが同一セッション内で
`SendInput`するため、この問題が原理的に発生しない（実機で確認済み）。ゲスト内`--nested`は
チャネルBのために既に`SandboxControlChannel`（`control/`配下のファイルベースhost↔guest
同期、DC-010）を介して稼働しており、Tier1と同じ`WindowGuard`/`Tier1WindowResolver`/
`InputSender`をそのまま再利用できる状態にある。

DC-017は過去に「Tier2の対話的操作を`control/`配下の1コマンド1ファイルのキューで実装する」案
（`alt-percommand-file-queue`）を明示的に却下している。理由は、AIによる長時間の対話的操作を
想定すると command/result ファイルが際限なく積み上がり、かつ`run/`ディレクトリ自体に
保持期限の仕組みが元々存在しないため。本changeはこの反省を踏まえ、汎用コマンドキューを
作らない設計を選ぶ。

## Goals / Non-Goals

**Goals:**
- Tier2チャネルAの`send_key`/`send_mouse`が、送信前にゲスト内対象アプリへ確実に
  入力フォーカスを持たせられるようにする（ホスト側からの「保証できない」という
  構造的限界を解消する）。
- DC-017 decision 1（非常駐・IPC/デーモンなし方針）およびDC-017が却下した
  `alt-percommand-file-queue`（ファイルが際限なく積み上がる汎用コマンドキュー）を
  再導入しない。
- チャネルAの対話性（低遅延な「操作→結果を見る→次を決める」ループ）への影響を
  実用上問題ないレベル（数百ms程度の追加遅延）に抑える。
- 既存のTier1向け`WindowGuard`/`Tier1WindowResolver`/`InputSender`をゲスト内で
  そのまま再利用し、新規のウィンドウ解決ロジックを作らない（チャネルBが既に
  やっていることと同じ再利用パターンを踏襲する）。

**Non-Goals:**
- チャネルAの実際のキー/マウスイベント自体をゲスト内`--nested`経由に切り替えることは
  しない（それは事実上チャネルBへの統合であり、チャネルAが低遅延な直接操作である
  という設計意図・DC-017 decision 3の前提を壊す。イベント送信は引き続きホストから
  RDPウィンドウへ直接`SendInput`する）。
- チャネルC（HvSocket、ファイル転送・モニタリング用）の実現可否検討は本changeの
  スコープ外。本changeはチャネルC実現に依存しない（依存させるとPoC未了のリスクを
  背負うことになるため、あえて切り離す）。
- Tier1チャネルAには変更を加えない（Tier1は同一セッション内で直接ウィンドウハンドルを
  扱えるため、この問題は原理的に発生しない）。

## Decisions

### 検討した選択肢

| 案 | 概要 | 対話性への影響 | DC-017整合性 |
|---|---|---|---|
| A. 現状維持（ドキュメントのみ） | `send_key`の説明文で目視確認を促すのみ | 影響なし | 整合するが恒久対策ではない |
| B. チャネルAをチャネルBへ統合 | 実際のキー/マウスイベントも`control/`ファイル経由でゲスト内`--nested`が代行 | 大（ポーリング間隔分の遅延が毎回発生、対話的操作に不向き） | decision 3の「直接注入」という設計意図と矛盾 |
| C. フォーカス確認のみゲスト内に委譲（採用） | 送信直前に軽量な「フォーカス要求/結果」ファイルペアでゲスト内にフォーカスだけ設定させ、実際のキー/マウスイベントは従来通りホストから直接RDPウィンドウへ送る | 小（1回の`send_key`/`send_mouse`につき数百ms） | 却下済みの汎用コマンドキューとは異なる形（1組の上書き型ファイルペア、蓄積しない）で実現可能 |

**採用: 案C**。理由は、フォーカス保証という「ゲスト内でなければ原理的に検証・実行できない」
部分だけを最小限ゲスト側に委譲し、実際の入力イベント配送という「低遅延であるべき」部分は
ホストから直接送る従来方式を維持できるため。DC-017 decision 3が守りたかった性質
（低遅延な直接操作）と、9.3で判明した限界（フォーカス未検証）の両方に同時に対応できる。

### ファイルベースプロトコルの設計（`alt-percommand-file-queue`の反省を踏まえる）

`SandboxControlChannel`に、`scenario.json`/`scenario-result.json`と同様の
**上書き型（蓄積しない）** ファイルペアを追加する:

- `control/focus-request.json`（host→guest）: `{ "requestId": "<GUID>", "app": "<policy name>" }`
  ホストが`send_key`/`send_mouse`の直前に**毎回同じファイルパスへ上書き**する
  （新しいファイルを追加で作るのではない）。
- `control/focus-result.json`（guest→host）: `{ "requestId": "<GUID>", "success": true, "reason": null }`
  ゲスト内`--nested`が要求を処理した後、同じファイルパスへ上書きする。

`requestId`で対応する要求/結果のペアを識別する（`run_scenario`/`scenario-result.json`と同じ
「1回投入・1回結果」のパターンを、繰り返し呼べる形に一般化したもの。ファイルは2つで固定
であり、呼び出し回数に応じて増えることはないため、`alt-percommand-file-queue`が問題視した
「際限なく積み上がる」性質を持たない）。

ゲスト内`--nested`のメインループ（`RunCommand.RunNested`の`control.BlockUntilStop(...)`）に、
`scenario.json`監視と同様の仕組みで`focus-request.json`の`requestId`変化を検出するコールバックを
追加する。検出したら、Tier1と同じ`Tier1WindowResolver.GetAllowedPids`/
`ResolveCandidateWindows`でapp名から対象ウィンドウを解決し、`SetForegroundWindow`相当の
処理（`WindowGuard`のフォアグラウンド確認ロジックを再利用）を実行し、結果を
`focus-result.json`へ書く。

ホスト側`InteractiveInputTools.SendKey`/`SendMouse`（Tier2の場合のみ）は、既存の
`WindowGuard.Authorize`によるホスト側フォアグラウンド確認の**前**に、この
`focus-request.json`書き込み→`focus-result.json`待機（タイムアウト: 2秒、
ポーリング間隔は`SandboxControlChannel`の既存`PollInterval`=250msをそのまま使う）を行う。
ゲスト側が失敗を返した場合、または待機がタイムアウトした場合は`McpException`で
理由付きエラーを返す（fail-closed、DC-017 decision 4の思想を踏襲）。

### なぜ`run_scenario`の`scenario.json`機構をそのまま使わないか

`scenario.json`は「VM起動前に一度だけ配置する」設計（`SandboxLauncher.Launch`のコメント参照:
ゲスト内`--nested`起動直後にpolicy.yamlを読むため、それより後に配置すると即座に失敗する）
であり、実行中に繰り返し書き換える用途を想定していない。`BlockUntilStop`の
`onScenarioDetected`コールバックも「一度だけ呼ばれる」設計（`scenarioHandled`フラグ）。
フォーカス要求は`send_key`/`send_mouse`のたびに繰り返し発生するため、別ファイルペア・
別コールバック（毎回発火する）として独立させる方が、既存のシナリオ機構の一発性という
性質を壊さずに済む。

## Risks / Trade-offs

- [リスク] `send_key`/`send_mouse`1回あたり数百ms（ポーリング間隔250ms×往復）の追加遅延が
  発生する → [対策] チャネルAの用途（AIエージェントの探索的操作）では許容範囲と判断する。
  実機での実測値を`TESTING.md`に追記し、体感上問題があれば`PollInterval`の見直しを検討する。
- [リスク] ゲスト内`--nested`が既にクラッシュ・応答不能な場合（DC-018で修正した
  パッケージング不整合のような別要因）、`focus-result.json`が永遠に書かれずタイムアウトする
  → [対策] 既存の`readySignalTimedOut`と同様、タイムアウトを検出可能なエラーとして
  呼び出し元に返す（サイレントハングにしない）。
- [リスク] `focus-request.json`/`focus-result.json`の`requestId`不一致（ホストが新しい要求を
  書いた直後にゲストが古い結果を返す競合）→ [対策] ホスト側は`focus-result.json`の
  `requestId`が自分が書いた`requestId`と一致するまで待ち続ける（不一致は無視して
  ポーリングを継続する）。
- [トレードオフ] 本design自体がDC-017の実装範囲を拡張する（decision 3にゲスト内変更が
  一部加わる）。DC-017の「チャネルAはゲスト内エージェントの変更を一切必要としない」という
  記述と矛盾するため、実装完了後にDC-017を直接書き換えるのではなく、新しい
  DecisionRecord（DC-019相当）としてこの変更を記録し、DC-017からは相互参照する。

## Open Questions

- タイムアウト値（2秒案）と`PollInterval`（250ms）は実機での体感を見て調整が必要か。
- `focus-request.json`の`app`が、現在ゲスト内で認識している`RunningApp`と一致しない場合
  （例: ゲスト再起動直後で`--nested`がまだpolicy.yamlしか読んでいない場合）のエラー
  ハンドリングは、実装時に既存の`ResolveTargetWindow`のエラーメッセージ方針に合わせる。
