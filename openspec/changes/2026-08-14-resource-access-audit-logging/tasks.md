## 0. 前提・引き継ぎ

このchangeは実Windows機（AppContainer/ETWが実際に動く環境）での検証を
前提とする。当初のセッション（Linux上のClaude Code on the web、.NET SDK・
実Windows機なし）では、コードの読み書きとGitHub公開ソース（microsoft/
perfview）の参照による設計検証はできたが、`dotnet build`・`dotnet test`・
実機実行のいずれも一度も行えていなかった。

2026-08-15、Windows実機（ユーザーの実マシン、管理者PowerShell）で
セクション1の実機検証を実施。ビルド・単体テストは無修正で成功したが、
実際に`srm audit`を動かす過程で**実装バグを2件・環境要因を1件**発見し、
コード側の2件はその場で修正・再ビルド・再検証まで完了した:

1. `AclManager.GrantReadAccessForAudit`が、TrustedInstaller所有の保護
   ディレクトリ（`C:\Windows\System32\drivers\etc`等）を`--scope`に
   指定すると`ERROR_ACCESS_DENIED`で未処理例外クラッシュしていた
   （他のACL付与箇所と違いベストエフォートの`catch`が無かった）。
   → 修正済み（2.2の該当コード参照）。
2. `AuditTraceCollector.Start`が`session.Source.Kernel.XXX += ...`
   （イベント購読）を`session.EnableKernelProvider(...)`より先に
   呼んでいたため、`.Source`への初回アクセスが暗黙に空のセッションを
   起動してしまい、その後の`EnableKernelProvider`が
   「セッションは最初に一度だけ有効化できる」という内部チェック
   （`TraceEventSession.IsValidSession`）に毎回必ず引っかかって
   例外を投げていた。マシン環境に関係なく100%再現する順序バグ
   だった（3.1参照）。→ 修正済み（`EnableKernelProvider`を先に
   呼ぶよう入れ替え、失敗時に`session.Dispose()`する`try/catch`も
   追加してセッションリークも修正）。
3. （環境要因）Windowsセキュリティの「コントロールされたフォルダー
   アクセス」が、`srm.exe`による保護ディレクトリへのACL変更試行を
   ブロックし、（2の修正前は）長時間ハングして見える状態を引き起こ
   していた。ユーザー環境で`srm.exe`を許可アプリに追加して回避。
   一般ユーザー向けの案内（マニュアル等）が必要か要検討。

以下、チェックボックスは「実際に検証が完了したもの」だけに`[x]`を付ける
という既存プロジェクトの慣習（tasks.mdの過去のchange参照）を踏襲する。
1.5（`srm diag --stacktrace`との同時実行）・1.7（子プロセスのPID追跡）
は、テスト対象スクリプトが端末側の別問題（後述）で早期終了したため
今回未検証のまま残っている。次にこのchangeに着手するセッションは、
1.5・1.7の追試と、7.3のDecisionRecord作成から始めること。

## 1. ビルド・実機検証（最優先・ブロッキング）

- [x] 1.1 `dotnet build Srm.sln`（または各csproj）が通ることを確認する。
      2026-08-14、Windows実機（.NET SDK 8.0.423）で確認済み。
      `dotnet build Srm.sln`は0警告・0エラーで成功。TraceEventパッケージ
      3.2.4の実バイナリとも下記のAPI面はすべて一致し、修正不要だった。
      本セッションはコンパイラを一度も通していないため、単純な構文誤り・
      名前空間の誤り・存在しないAPIの参照などが残っている可能性がある。
      特に以下を重点的に確認する:
      - `src/Srm.Diagnostics.Kernel/AuditTraceCollector.cs`:
        `KernelTraceEventParser.Keywords.FileIOInit`/`NetworkTCPIP`、
        `session.Source.Kernel.FileIOCreate`/`TcpIpConnect`/
        `TcpIpConnectIPV6`、`FileIOCreateTraceData.FileName`、
        `TcpIpConnectTraceData.daddr`/`dport`、
        `TcpIpV6ConnectTraceData.daddr`/`dport`、`TraceEvent.TimeStamp`/
        `ProcessID`。これらはmicrosoft/perfviewのGitHub公開ソース
        （`src/TraceEvent/Parsers/KernelTraceEventParser.cs`、
        `src/TraceEvent/TraceEvent.cs`、`src/TraceEvent/
        TraceEventSession.cs`）を参照して実装したが、実際にインストール
        される`Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.4
        （`Srm.Diagnostics.Kernel.csproj`にピン留め済み）のAPI面と完全に
        一致するかは未確認。
      - `src/Srm.Runtime/Operations/AuditOperation.cs`・
        `src/Srm.Runtime/AclManager.cs`・
        `src/Srm.Runtime/AppContainerLauncher.cs`の変更箇所。
      - `src/Srm.Cli/Commands/AuditCommand.cs`（`System.CommandLine`
        2.0.0-beta4.22272.1での`Option<string[]>`
        `{ AllowMultipleArgumentsPerToken = false }`の挙動を含む）。
- [x] 1.2 `dotnet test tests/Srm.Runtime.Tests`で
      `AuditVerdictClassifierTests`が通ることを確認する。
      2026-08-14、Windows実機・オンライン環境で確認済み。6件全て合格
      （`ClassifyHost_UnresolvedHost_ReturnsWouldBlock`のexample.comへの
      実DNS解決も含む）。なお同じ実行で`Srm.Runtime.Tests`全体を流すと
      `RestrictedAccountTests`等8件が失敗するが、これは非管理者シェルで
      ローカルアカウント作成(`NetUserAdd`)がエラーコード5で拒否される
      環境要因であり、本changeの差分とは無関係（既存のテストで、この
      changeでは変更していない）。
      `ClassifyHost_UnresolvedHost_ReturnsWouldBlock`は実際に`example.com`
      をDNS解決するため、オフライン環境では失敗しうる点に注意
      （`WfpManagerTests`が既に同じ前提を持っているのでプロジェクトの
      既存の慣習には沿っているが、CI環境がオフラインの場合は別途検討）。
- [x] 1.3 実機で`srm audit <tier1-policy> --scope <path>`を実行し、
      対象アプリが`--scope`内のファイルへアクセスした際に
      `audit_fs`ログが記録されること、`--scope`外へのアクセスは（観測
      すらされず）拒否されることを確認する。
      2026-08-15、Windows実機で確認済み。`policies\audit-test.yaml`
      （powershell.exeを`%TEMP%\srm-audit-test`配下で実行）で
      `srm audit audit-test --scope "%TEMP%\srm-audit-test" --scope
      "C:\Windows\System32\drivers\etc"`を実行し、`audit_fs`が正しく
      記録・分類されることを確認（`allow_paths`内の`infile.txt`等は
      `Allowed`、それ以外は`WouldBlock`）。
      【訂正点】「--scope外は観測すらされず拒否される」という前提は
      部分的にしか正しくない。実機では`C:\Program Files`や
      `C:\Windows\System32`配下など、Windowsの既定ACLで元々
      ALL APPLICATION PACKAGESに読み取りが許可されている場所は、
      `--scope`に含めていなくても普通に読めてしまい、`WouldBlock`
      判定のまま観測される（拒否はされない）。「観測すらされず拒否」
      になるのは、他ユーザーのプロファイル等、本当にACLで保護された
      場所に限られる。design.mdの前提を修正すべき実機発見。
- [x] 1.4 実機で同じ実行中に、対象アプリが`network.allow_hosts`外への
      接続を試みた際に`audit_net`ログが記録されること（拒否されず
      実際に接続が成立/試行されること）を確認する。
      2026-08-15、Windows実機で確認済み。`allow_hosts`内の
      `example.com`へのHTTPS接続が`audit_net`に`Allowed`として記録
      された（リモートアドレスはIPv6: `2606:4700:10::ac42:93f3:443`、
      `TcpIpConnectIPV6`イベント経路の実機動作も確認）。
      `allow_hosts`外ホストへの接続（`WouldBlock`となるケース）は、
      テストスクリプト側のエラー（後述）で未実行のまま。次回実機
      検証時に追試が必要。
- [x] 1.5 `AuditTraceCollector`のセッション名（`SRM-Audit-{appName}-
      {guid}`、`KernelTraceEventParser.KernelSessionName`以外の任意名）で
      `EnableKernelProvider`が実際に動作すること、`srm diag --stacktrace`
      と同時実行しても互いに干渉しないことを確認する（design.mdの
      「セッション名の衝突回避」の裏付け）。
      2026-08-15、Windows実機で確認済み。`srm audit`実行中（対象PID
      監視中）に別ターミナルから`srm diag --pid <対象PID> --stacktrace
      --duration-ms 3000`を実行し、セッション名の衝突エラーは発生
      しなかった（`srm-audit`側の任意名カーネルセッションと`srm diag
      --stacktrace`側の予約名"NT Kernel Logger"セッションが実際に
      共存できることを確認）。結果は`TotalSamples: 0`
      （`"対象プロセスのCPUサンプリングイベントが採取できませんでした"`）
      だったが、対象プロセスが`Start-Sleep`でアイドル中でCPUをほぼ
      消費していなかったためであり、想定通り（バグではない）。
- [x] 1.6 Ctrl+Cで`srm audit`を止めた際、`AuditTraceCollector.Dispose`の
      `_session.Stop()`が呼ばれてから`_processingThread`が実際に
      `Join`で合流できる（＝`Source.Process()`が想定通り戻る）ことを
      確認する。戻らない場合はタイムアウト後の扱い（現状5秒で
      `Join`を諦めるだけで例外にしない）が妥当か再検討する。
      2026-08-15、Windows実機で確認済み。1回目の実行ではテスト対象
      スクリプトが端末側エラーで異常終了し「対象プロセスが既に終了
      している」分岐を通ったが、2回目の実行では対話的にCtrl+Cを
      押して`stopRequested`分岐（`proc.Kill(entireProcessTree: true)`
      を含む経路）を確認できた。いずれの分岐でも`using var collector`
      のDisposeが正常に完了し、`_session.Stop()`→
      `_processingThread.Join()`がハングせず、`srm audit 終了`の
      サマリーがログ・コンソール双方に正しく出力された。
- [ ] 1.7 子プロセスを生成するアプリで、`JobObjectManager.
      TryGetProcessIds`のポーリング（`AuditCommand`、500ms間隔）で
      新しいPIDが`AuditTraceCollector.SetTrackedPids`に反映され、
      その子プロセスのファイル/ネットワークアクセスも記録されることを
      確認する。
      2026-08-15、Windows実機で検証したが、**子プロセスのイベントは
      観測できなかった**（実装のギャップの可能性が高い、要修正
      検討）。`cmd.exe /c type infile.txt >nul`を`Start-Process -Wait`
      で子プロセスとして起動したが、ログには子プロセス由来のPIDが
      一切出現せず、`C:\WINDOWS\System32\cmd.exe`は代わりに**親
      PowerShellのPID**でWouldBlock観測されていた（`Start-Process`が
      実行ファイルを解決する際に親プロセス自身が読んだものと推測）。
      考えられる原因: `cmd /c type ... >nul`は数十ms程度で完了する
      短命プロセスであり、`JobObjectManager.TryGetProcessIds`の
      500msポーリング間隔では、子プロセスの生成と終了の間に
      `SetTrackedPids`へ反映するタイミングを間に合わせられない
      （ポーリングして新PIDを追加する前に子プロセスが終了し、ETW側の
      `_trackedPids.Contains`フィルタで弾かれ続けた可能性）。短命な
      子プロセスの監視漏れは設計上の既知の限界としてdesign.mdに
      明記するか、ポーリング間隔を短縮する等の対策を検討すべき。
      DecisionRecordに記録する。

## 2. WfpManager/AppContainerLauncher/AclManagerの変更

- [x] 2.1 `AppContainerLauncher.Launch`に`forceInternetCapability`
      パラメータを追加（既定`false`、既存呼び出し元は無変更）。コード
      実装済み・未ビルド・未検証。
- [x] 2.2 `AclManager.GrantReadAccessForAudit`を追加（既存の
      `GrantTraverseChain`/`GrantPathAccess`プライベートヘルパーを
      そのまま再利用、読み取り専用）。コード実装済み・未ビルド・未検証。
- [x] 2.3 `WfpManager`は無変更（design.mdの決定により、auditモードは
      WFPを一切呼ばない）。

## 3. ETW観測コレクタ

- [x] 3.1 `src/Srm.Diagnostics.Kernel/AuditTraceCollector.cs`を実装
      （リアルタイムETWセッション、`FileIOInit`+`NetworkTCPIP`
      キーワード、`FileAccess`/`NetworkConnect`イベント、
      `SetTrackedPids`によるPIDフィルタ更新）。コード実装済み・
      未ビルド・未実機検証（セクション1参照）。

## 4. 判定ロジック・データ型

- [x] 4.1 `src/Srm.Runtime/Diagnostics/AuditModels.cs`
      （`AuditVerdict`/`AuditFileEvent`/`AuditNetworkEvent`/
      `AuditSummary`）を実装。
- [x] 4.2 `src/Srm.Runtime/Diagnostics/AuditVerdictClassifier.cs`を実装
      （`allow_paths`のプレフィックス一致、`allow_hosts`を解決したIP
      集合との突き合わせ、ループバックの扱いは`WfpManager.
      AddAllowRules`と同じ規約）。
- [x] 4.3 単体テスト`tests/Srm.Runtime.Tests/Diagnostics/
      AuditVerdictClassifierTests.cs`を実装（実行はセクション1.2参照）。

## 5. CLI/Operationの配線

- [x] 5.1 `src/Srm.Runtime/Operations/AuditOperation.cs`を実装
      （Tier1限定チェック、`--scope`必須チェック、ACL付与、Job Object
      作成、`forceInternetCapability: true`での起動）。
- [x] 5.2 `src/Srm.Cli/Commands/AuditCommand.cs`を実装（`--scope`省略時の
      `application.working_directory`フォールバック、隔離しないことの
      警告表示、Ctrl+C/プロセス終了検知、`AuditTraceCollector`の
      イベント購読とログ記録、終了時のサマリー表示）。
- [x] 5.3 `src/Srm.Cli/Program.cs`に`AuditCommand.Build(policiesDir)`を
      登録。
- [x] 5.4 実機で`srm logs <app>`から`audit_fs`/`audit_net`ログが問題なく
      閲覧できることを確認する（`StructuredLogger`は既存実装のままなので
      リスクは低いはずだが未確認）。
      2026-08-15、Windows実機で確認済み。`srm logs audit-test`で
      `audit_fs`/`audit_net`のJSONLエントリが問題なく表示された。

## 6. 安全上のガードレール

- [x] 6.1 `AuditCommand.Run`の冒頭で、隔離が弱まること（fs広域読み取り
      許可、network無制限）とTier2併用の推奨を警告表示する（コード
      実装済み）。
- [x] 6.2 Tier2ポリシーを指定した場合は`AuditOperation.Execute`が
      明確なエラーで終了する（design.md Decision「v1はTier1限定」）。

## 7. ドキュメント・DecisionRecord

- [x] 7.1 `manual/usage.md`に`srm audit`のコマンドリファレンス（使い方・
      出力例・安全上の注意・`--scope`の制約）を追記。
- [x] 7.2 `README.md`のコマンド一覧表に`srm audit`を追記。
- [x] 7.3 実機での動作確認が完了した後、「AppContainer/WFPの許可リスト
      方式では拒否せず記録することが構造的にできず、ETWという別の観測
      経路が必要だった」という設計判断をDecisionRecordとして記録する
      （`views/records/`、既存DC-015/DC-022と同様のスタイル。実機検証の
      結果・実機のみで判明した訂正点もあれば併せて記録する）。
      2026-08-15、[DC-028](../../../views/records/DC-028.md)として記録
      した（`views/all-records.md`にも追記済み）。実機で発見・修正した
      2件の実装バグ、環境要因1件、実機のみで判明した仕様上のギャップ
      （短命子プロセス追跡漏れ）・訂正点（「--scope外は観測すらされず
      拒否」の精緻化）を記録。
