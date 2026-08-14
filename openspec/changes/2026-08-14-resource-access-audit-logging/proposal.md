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

- 新しいCLIサブコマンド `srm audit <policy> [--scope <path>]...` を追加する。
  指定ポリシーのアプリを「拒否なし・全記録」モードで、`srm run`と異なり
  フォアグラウンド・ブロッキングで起動する（対象アプリの生存期間中ずっと
  ETWイベントを消費し続ける必要があるため）。
  - **設計変更（実装中に確定した決定）**: 当初案は「ネットワークはWFPの
    ネットイベント購読（`FwpmNetEventSubscribe4`）で観測する」だったが、
    実装時にETW（`Microsoft-Windows-Kernel-Network`、`KernelTraceEventParser.
    Keywords.NetworkTCPIP`）に一本化した。理由は、(a) 既存`Srm.Diagnostics.
    Kernel`が依存する`Microsoft.Diagnostics.Tracing.TraceEvent`が既に
    `TcpIpConnect`/`TcpIpConnectIPV6`イベントの型付きラッパーを提供しており、
    WFPの生の`FWPM_NET_EVENT4`構造体（ネストしたunionを含む複雑なマーシャ
    リングが必要）を新規にP/Invoke実装するより大幅にリスクが低いこと、
    (b) ファイルシステム側と観測手段（ETW＋TraceEvent）を統一でき、コードの
    重複と検証すべき未知のAPI面を減らせること。詳細はdesign.md参照。
  - **ネットワーク**: `WfpManager`を一切呼ばない（`BlockAll`はもちろん
    `allow_hosts`のPERMITルールも登録しない＝WFP層では無制限）。
    `AppContainerLauncher.Launch`に`forceInternetCapability: true`を渡し、
    `allow_hosts`の内容に関わらずAppContainerの`internetClient`ケーパビリ
    ティを強制付与する（これが無いとWindows組み込みのAppContainerネット
    ワーク隔離で全アウトバウンドがブロックされ、観測が意味を成さない）。
    実際の接続試行はETW（`TcpIpConnect`/`TcpIpConnectIPV6`）で観測し、
    `policy.Network.AllowHosts`を解決したIP集合と突き合わせて`Allowed`/
    `WouldBlock`を判定する。
  - **ファイルシステム**: AppContainerのDACL許可リスト方式のままでは
    未許可パスへのアクセスがOSレベルで即座に拒否され観測不能なため、
    audit実行では`--scope`で指定したパス（省略時は`application.
    working_directory`）へ読み取りのみの広いアクセスを付与した上で、ETW
    （`Microsoft-Windows-Kernel-File`、`KernelTraceEventParser.Keywords.
    FileIOInit`の`FileIOCreate`イベント）でファイルI/Oを観測する。
    `policy.Filesystem.AllowPaths`と突き合わせて`Allowed`/`WouldBlock`を
    判定する。
  - 記録形式は既存`StructuredLogger`のJSONLをそのまま使い、`srm logs`で
    閲覧できるようにする。ポリシーYAMLのスキーマ変更は不要（`allow_paths`/
    `allow_hosts`を「許可済みかどうかの判定基準」としてそのまま流用する）。
    監視対象パス（scope）はポリシーYAMLではなく`srm audit`のCLIオプション
    （`--scope`、複数指定可）として渡す（PolicyModel/PolicyValidatorという
    enforcement側の共有スキーマを変更しない、影響範囲を絞るための判断）。
- **Tier1限定（v1のスコープ）**: Tier2対応は、`StackTraceOperation`/DC-021
  と同様にゲスト内`--nested`自身にETWセッションを張らせ`control/`経由で
  結果を転送する追加配線が必要で、v1のスコープを大きく超えるため見送った。
  Tier2ポリシーを指定した場合は明確なエラーで終了する。
- **安全上の制約（重要・BREAKING ではないが明示が必須）**: auditモードは
  意図的に隔離を弱める（fs広域読み取り許可、network無制限）。`srm audit`
  実行時、このモードがサンドボックスとして機能しない（脱走防止なし）旨を
  毎回警告表示する。信頼できないバイナリの挙動観測にはTier2（Windows
  Sandbox、VM境界がある）での実行を推奨する文言をヘルプ・警告に含める
  （Tier2対応が入るまでは、使い捨て可能な環境での実行を推奨する注記に
  留める）。

## Capabilities

### New Capabilities
- `resource-access-audit-logging`: ファイルシステム/ネットワークへの
  アクセス試行を、拒否せずに記録するaudit実行モード。

## Impact

実装済み（本セッションはLinux上のClaude Code on the webで実行されており、
.NET SDK・実Windows機がないため、以下はいずれも**未ビルド・未実機検証**。
tasks.mdの実機検証チェックリストを参照）。

- 新規: `src/Srm.Runtime/Operations/AuditOperation.cs` — Tier1限定の
  オーケストレーション（ACL付与、AppContainer起動、Job Object管理）。
  `WfpManager`を呼ばない点が`RunOperation.RunTier1`との最大の違い。
- 新規: `src/Srm.Runtime/Diagnostics/AuditModels.cs`/
  `AuditVerdictClassifier.cs` — 判定結果の型と、パス/ホストをallow_paths/
  allow_hostsと突き合わせる純粋ロジック（ネイティブAPI非依存、単体テスト
  可能）。
- 新規: `src/Srm.Diagnostics.Kernel/AuditTraceCollector.cs` — リアルタイム
  ETWセッション（`FileIOInit`＋`NetworkTCPIP`キーワード）でファイル/
  ネットワークイベントを観測するコレクタ。既存`KernelCpuStackSampler`と
  同じNuGet依存（`Microsoft.Diagnostics.Tracing.TraceEvent`）を使うが、
  固定時間サンプリング＋ETL後処理ではなくリアルタイム購読方式にした
  （対象アプリの生存期間中ずっと観測し続ける必要があるため）。
- 新規: `src/Srm.Cli/Commands/AuditCommand.cs` — CLIエントリポイント。
  `Srm.Runtime`は`Microsoft.Diagnostics.Tracing.TraceEvent`に依存させない
  既存の設計方針（TraceEvent利用は常に`Srm.Cli`経由）を踏襲し、ETW収集の
  呼び出しはここで行う。
- `src/Srm.Runtime/AclManager.cs`: `GrantReadAccessForAudit`を追加（既存の
  `GrantTraverseChain`/`GrantPathAccess`プライベートヘルパーを再利用する
  読み取り専用版）。
- `src/Srm.Runtime/AppContainerLauncher.cs`: `Launch`に
  `forceInternetCapability`パラメータを追加（既定`false`、既存呼び出し元の
  挙動は変えない）。
- `src/Srm.Cli/Program.cs`: `AuditCommand`をルートコマンドへ登録。
- ドキュメント: `manual/usage.md`/`README.md`に`srm audit`を追記。
- 単体テスト:
  `tests/Srm.Runtime.Tests/Diagnostics/AuditVerdictClassifierTests.cs`
  （ネイティブAPI非依存のため`[Fact]`、Windows専用ではない）。
- 影響しない範囲: 既存の`srm run`/`srm stop`の拒否ロジック（`WfpManager`の
  `BlockAll`+`allow_hosts`、`AclManager`の`GrantAccess`許可リスト方式）は
  本change以前と変わらない。`srm audit`は別コマンドとして追加され、既存
  フローには一切介入しない。
