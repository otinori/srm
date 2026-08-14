## ADDED Requirements

### Requirement: `srm audit`はブロックせずファイルシステム/ネットワークへのアクセス試行を記録する
`srm audit <policy>`は、対象ポリシーの`filesystem.allow_paths`/
`network.allow_hosts`によるアクセス拒否を行わずに対象アプリ（子プロセス
含む）を起動し、実際に発生したファイルアクセス・ネットワーク接続の試行を
記録しなければならない（MUST）。記録された各アクセスには、ポリシーの
`allow_paths`/`allow_hosts`と照合した`allowed`/`would_block`の判定を
付与しなければならない（MUST）。

#### Scenario: allow_paths外への読み取りアクセスは拒否されずに記録される
- **WHEN** `srm audit`で起動したアプリが`filesystem.allow_paths`に
  含まれないパスを読み取ろうとする
- **THEN** システムはそのアクセスを拒否せず許可した上で、`would_block`と
  判定してログに記録する

#### Scenario: allow_hosts外へのネットワーク接続は拒否されずに記録される
- **WHEN** `srm audit`で起動したアプリが`network.allow_hosts`に
  含まれないホストへ接続しようとする
- **THEN** システムはその接続を拒否せず許可した上で、`would_block`と
  判定してログに記録する

#### Scenario: allow_paths/allow_hosts内へのアクセスも記録される
- **WHEN** `srm audit`で起動したアプリが`filesystem.allow_paths`または
  `network.allow_hosts`に含まれるリソースへアクセスする
- **THEN** システムはそのアクセスを`allowed`と判定してログに記録する

### Requirement: auditモードは既存の`srm run`の拒否ロジックに影響しない
`srm audit`の追加は、既存`srm run`（Tier1/Tier2いずれも）における
`filesystem.allow_paths`/`network.allow_hosts`の拒否動作を変更しては
ならない（MUST NOT）。

#### Scenario: srm runは引き続き未許可リソースを拒否する
- **WHEN** `srm audit`が実装された後に`srm run`で同じポリシーを実行し、
  対象アプリが`allow_paths`/`allow_hosts`に含まれないリソースへ
  アクセスしようとする
- **THEN** システムは本change以前と同様にそのアクセスを拒否する

### Requirement: auditモードは隔離を弱めることをユーザーに明示しなければならない
`srm audit`は、ファイルシステムへの広い読み取りアクセスを許可し
ネットワークを無制限に許可した状態で実行するため、実行時にこの実行モードが
サンドボックスとして機能しない旨を警告として表示しなければならない
（MUST）。

#### Scenario: srm audit実行時に隔離が弱いことを警告する
- **WHEN** ユーザーが`srm audit <policy>`を実行する
- **THEN** システムは、auditモードが未許可のファイル読み取り・ネットワーク
  接続を拒否せず許可すること、および信頼できないアプリの観測にはTier2の
  併用を推奨する旨を、実行前に警告として表示する
