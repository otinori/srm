## ADDED Requirements

### Requirement: 制限付き専用アカウントはLow Integrity Levelによる書き込みバリアを持つ
本機能で対象アプリを起動する専用アカウントのトークンは、Low Integrity
Level（`SetTokenInformation(TokenIntegrityLevel)`でwell-known SID
`S-1-16-4096`へ引き下げたもの）でなければならない（MUST）。整合性レベルを
引き下げていない通常（Medium）のログオントークンでプロセスを起動しては
ならない（SHALL NOT、Windows Mandatory Integrity ControlによるMedium IL
以上への書き込み拒否が機能しなくなるため）。当初検討した
`CreateRestrictedToken`（`BUILTIN\Users`・`Authenticated Users`・
`Everyone`の無効化）は、実機検証で標準的なプロセス起動APIすべてと
相性が悪く機能しなかったため採用しない（詳細はDC-023参照）。

#### Scenario: Low ILプロセスはラベルなし（既定Medium IL）パスへの書き込みを拒否される
- **WHEN** 本機能で起動されたプロセスが、Low Integrity Levelのマンダトリ
  ラベルを付与されていない任意のパスへの書き込みを試みる
- **THEN** システムはWindows Mandatory Integrity Controlにより、DACLの
  許可内容に関わらず当該書き込みを拒否する

#### Scenario: 通常（Medium）の整合性レベルのトークンでは起動されない
- **WHEN** 本機能で対象アプリのプロセスを起動する
- **THEN** システムはLow Integrity Levelへ引き下げたトークンを使用し、
  アカウントの素のMedium整合性レベルのままプロセス起動に使わない

### Requirement: filesystem.allow_pathsフォルダにはLow Integrity Levelのマンダトリラベルを付与する
`filesystem.allow_paths`で指定された各フォルダには、Low Integrity Level
のマンダトリラベルACE（`SYSTEM_MANDATORY_LABEL_NO_WRITE_UP`）をSACLへ
付与しなければならない（MUST）。ラベルを付与しないフォルダは既定でMedium
整合性レベルとして扱われ、Low ILプロセスからの書き込みを拒否されてしまう
ため。

#### Scenario: Low ILラベル付きallow_pathsへの書き込みは成功する
- **WHEN** 本機能で起動されたLow ILプロセスが、Low Integrity Levelの
  マンダトリラベルを付与済みの`allow_paths`フォルダへ書き込みを試みる
- **THEN** 書き込みは成功する

### Requirement: 祖先ディレクトリのTraverse権限は全ポリシー共有のローカルグループに付与する
制限付き専用アカウントが`filesystem.allow_paths`へ到達するための祖先
ディレクトリのTraverse/ReadAttributes権限は、ポリシー個別のアカウントSIDでは
なく、本機能を使う全ポリシーが所属する共有ローカルグループのSIDに対して
付与しなければならない（MUST）。これにより、新しいポリシー名を使うたびに
祖先ディレクトリへのNTFS ACL初回伝播コスト（DC-016で確認済みの
100〜180秒）が再発することを防ぐ。

#### Scenario: 2つ目以降のポリシーは祖先ディレクトリへの書き込みをスキップする
- **WHEN** 既に共有ローカルグループへTraverse権限が付与済みの祖先
  ディレクトリ配下に、新しいポリシー名の`allow_paths`を追加する
- **THEN** システムは当該祖先ディレクトリへのACL書き込みをスキップし、
  DC-016で観測されたような数分単位の遅延を発生させない

### Requirement: allow_pathsへの実際のアクセス許可はポリシーごとに個別のアカウントに付与する
`filesystem.allow_paths`に対する実際の読み取り/書き込み許可
（GENERIC_READ/GENERIC_ALL相当）は、共有ローカルグループではなく、
ポリシー名から決定的に導出された、そのポリシー専用のアカウントのSIDに
対してのみ付与しなければならない（MUST）。これにより、あるポリシーの
`allow_paths`が同機能を使う他のポリシーのプロセスから読み取り可能に
なってはならない（SHALL NOT）。

#### Scenario: 別ポリシーのallow_pathsにはアクセスできない
- **WHEN** ポリシーAの専用アカウントで起動されたプロセスが、ポリシーBの
  `allow_paths`にのみ許可が付与されているパスへアクセスを試みる
- **THEN** システムは当該アクセスを拒否する（ポリシーAのアカウントには
  ポリシーBのパスへの明示的なACEが存在しないため）

#### Scenario: 同一ポリシーの再実行は同じアカウントを再利用する
- **WHEN** 同じポリシー名で`srm run`が複数回実行される
- **THEN** システムはポリシー名から決定的に導出される同一のアカウントを
  再利用し、実行のたびに新規アカウントを作成しない
