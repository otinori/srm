## Why

現在のSRMは、ファイルシステム（AppContainerのDACL許可リスト、`AclManager`/
`allow_paths`）とネットワーク（WFPの`BlockAll`＋`allow_hosts`許可、
`WfpManager`）のいずれも、ポリシー外のリソースへのアクセスを「拒否」する
形でしか扱えない。拒否は一方向の判定であり、拒否された瞬間にOSレベルで
遮断されるため、SRM自身は「何にアクセスしようとしたか」を観測・記録できない。

これは以下のような場面で不足になる。

- **未知のアプリ/エージェントのポリシー作成時**: そのアプリが実際にどの
  パス・どのホストへアクセスしようとするか事前にわからないため、
  `allow_paths`/`allow_hosts`を最小権限で書くには試行錯誤（拒否→
  ログでエラー原因を確認→ポリシーに追記、を繰り返す）が必要になっている。
- **セキュリティリスク評価**: 信頼していないアプリ・AIエージェントの実際の
  挙動（どの機密パスに触れようとするか、どの外部ホストと通信しようとするか）
  を安全に観測したい場合、現状は拒否したことしかわからず、「何をしようと
  したか」の全体像が見えない。Procmon/Wiresharkなど外部ツールを都度手動で
  併用するほかなく、SRMの標準ワークフロー（`srm run`→`srm logs`）に
  統合されていない。

本changeは、拒否せずアクセス試行を記録する新しい実行モード（`srm audit`）を
追加し、この用途に対応する。

## What Changes

- 新しいCLIサブコマンド `srm audit <policy>` を追加する。指定ポリシーの
  アプリを「拒否なし・全記録」モードで起動する。
  - **ネットワーク**: `WfpManager`の`BlockAll`ルールを登録せず、代わりに
    WFPのネットイベント購読（`FwpmNetEventSubscribe4`）でこのアプリ
    （識別子は既存の`WfpIdentity`＝PackageSid/UserSid/AppPathをそのまま
    再利用）が発信した接続試行を観測する。`policy.Network.AllowHosts`と
    突き合わせて`allowed`/`would_block`を判定し、`StructuredLogger`で
    JSONL記録する。
  - **ファイルシステム**: AppContainerのDACL許可リスト方式のままでは
    未許可パスへのアクセスがOSレベルで即座に拒否され観測不能なため、
    audit実行では対象アプリに広い読み取りアクセスを付与した上で、ETW
    （`Microsoft-Windows-Kernel-File`、既存`Srm.Diagnostics.Kernel`の
    `Microsoft.Diagnostics.Tracing.TraceEvent`依存を再利用）でファイル
    I/Oイベントを収集する。`policy.Filesystem.AllowPaths`と突き合わせて
    `allowed`/`would_block`を判定し、同じくJSONL記録する。
  - 記録形式は既存`StructuredLogger`のJSONLをそのまま使い、`srm logs`で
    閲覧できるようにする。ポリシーYAMLのスキーマ変更は不要（`allow_paths`/
    `allow_hosts`を「許可済みかどうかの判定基準」としてそのまま流用する）。
- **安全上の制約（重要・BREAKING ではないが明示が必須）**: auditモードは
  意図的に隔離を弱める（fs広域読み取り許可、network無制限）。
  `srm audit`実行時、Tier1ポリシーに対しては「このモードはサンドボックス
  として機能しない（脱走防止なし）」旨を警告表示する。信頼できない
  バイナリの挙動観測にはTier2（Windows Sandbox、VM境界がある）での実行を
  強く推奨する文言をヘルプ・警告に含める。

## Capabilities

### New Capabilities
- `resource-access-audit-logging`: ファイルシステム/ネットワークへの
  アクセス試行を、拒否せずに記録するaudit実行モード。

## Impact

- 新規: `src/Srm.Runtime/Operations/AuditOperation.cs`（仮）、
  `src/Srm.Cli/Commands/AuditCommand.cs`。
- `WfpManager`: `BlockAll`を登録しない代わりにネットイベント購読を行う
  新メソッド（例: `WfpManager.InstallAuditMode`）を追加。`WfpNative.cs`に
  `FwpmNetEventSubscribe4`/`FWPM_NET_EVENT_SUBSCRIPTION0`等、現状未使用の
  P/Invoke宣言が新規に必要（design.md参照、実機PoCが前提）。
- 新規: `src/Srm.Runtime/Diagnostics/FileAccessAuditCollector.cs`（仮）—
  ETWでファイルI/Oを観測し`AllowPaths`と突き合わせる。既存
  `Srm.Diagnostics.Kernel`のTraceEvent利用パターンを踏襲する。
- `AclManager`: audit実行時に対象パスへ広い読み取りアクセスを付与する
  経路が必要（既存`GrantAccess`とは別の、audit専用の緩和版）。
- `StructuredLogger`: フォーマット自体の拡張は不要（既存`Log(level,
  message, extra)`をそのまま利用）。
- ドキュメント: `manual/usage.md`に`srm audit`の使い方と安全上の注意を
  追記。この提案が前提とする「AppContainer/WFPの許可リスト方式では
  拒否せず記録することが構造的にできない」という制約と、その回避策として
  ETW/WFPネットイベントを採用した経緯をDecisionRecordとして残す
  （tasks.md参照）。
- 影響しない範囲: 既存の`srm run`/`srm stop`の拒否ロジック（`WfpManager`の
  `BlockAll`+`allow_hosts`、`AclManager`の許可リスト方式）は本change
  以前と変わらない。`srm audit`は別コマンドとして追加され、既存フローには
  一切介入しない。
