## 0. 前提

このchangeは実Windows機（AppContainer/WFP/ETWが実際に動く環境）での検証を
前提とする。本セッション（Linux上のClaude Code on the web、.NET SDK未導入）
では以下のいずれも実行・ビルド検証ができないため、本tasks.mdの実装項目は
すべて未着手（`[ ]`）としている。実装・実機検証はWindows開発機で行うこと。

## 1. 実機PoC（設計確定前のブロッキングタスク）

- [ ] 1.1 `FwpmEngineSetOption0`に`FWPM_ENGINE_COLLECT_NET_EVENTS`を渡して
      有効化できるか、`FwpmNetEventSubscribe4`でこのセッションが登録した
      フィルターに一致する接続試行（許可/拒否いずれも）のコールバックを
      受け取れるかを実機で確認する。`WfpNative.cs`に無い構造体
      （`FWPM_NET_EVENT_SUBSCRIPTION0`、`FWPM_NET_EVENT_ENUM_TEMPLATE0`、
      `FWPM_NET_EVENT_CALLBACK0`等）のマーシャリングを新規に書く必要が
      あるため、design.mdの「不採用: システム監査ポリシー」判断も含めて
      ここで裏付けを取る。
- [ ] 1.2 `BlockAll`を登録しない（＝WFP的には無制限許可）状態で、
      ネットイベント購読だけで「本来ならblockされていたはずの接続」を
      `allow_hosts`との突き合わせで正しく`would_block`判定できるかを
      実機で確認する。
- [ ] 1.3 ETW `Microsoft-Windows-Kernel-File`プロバイダーから、対象PID
      （および`JobObjectManager`で追跡する子PID）のファイル読み取り
      イベントをリアルタイムで受け取れるかを実機で確認する。
      `Srm.Diagnostics.Kernel`の既存ETWセッション管理コードを流用できるか
      （同一プロセス内で`srm diag --stacktrace`用セッションと共存できるか
      含む）を確認する。
- [ ] 1.4 design.md Open Question 2（書き込みアクセスを実際に許可せずとも
      「書き込もうとした」イベントをETWで観測できるか）を実機で確認し、
      v1のスコープ（読み取りのみ観測）を維持するか広げるかを決定する。

## 2. WfpManagerのaudit拡張

- [ ] 2.1 `WfpManager.Install`とは別に、`BlockAll`を登録せず
      ネットイベント購読のみをセットアップする`WfpManager.InstallAuditMode`
      （仮称）を追加する。既存の`WfpIdentity`（PackageSid/UserSid/AppPath）
      をそのまま条件として使う。
- [ ] 2.2 受信したネットイベントを`policy.Network.AllowHosts`と突き合わせ
      て`allowed`/`would_block`を判定し、`StructuredLogger`へ記録する
      パイプラインを実装する。
- [ ] 2.3 `srm audit`終了時（`Ctrl+C`または対象プロセス終了）に購読・
      フィルター・サブレイヤー・プロバイダーを確実に後始末できることを
      確認する（既存`RemoveForApp`と同様のクリーンアップパスが必要か検討）。
- [ ] 2.4 単体/実機統合テストを追加する（`tests/Srm.Runtime.Tests/`、
      既存`WfpManagerTests.cs`のパターンに倣う）。

## 3. ファイルシステムaudit収集

- [ ] 3.1 `FileAccessAuditCollector`（仮称）を新規実装し、ETWで対象
      プロセスツリーのファイル読み取りイベントを収集して
      `policy.Filesystem.AllowPaths`と突き合わせ、`allowed`/`would_block`
      判定を`StructuredLogger`へ記録する。
- [ ] 3.2 `AclManager`に、audit実行専用の「対象パスへ広い読み取りアクセスを
      付与する」経路を追加する（design.md Open Question 1の範囲決定に
      依存。既存`GrantAccess`とは別メソッドにし、既存の拒否モードの
      挙動には影響させない）。
- [ ] 3.3 単体/実機統合テストを追加する。

## 4. CLI/Operationの配線

- [ ] 4.1 `src/Srm.Runtime/Operations/AuditOperation.cs`（仮称）を追加し、
      2系・3系の実装を組み合わせて対象アプリを起動、終了までイベントを
      収集する。
- [ ] 4.2 `src/Srm.Cli/Commands/AuditCommand.cs`を追加し、
      `srm audit <policy>`として登録する（`srm run`等の既存コマンド登録
      パターンに倣う）。
- [ ] 4.3 `srm logs <app>`で監査ログ（fs/networkのallowed/would_block
      レコード）が閲覧できることを確認する。

## 5. 安全上のガードレール

- [ ] 5.1 `srm audit`実行時に、隔離が弱まること（fs広域読み取り許可、
      network無制限）とTier2併用の推奨を警告表示する（spec.mdの
      Requirement参照）。
- [ ] 5.2 design.md Open Question 3（Tier1でのaudit実行を許可するか、
      それとも拒否してTier2限定にするか）を製品判断として決定し、
      決定に応じて`AuditCommand`にガードを実装する。

## 6. ドキュメント・DecisionRecord

- [ ] 6.1 `manual/usage.md`に`srm audit`の使い方・出力形式・安全上の
      注意を追記する。
- [ ] 6.2 「AppContainer/WFPの許可リスト方式では拒否せず記録することが
      構造的にできず、ETW／WFPネットイベントという別の観測経路が必要
      だった」という設計判断をDecisionRecordとして記録する
      （`views/records/`、既存DC-015/DC-022と同様のスタイル）。
