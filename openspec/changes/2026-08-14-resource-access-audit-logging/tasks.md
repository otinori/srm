## 0. 前提・引き継ぎ

このchangeは実Windows機（AppContainer/ETWが実際に動く環境）での検証を
前提とする。本セッション（Linux上のClaude Code on the web、.NET SDK・
実Windows機なし）では、コードの読み書きとGitHub公開ソース（microsoft/
perfview）の参照による設計検証はできたが、**`dotnet build`・`dotnet test`・
実機実行のいずれも一度も行えていない**。

以下、チェックボックスは「実際に検証が完了したもの」だけに`[x]`を付ける
という既存プロジェクトの慣習（tasks.mdの過去のchange参照）を踏襲し、
コードを書いただけの項目は`[ ]`のまま「(実装済み・未ビルド・未検証)」と
注記する。**次のセッションはまずセクション1から着手すること。**

## 1. ビルド・実機検証（最優先・ブロッキング）

- [ ] 1.1 `dotnet build Srm.sln`（または各csproj）が通ることを確認する。
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
- [ ] 1.2 `dotnet test tests/Srm.Runtime.Tests`で
      `AuditVerdictClassifierTests`が通ることを確認する。
      `ClassifyHost_UnresolvedHost_ReturnsWouldBlock`は実際に`example.com`
      をDNS解決するため、オフライン環境では失敗しうる点に注意
      （`WfpManagerTests`が既に同じ前提を持っているのでプロジェクトの
      既存の慣習には沿っているが、CI環境がオフラインの場合は別途検討）。
- [ ] 1.3 実機で`srm audit <tier1-policy> --scope <path>`を実行し、
      対象アプリが`--scope`内のファイルへアクセスした際に
      `audit_fs`ログが記録されること、`--scope`外へのアクセスは（観測
      すらされず）拒否されることを確認する。
- [ ] 1.4 実機で同じ実行中に、対象アプリが`network.allow_hosts`外への
      接続を試みた際に`audit_net`ログが記録されること（拒否されず
      実際に接続が成立/試行されること）を確認する。
- [ ] 1.5 `AuditTraceCollector`のセッション名（`SRM-Audit-{appName}-
      {guid}`、`KernelTraceEventParser.KernelSessionName`以外の任意名）で
      `EnableKernelProvider`が実際に動作すること、`srm diag --stacktrace`
      と同時実行しても互いに干渉しないことを確認する（design.mdの
      「セッション名の衝突回避」の裏付け）。
- [ ] 1.6 Ctrl+Cで`srm audit`を止めた際、`AuditTraceCollector.Dispose`の
      `_session.Stop()`が呼ばれてから`_processingThread`が実際に
      `Join`で合流できる（＝`Source.Process()`が想定通り戻る）ことを
      確認する。戻らない場合はタイムアウト後の扱い（現状5秒で
      `Join`を諦めるだけで例外にしない）が妥当か再検討する。
- [ ] 1.7 子プロセスを生成するアプリで、`JobObjectManager.
      TryGetProcessIds`のポーリング（`AuditCommand`、500ms間隔）で
      新しいPIDが`AuditTraceCollector.SetTrackedPids`に反映され、
      その子プロセスのファイル/ネットワークアクセスも記録されることを
      確認する。

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
- [ ] 5.4 実機で`srm logs <app>`から`audit_fs`/`audit_net`ログが問題なく
      閲覧できることを確認する（`StructuredLogger`は既存実装のままなので
      リスクは低いはずだが未確認）。

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
- [ ] 7.3 実機での動作確認が完了した後、「AppContainer/WFPの許可リスト
      方式では拒否せず記録することが構造的にできず、ETWという別の観測
      経路が必要だった」という設計判断をDecisionRecordとして記録する
      （`views/records/`、既存DC-015/DC-022と同様のスタイル。実機検証の
      結果・実機のみで判明した訂正点もあれば併せて記録する）。
