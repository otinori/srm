## Context

`WfpManager`（`src/Srm.Runtime/WfpManager.cs`）と`AclManager`
（`src/Srm.Runtime/AclManager.cs`）は、いずれも「許可リストに載っていない
ものは既定で拒否される」という構造で動いている。

- ネットワーク: `WfpManager.Install`が`BlockAll`フィルター（重み最小＝
  最優先ではなく、条件数の少なさで自動的に低優先になる設計）と、
  `allow_hosts`ごとの`PERMIT`フィルターを同一サブレイヤーに登録する。
  拒否はWFPカーネルコンポーネントが行い、SRM（ユーザーモード）は個々の
  接続試行を観測していない。
- ファイルシステム: `AclManager.GrantAccess`が`allow_paths`にのみDACLの
  ACEを付与する。未許可パスにはACEが無いため、NTFS/AppContainerトークンの
  標準的なアクセスチェックが自動的に拒否する。SRMは各アクセス試行を
  フックしていない（フックする仕組み自体が存在しない）。

このため、「拒否せず記録する」を実現するには、ネットワーク／ファイル
システムのそれぞれで、今とは別の観測経路を追加する必要がある。

## Goals / Non-Goals

**Goals:**
- 拒否を行わずに、対象アプリ（子プロセスを含む）が試みたファイルアクセス・
  ネットワーク接続を記録する`srm audit`モードを追加する。
- 記録した各アクセスについて、既存ポリシーの`allow_paths`/`allow_hosts`と
  照合した`Allowed`/`WouldBlock`の判定を付与し、そのまま
  `filesystem.allow_paths`/`network.allow_hosts`の下書きに使える形にする。
- 既存の`srm run`（拒否モード）のロジック・挙動には一切影響しない。

**Non-Goals:**
- auditモードを新しい隔離レベル（Tier3など）として設計すること。auditは
  隔離ではなく観測が目的であり、隔離が必要な場合は既存Tier1/Tier2と組み
  合わせて使う前提（後述の「安全上の注意」、および現状Tier1限定という
  制約）。
- ファイルシステムの書き込みアクセスまで安全に開放して観測すること。v1
  では読み取りアクセスの観測のみをスコープとする（Open Questions参照）。
- Windowsシステム全体の監査ポリシー（`auditpol`のFiltering Platform
  Connection/Object Accessサブカテゴリ）を変更すること。本changeはシステム
  全体の設定を一切変更しない、この特定アプリだけにスコープされた観測手段
  のみを使う（既存コードの「アプリごとに専用のprovider/subLayer/session/
  AppContainer/専用アカウントを使う」という一貫した設計方針を踏襲）。
- Tier2対応（v1はTier1限定。Decision参照）。

## Decisions

### Decision: audit専用の別コマンド（`srm audit`）として追加する（`srm run`のフラグにしない）

`srm run --audit`のようなフラグ拡張ではなく、独立した`srm audit`
サブコマンドとする。理由:

- 意味論が根本的に異なる（`run`＝拒否による隔離、`audit`＝拒否なしの観測）。
  フラグ1つで意味が反転する設計は、誤ってフラグを付け忘れた/付け間違えた
  場合の事故（本来隔離したいのに素通しになる）のリスクが高い。
- `srm evidence`や`srm diag`と同様、目的ごとに専用コマンドを立てるのが
  このプロジェクトの既存パターンに合う。
- `srm run`はバックグラウンドで起動して即座に戻る設計だが、`srm audit`は
  ETWイベントを消費し続ける必要がありフォアグラウンド・ブロッキングに
  なる（`srm diag --watch`に近いUX）。この起動モデルの違いもコマンドを
  分ける理由になる。

### Decision（実装時に確定）: ネットワーク観測もETW（Kernel-Network）に一本化し、WFPネットイベント購読は不採用にする

提案初期の検討では、ネットワークはWFPのネットイベント購読
（`FwpmNetEventSubscribe4`）で観測する案だった。実装フェーズで以下の理由
により不採用とし、ファイルシステム側と同じくETW
（`Microsoft-Windows-Kernel-Network`、`KernelTraceEventParser.Keywords.
NetworkTCPIP`）に一本化した。

- **API面のリスクの違いが大きい**: `FwpmNetEventSubscribe4`まわり
  （`FWPM_NET_EVENT_SUBSCRIPTION0`・`FWPM_NET_EVENT4`・コールバック委譲）は
  現状の`WfpNative.cs`に存在しない、ネストしたunionを含む複雑な構造体の
  新規P/Invoke実装が必要になる。対して、既存`Srm.Diagnostics.Kernel`が
  既に依存する`Microsoft.Diagnostics.Tracing.TraceEvent`
  （microsoft/perfview、`KernelCpuStackSampler`が実機で動作確認済み）は、
  `TcpIpConnect`/`TcpIpConnectIPV6`イベントに対する型付きの`daddr`/`dport`/
  `ProcessID`プロパティを標準で提供しており、マーシャリングを自前で書く
  必要が無い（`microsoft/perfview`の公開ソースで`TcpIpConnectTraceData`/
  `TcpIpV6ConnectTraceData`の実装を確認済み）。
- **観測手段の統一**: ファイルシステム側も同じくETW
  （`Microsoft-Windows-Kernel-File`）で観測する設計であるため、ネットワーク
  側もETWに揃えることで、単一の`AuditTraceCollector`（1つのリアルタイム
  ETWセッション、`FileIOInit`と`NetworkTCPIP`の両キーワードを同時有効化）
  で両方をカバーできる。WFPとETWという2つの異質なネイティブAPI面を検証・
  維持するコストを避けられる。
- **副次的な単純化**: ネットワークをブロックしないと決めた時点で、audit
  実行時にWFPフィルターを一切登録しない（`WfpManager`を呼ばない）という
  選択肢が生まれた。その場合、AppContainerの`internetClient`ケーパビリティ
  さえ強制付与すれば（`AppContainerLauncher.Launch`の新しい
  `forceInternetCapability`パラメータ）、WFP層は完全に無関与にできる。

**セッション名の衝突回避**: `KernelCpuStackSampler`は予約名`"NT Kernel
Logger"`（`KernelTraceEventParser.KernelSessionName`）を使うが、この名前は
Windows 7以前ではシステム全体で同時に1つしか使えない制約がある。Windows 8
以降は任意のセッション名でも`EnableKernelProvider`が動作し（`TraceEventSession.
cs`内の`OperatingSystemVersion.AtLeast(62)`分岐で確認）、複数のカーネル
セッションを同時に持てる。SRMの対象OS（Windows 10/11、README参照）は
いずれもこれに該当するため、`AuditTraceCollector`は`SRM-Audit-{appName}-
{guid}`という専用のセッション名を使い、`srm diag --stacktrace`や複数の
`srm audit`の同時実行と衝突しないようにした。

**技術的リスク（実機PoCが前提、tasks.md参照）**: 上記の判断、および
`AuditTraceCollector`の実装（`session.Source.Kernel.FileIOCreate`/
`TcpIpConnect`/`TcpIpConnectIPV6`、`FileIOCreateTraceData.FileName`、
`TcpIpConnectTraceData.daddr`/`dport`、`TraceEvent.TimeStamp`/`ProcessID`、
`TraceEventSession(sessionName)`のリアルタイムコンストラクタ、
`EnableKernelProvider`の引数）は、`microsoft/perfview`リポジトリを実際に
`git clone`してソースを直接確認し、これらのAPI（`TraceEventDispatcher.
Kernel`プロパティの実在含む）が存在すること自体は裏付けた。ただし、
実際にインストールされる`Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.4
のバイナリでのビルド可否、および実際のイベント発火タイミング・正確な
フィールド内容、`session.Stop()`呼び出しから`Source.Process()`のブロック
解除までの遅延は未検証（この既存コードベース自体、WFPのUserSid条件で
実機のみで判明した`FWP_E_TYPE_MISMATCH`のような前例がある領域）。

### Decision: ファイルシステム観測はETW（`Microsoft-Windows-Kernel-File`）を使い、audit実行時は対象パスへの読み取りアクセスを事前に付与する

代替案として、NTFSオブジェクトアクセス監査（SACL＋`SYSTEM_AUDIT_ACE`、
セキュリティイベントログ4656/4663相関）も検討したが、不採用とした。

**採用: ETW（`Microsoft-Windows-Kernel-File`）**
- `Srm.Diagnostics.Kernel`が既に`Microsoft.Diagnostics.Tracing.TraceEvent`
  に依存し、ETWカーネルセッションを扱うコードが存在する（`srm diag
  --stacktrace`、`FileIOCreateTraceData`の`FileName`を既に利用している）。
  同じ依存・パターンを転用でき、新規サードパーティ依存を増やさない。
- PID単位のフィルタリングが容易で、`JobObjectManager`が既に追跡している
  プロセスツリー（子プロセス含む）にそのままスコープできる
  （`AuditCommand`が`JobObjectManager.TryGetProcessIds`をポーリングし、
  `AuditTraceCollector.SetTrackedPids`で追跡対象PID集合を更新する）。
- システム全体の監査ポリシーを変更しない。

**重要な制約**: AppContainerのDACL許可リストはそのままでは「未許可パスは
即座に拒否」のままなので、拒否せず観測するには、audit実行の対象パスに
事前に（既存`AclManager.GrantAccess`とは別の、audit専用の）広い読み取り
アクセスを付与しておく必要がある（`AclManager.GrantReadAccessForAudit`）。
つまりaudit実行中、対象アプリは実際に広い範囲を読み取れる状態になる。
v1では書き込みアクセスまでは広げない（読み取りだけを観測対象にする、
Non-Goals参照）。

### Decision: 監視スコープ（`--scope`）はポリシーYAMLではなくCLIオプションにする

`filesystem.allow_paths`/`network.allow_hosts`と違い、audit観測の対象
パス範囲は一回限りの診断作業の性質が強く、ポリシーファイルにチェックイン
して恒久化する類の設定ではないと判断した。`PolicyModel`/`PolicyValidator`
（enforcement側と共有するスキーマ）を変更せずに済むため、影響範囲も
小さくなる。`srm diag`の`--duration-ms`/`--top`と同じ位置づけ。

未指定時は`application.working_directory`にフォールバックする
（`AuditCommand.ResolveDefaultScope`）。

### Decision: v1はTier1限定にする（Open Question 3の決定）

Tier2でauditを行うには、`StackTraceOperation`/DC-021が採用したパターン
（ETWセッションはサンプリング対象と同じカーネル上でしか動かせないため、
ゲスト内`--nested`自身がETWセッションを張り、`SandboxControlChannel`の
request/resultプリミティブでホストへ結果を転送する）を、単発のリクエスト/
レスポンスではなく「対象アプリの生存期間中ずっと継続するストリーム」に
拡張する必要があり、v1のスコープを大きく超える。`AuditOperation.Execute`
は`policy.Tier != 1`で明確なエラーを返す。Tier2対応は将来の別changeとする。

## Open Questions（残存）

1. **書き込みアクセスの扱い**: v1では読み取りのみを観測する前提だが、
   「書き込もうとしたパス」もリスク評価上は重要な情報になりうる。書き込みを
   実際に許可して観測するのか、書き込みシステムコール自体は拒否したまま
   「書き込もうとした」というイベントだけをETWで拾えるのか
   （`FileIo/Write`イベントの発火条件次第、要実機確認）は技術検証が必要。
2. **`--scope`外へのアクセスは観測すらできない**: `--scope`で広げた範囲の
   外側は引き続きAppContainerのDACLで拒否されるため、「本当は触りたかった
   が拒否されて観測できなかったパス」が残りうる。`--scope`を広げるほど
   隔離が弱まるトレードオフはユーザーの判断に委ねている
   （`manual/usage.md`に明記）。
3. **Tier2対応**: 上記Decision参照。将来の別changeとする。
