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
  照合した`allowed`/`would_block`の判定を付与し、そのまま
  `filesystem.allow_paths`/`network.allow_hosts`の下書きに使える形にする。
- 既存の`srm run`（拒否モード）のロジック・挙動には一切影響しない。

**Non-Goals:**
- auditモード自体を新しい隔離レベル（Tier3など）として設計すること。
  auditモードは隔離ではなく観測が目的であり、隔離が必要な場合は既存の
  Tier1/Tier2と組み合わせて使う前提（後述「安全上の注意」）。
- ファイルシステムの書き込みアクセスまで安全に開放して観測すること。
  v1では読み取りアクセスの観測のみをスコープとする（Open Questions参照）。
- Windowsシステム全体の監査ポリシー（`auditpol`のFiltering Platform
  Connection/Object Accessサブカテゴリ）を変更すること。本changeは
  システム全体の設定を一切変更しない、この特定アプリだけにスコープされた
  観測手段のみを使う（既存コードの「アプリごとに専用のprovider/subLayer/
  session/AppContainer/専用アカウントを使う」という一貫した設計方針を踏襲）。

## Decisions

### Decision: audit専用の別コマンド（`srm audit`）として追加する（`srm run`のフラグにしない）

`srm run --audit`のようなフラグ拡張ではなく、独立した`srm audit`
サブコマンドとする。理由:

- 意味論が根本的に異なる（`run`＝拒否による隔離、`audit`＝拒否なしの観測）。
  フラグ1つで意味が反転する設計は、誤ってフラグを付け忘れた/付け間違えた
  場合の事故（本来隔離したいのに素通しになる）のリスクが高い。
- `srm evidence`や`srm diag`と同様、目的ごとに専用コマンドを立てるのが
  このプロジェクトの既存パターンに合う。

### Decision: ネットワーク観測はWFPネットイベント購読（`FwpmNetEventSubscribe4`）を使う

代替案として、Windowsの高度な監査ポリシー（「フィルタリング プラット
フォーム接続」サブカテゴリ、成功/失敗監査を有効化しセキュリティイベント
ログ5156/5157を読む）も検討したが、不採用とした。

**採用: `FwpmNetEventSubscribe4`**
- このプロセス（このWFPセッション）内で購読・受信が完結し、システム全体の
  監査ポリシーを変更しない。他のアプリ・他のSRMポリシー実行には一切影響
  しない。
- 既存`WfpManager`が持つ識別子抽象（`WfpIdentity`＝PackageSid/UserSid/
  AppPath）をそのまま条件・フィルタリングに再利用できる。

**不採用: システム監査ポリシー＋セキュリティイベントログ**
- `auditpol /set`はマシン全体に影響する設定変更であり、対象アプリ以外の
  全プロセスの接続もログされ始める（ノイズが大きく、他の目的のログ収集と
  競合しうる）。
- セキュリティイベントログの読み取りには追加の特権・ポーリングが必要で、
  `srm audit`終了時のクリーンアップ（監査ポリシーを元に戻す）も必要になり、
  「アプリごとにスコープを閉じる」という既存設計方針から外れる。

**技術的リスク（実機PoCが前提、tasks.md参照）**: `FwpmNetEventSubscribe4`・
`FWPM_NET_EVENT_SUBSCRIPTION0`・`FWPM_NET_EVENT_ENUM_TEMPLATE0`・
コールバック委譲（`FWPM_NET_EVENT_CALLBACK0`）は、現状の`WfpNative.cs`に
存在しない新規のP/Invoke面である。`FwpmEngineSetOption0`で
`FWPM_ENGINE_COLLECT_NET_EVENTS`を有効化する必要がある点も含め、実機での
動作確認が必須（WfpManagerの既存コードも、複数箇所で「実機検証で判明した
否定的結果」がドキュメント化されている＝仕様書だけでは動作を断定できない
領域）。

### Decision: ファイルシステム観測はETW（`Microsoft-Windows-Kernel-File`）を使い、audit実行時は対象パスへの読み取りアクセスを事前に付与する

代替案として、NTFSオブジェクトアクセス監査（SACL＋
`SYSTEM_MANDATORY_LABEL`ならぬ`SYSTEM_AUDIT_ACE`、セキュリティイベント
ログ4656/4663相関）も検討したが、不採用とした。

**採用: ETW（`Microsoft-Windows-Kernel-File`）**
- `Srm.Diagnostics.Kernel`が既に`Microsoft.Diagnostics.Tracing.TraceEvent`
  に依存し、ETWカーネルセッションを扱うコードが存在する（`srm diag
  --stacktrace`）。同じ依存・パターンを転用でき、新規サードパーティ依存を
  増やさない。
- PID単位のフィルタリングが容易で、`JobObjectManager`が既に追跡している
  プロセスツリー（子プロセス含む）にそのままスコープできる。
- システム全体の監査ポリシーを変更しない。

**不採用: SACLベースのオブジェクトアクセス監査**
- 監査対象にする全パス（＝観測したい範囲全体、通常はユーザープロファイル
  やドライブ全体など広め）へのSACL設定が必要で、`AclManager`のコメントに
  ある通りNTFSの継承伝播だけで数分かかることがある（大きいディレクトリ
  ツリーでは実用的な待ち時間にならない）。
- こちらもマシン全体の監査ポリシー有効化（`auditpol`）が前提になり、
  ネットワーク側と同じ理由で不採用。

**重要な制約**: AppContainerのDACL許可リストはそのままでは「未許可パスは
即座に拒否」のままなので、拒否せず観測するには、audit実行の対象パスに
事前に（既存`AclManager.GrantAccess`とは別の、audit専用の）広い読み取り
アクセスを付与しておく必要がある。つまりaudit実行中、対象アプリは
実際に広い範囲を読み取れる状態になる。これは「隔離を弱める」ということ
であり、Non-Goalsで述べた通りv1では書き込みアクセスまでは広げない
（読み取りだけを観測対象にする）。

## Open Questions

1. **audit実行で読み取りアクセスを付与する範囲**: ポリシーの
   `application.working_directory`＋既存`allow_paths`の親ディレクトリまで
   に限定するのか、`%USERPROFILE%`全体やドライブ全体まで広げるのか。
   狭すぎると「本当は触りたかったが拒否されて観測できなかったパス」が
   残ってしまい、広すぎると隔離をほぼ無効化してしまう。ポリシーに
   `audit.scope_paths`のような明示フィールドを追加するか検討が必要。
2. **書き込みアクセスの扱い**: v1では読み取りのみを観測する前提だが、
   「書き込もうとしたパス」もリスク評価上は重要な情報になりうる。
   書き込みを実際に許可して観測するのか、書き込みシステムコール自体は
   拒否したまま「書き込もうとした」というイベントだけをETWで拾えるのか
   （`FileIo/Write`イベントの発火条件次第、要実機確認）は技術検証が必要。
3. **Tier1でのaudit実行を許可するか**: Tier1（AppContainerのみ、VM境界
   なし）でauditモードを実行すると、広く読み取りアクセスを許可した状態で
   ホストと同一セッション上で動くことになり、実質的に無隔離に近い。
   `srm audit`をTier2限定にする（Tier1ポリシーには警告だけでなく実行を
   拒否する）べきかは製品判断が必要。
4. **`FwpmNetEventSubscribe4`のシステム要件**: `FWPM_ENGINE_COLLECT_NET_
   EVENTS`の有効化がグループポリシーやWindowsエディションに依存しないか、
   実機での確認が必要（既存`WfpManager`のUserSid条件で
   `FWP_E_TYPE_MISMATCH`を実機で踏んだ前例がある通り、ドキュメントだけで
   断定できない領域）。
