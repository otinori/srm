## 1. 汎用request/resultプリミティブの抽出

- [x] 1.1 `SandboxControlChannel`に`kind`文字列を引数に取る汎用メソッド対（`WriteRequest<TRequest>`/`TryReadRequest<TRequest>`/`WriteResult<TResult>`/`WaitForResult<TResult>`）を追加する
- [x] 1.2 ゲスト側`BlockUntilStop`ループを、登録されたkindごとのハンドラを辞書で回す形に単純化する（既存の3階層オーバーロードを解消） — 既存3(4)パターン用の型付きオーバーロードは残しつつ、末尾に`genericRequestHandlers`パラメータを追加した新しいオーバーロードを追加する形にした（下記1.3参照、既存パターンは非破壊のまま）
- [x] 1.3 既存のscenario/focus/diagパターンを新プリミティブへ移行するか、現状維持のままにするかを実装時に判断し記録する（design.md Decision 2参照） — **現状維持を選択**。design.md Decision 2が最初から移行を任意としており、scenario/focus/diag/mcp（design.md執筆後にchannel-d-guest-mcp-bridgeで追加された4パターン目）はいずれも実機検証済みの安定動作のため、リファクタリングで壊すリスクを避けた。新規capability（file-transfer/process-monitor）のみ新プリミティブを使う
- [x] 1.4 移行する場合、既存のTier2実機検証手順（DC-017/DC-019/DC-020/DC-021で確認済みの挙動）が壊れていないことを実機で再確認する — 1.3で「移行しない」と判断したため対象外。既存4パターンのコードは無変更

## 2. tier2-file-transfer実装

- [x] 2.1 `file-transfer-request.json`/`file-transfer-result.json`のモデル（`RequestId`・`Direction`・転送元/転送先パス・完了フラグ）を定義する — `FileTransferModel.cs`
- [x] 2.2 `SandboxRunPaths`の`Input`/`Outbox`配下に`transfer/<RequestId>/`サブフォルダを作成するロジックを実装する — `FileTransferOperation.Put`（ホスト側Input配下ステージング）と`FileTransferRequestHandler`（ゲスト側Outbox配下配置）
- [x] 2.3 転送元/転送先パスを対象ポリシーの`filesystem.allow_paths`で検証し、範囲外・パストラバーサルを拒否するバリデーションを実装する — `PathAllowlist.cs`（新規）、`FileTransferRequestHandler`から使用
- [x] 2.4 ホスト→ゲスト方向の転送（`Input`経由）を実装する — `FileTransferOperation.Put`/`FileTransferRequestHandler.Handle`
- [x] 2.5 ゲスト→ホスト方向の転送（`Outbox`経由、DC-013検疫モデル準拠）を実装する — `FileTransferOperation.Get`/`FileTransferRequestHandler.Handle`。design.md Decision 4通り、転送されたファイルはoutbox配下に置かれるだけで、既存のDC-013検疫（`srm stop`時のスキャン→`srm evidence promote`）をそのまま経由する（新規の検疫コードは不要だった）
- [x] 2.6 CLIサブコマンド（例: `srm transfer`）またはMCPツール化の要否を実装時に判断する（design.md Open Questions参照） — **CLIサブコマンドを選択**（`srm transfer put`/`srm transfer get`）。MCPツール化は見送り、Open Questionのまま次changeへ持ち越す
- [x] 2.7 Windows実機でホスト→ゲスト・ゲスト→ホスト双方向の転送を検証する — 実施済み（DC-025参照）: マップフォルダ内(`C:\srm\outbox`)・マップフォルダ外だがallow_paths範囲内(`C:\Windows\Temp\transfer-allowed`)の両方でput成功、allow_paths範囲外への転送は拒否、put→getの往復でコンテンツ一致を確認、allow_paths範囲外からのget拒否も確認

## 3. tier2-process-monitor実装

- [x] 3.1 `monitor-request.json`/`monitor-result.json`のモデル（対象PID・ポーリング継続条件・プロセス一覧/リソース使用状況）を定義する — `ProcessMonitorModel.cs`。対象PIDは要求に含めず、既存のJob Object配下プロセスツリー解決（focus/scenarioと同じ`allowedPids`パターン）を再利用する設計にした
- [x] 3.2 ゲスト側で継続的にスナップショットを`monitor-result.json`へ上書きするロジックを実装する（履歴を蓄積しない） — `RunCommand.RunProcessMonitorLoop`（バックグラウンドスレッド、`BlockUntilStop`本体をブロックしないため）、`ProcessMonitorHandler.Handle`
- [x] 3.3 対象プロセスが解決できない・消失した場合に`Success=false`を返すエラーハンドリングを実装する — `ProcessMonitorHandler`: プロセスツリーの一部が消失しても即失敗にはせず、全滅した場合のみ`Success=false`（実機で「対象アプリ自体が消失」と「子プロセス1件が正常終了」を区別する必要があったため）
- [x] 3.4 CLIサブコマンド（例: `srm diag <app> --watch`）またはMCPツール化の要否を実装時に判断する（design.md Open Questions参照） — **`srm diag <app> --watch [--interval-ms] [--count]`を選択**（design.mdの例示通り）。MCPツール化は見送り
- [x] 3.5 Windows実機で継続ポーリング・ファイル非蓄積を確認する — 実施済み（DC-025参照）: `tier2.app_container: false`の実行中Tier2アプリに対し、`--count 4`で4件の異なるタイムスタンプのスナップショットを取得、親プロセス(cmd.exe)と子プロセス(ping.exe)の両方が一覧に含まれることを確認。**副次的発見**: `tier2.app_container: true`（既定）のアプリに対しては、既存の`srm diag`（本change変更前からの機能）も含めPID解決が失敗する未解決の問題を発見・DC-025のreview_triggerに記録した（本changeのスコープ外、修正は次回持ち越し）

## 4. ドキュメント・記録の更新

- [x] 4.1 DC-017のreview_triggerにある「チャネルC（HvSocket）」前提を置き換える新しいDecisionRecordを作成する — [DC-025](../../../views/records/DC-025.md)。DC-017自体のreview_triggerも解決済みとして更新した
- [x] 4.2 README/`views/records`にチャネルCの新しい実現方式を反映する — DC-025を`registry/records.yaml`・`views/all-records.md`へ登録。`manual/usage.md`に`srm transfer`/`srm diag --watch`のコマンドリファレンスを追加
- [x] 4.3 本changeを実装完了後にアーカイブする（`openspec-archive-change`） — 実装完了後に実施
