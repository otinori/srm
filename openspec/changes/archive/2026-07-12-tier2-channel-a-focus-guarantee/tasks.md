## 1. SandboxControlChannel拡張（フォーカス要求プロトコル）

- [x] 1.1 `SandboxControlChannel`に`focus-request.json`/`focus-result.json`の
      読み書きメソッドを追加する（`WriteScenario`/`ReadScenario`と同様のパターン。
      `requestId`（GUID文字列）・`app`名を含む`FocusRequestModel`、
      `requestId`・`success`・`reason`を含む`FocusResultModel`を定義する）
- [x] 1.2 host側: `WriteFocusRequest(FocusRequestModel)`（都度同じパスへ上書き）・
      `TryReadFocusResult()`・`WaitForFocusResult(requestId, timeout)`
      （`requestId`が一致する結果が出るまでポーリング、不一致は無視）を実装する
- [x] 1.3 guest側: `BlockUntilStop`のコールバック機構を、`scenario.json`と同様に
      `focus-request.json`の`requestId`変化も検出できるよう拡張する（一度きりでは
      なく、`requestId`が変わるたびに毎回発火する）
- [x] 1.4 ユニットテストを追加する（`SandboxControlChannelTests`に
      focus-request/resultの読み書き・requestId不一致時の待機継続を確認するテストを
      追加）

## 2. ゲスト内フォーカス処理（`--nested`）

- [x] 2.1 `RunCommand.RunNested`のメインループに、フォーカス要求検出時のハンドラ
      （`HandleFocusRequest`相当）を追加する。既存の`Tier1WindowResolver.
      GetAllowedPids`/`ResolveCandidateWindows`でapp名から対象ウィンドウを解決し、
      `WindowGuard`のフォアグラウンド確認ロジック（`SetForegroundWindow`+実際に
      フォアグラウンドになったことの確認）を再利用して実行する
      （本体ロジックは`FocusRequestHandler`(Srm.Runtime.Interaction)へ切り出し、
      `RunCommand.HandleFocusRequest`はallowedPids算出とファイル書き込みのみを
      担う薄いラッパーにした。`ScenarioExecutor`と同じ「本体を独立クラスに」
      パターンを踏襲）
- [x] 2.2 対象ウィンドウが見つからない・フォーカス設定に失敗した場合、
      `FocusResultModel.Success=false`と理由を`focus-result.json`へ書く
- [x] 2.3 ユニットテストを追加する（notepad.exeを実際に起動する実Win32テストのため
      `WindowsOnlyFactAttribute`でLinux上はSkip。CIのwindows-latestランナーで
      フォーカス要求処理→`focus-result.json`書き込みの往復を確認する）

## 3. ホスト側チャネルA（Tier2のみ）

- [x] 3.1 `InteractiveInputTools.SendKey`/`SendMouse`に、Tier2の場合のみ
      `WindowGuard.Authorize`の前段としてフォーカス要求プロトコルを挟むロジックを
      追加する（`requestId`をGUIDで発行→`WriteFocusRequest`→
      `WaitForFocusResult(timeout: 2秒)`→失敗/タイムアウト時は`McpException`）
- [x] 3.2 Tier1の`send_key`/`send_mouse`はこの経路を通らないことを確認する
      （既存コードパスに影響が無いことをコードレビューで確認。`EnsureGuestFocus`
      冒頭の`if (app.Tier != 2) return;`で保証されており、`ControlDir`が
      nullでも例外にならないことをテストで確認済み）
- [x] 3.3 ユニットテストを追加する（`Srm.Mcp.Tests`に、Tier2判定時のみフォーカス
      要求が発行されること・タイムアウト時にエラーになることを確認するテストを
      追加。実際のファイルIOはフェイクの`SandboxControlChannel`相当でモックする）
      （`EnsureGuestFocus`を`internal`化しタイムアウトを引数化。実際の
      `SandboxControlChannel`を一時ディレクトリに向けて使い、別スレッドで
      ゲスト側の応答を模擬する形で成功/失敗/タイムアウトの3経路を検証した）

## 4. ドキュメント整合

- [x] 4.1 `send_key`/`send_mouse`のツール説明文から、暫定対応として追加していた
      「送信前後でscreenshotによる目視確認を徹底すること」という注意書きを外し、
      フォーカスは自動的に保証される旨に更新する
- [x] 4.2 `manual/usage.md`のチャネルA説明に、Tier2では送信前に短い（数百ms程度の）
      フォーカス確認が挟まる旨を追記する
- [x] 4.3 `records/DC-017.yaml`のreview_triggerのうち、本changeで解消する項目
      （Tier2チャネルAのフォーカス保証の恒久対策）を「解決」として更新し、
      本changeの新しいDecisionRecordへの相互参照を追加する
- [x] 4.4 本changeの決定内容を新しいDecisionRecord（`records/DC-019.yaml`）として
      記録し、`registry/records.yaml`・`views/records/DC-019.md`を作成する

## 5. ビルド・テスト検証（開発環境で可能な範囲）

- [x] 5.1 `dotnet build Srm.sln`が通ることを確認する
- [x] 5.2 `dotnet test`で新規ユニットテストがすべてパスすることを確認する
      （全88テスト成功: Srm.PolicyIntegrity.Tests 5、Srm.PolicyEngine.Tests 25、
      Srm.Runtime.Tests 74、Srm.Mcp.Tests 9）

## 6. 実機確認要（Windows実機・Tier2）

- [x] 6.1 Tier2アプリ（`tier2-notepad-test`等）を`srm_run`で起動し、`send_key`が
      毎回ゲスト内フォーカス確認を経てから入力を発行し、対象アプリへ確実に
      テキストが入力されることを確認する（DC-017 9.3で発生した「フォーカスが
      無く入力が届かない」事象が再現しないことを確認する）
      （2026-07-12実機確認。`send_key`を4回連続で呼び、すべてゲスト内notepadへ
      正しくテキストが入力されることを確認した。当初1回目の直後スクリーン
      ショットが空に見えたが、これはPrintWindow/RDP描画の反映遅延によるもので、
      少し待ってから撮ったスクリーンショットでは正しく反映されていた
      （フォーカス保証自体の不具合ではないと判断）。DC-017 9.3で発生していた
      「フォーカスが無く入力が全く届かない」事象は再現しなかった）
- [x] 6.2 意図的にゲスト内`--nested`を停止させた状態（またはフォーカス要求が
      解決できない状態）で`send_key`を呼び、タイムアウトエラーが返ることを
      確認する
      （2026-07-12実機確認。`WindowsSandbox.exe`（VM本体）のみを強制終了し
      `WindowsSandboxClient.exe`（ホスト側RDPウィンドウ）を残す形でゲスト
      無応答状態を再現したところ、`RunningAppRegistry`側がホストプロセスの
      消失を検知し「実行中のアプリが見つかりません」を即座に返した。本来
      狙っていたフォーカス確認タイムアウト経路（`EnsureGuestFocus`の
      `WaitForFocusResult`）そのものは、より手前のガードで先にエラーになる
      ため実機では未到達だったが、タイムアウトのロジック自体は
      `InteractiveInputToolsTests.EnsureGuestFocus_Tier2_TimesOutWhenGuestNeverResponds`
      でユニットテスト済み。いずれの経路でもハングせず境界のある時間で
      明確なエラーが返ることを確認した）
- [x] 6.3 `send_key`/`send_mouse`1回あたりの追加レイテンシを実測し、
      `TESTING.md`に記録する。体感上問題があれば`PollInterval`の見直しを
      別途検討する
      （2026-07-12実機実測: 初回（ゲスト起動直後）961ms、2回目以降292ms/288ms。
      対話的操作として体感上問題ないレベルと判断し、`PollInterval`の変更は
      不要と判断した）
- [x] 6.4 チャネルB（`run_scenario`）の実行時間・成功率に変化が無いことを
      再確認する（既存のBlockUntilStopコールバック拡張が影響しないことの確認）
      （2026-07-12実機確認。`run_scenario`（send_key+screenshot）が
      `success:true`で完走し、`get_scenario_result`が約2.9秒で結果を返すことを
      確認した。DC-017 9.5で確認済みの挙動から変化は見られなかった）
