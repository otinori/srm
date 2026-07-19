## Requirements

### Requirement: Tier2ゲスト内ネットワーク遮断はネスト先アプリのAppContainerラップ有無に依存しない
Tier2の`network.allow_hosts`強制は、ゲスト内でネストされた対象アプリが
AppContainerでラップされているかどうかに関わらず適用されなければならない
（MUST）。AppContainerラップを無効化するポリシー（本changeで追加する
opt-outフィールド）が指定された場合、システムはAppContainerパッケージSID
条件（`FWPM_CONDITION_ALE_PACKAGE_ID`）の代わりに、非AppContainer向けの
識別子（ユーザーSIDまたは実行ファイルパス）を条件としたWFPフィルターを
登録しなければならない（MUST）。

#### Scenario: AppContainerラップありのTier2アプリは既存動作のまま
- **WHEN** `tier2.app_container`が省略または`true`のTier2ポリシーで
  `srm run`が実行される
- **THEN** システムは既存のAppContainerパッケージSID条件でWFPフィルターを
  登録し、動作は本change以前と変わらない

#### Scenario: AppContainerラップなしのTier2アプリでもallow_hosts以外への通信が拒否される
- **WHEN** `tier2.app_container: false`のTier2ポリシーで`srm run`が実行され、
  対象アプリが`network.allow_hosts`に含まれないホストへの接続を試みる
- **THEN** システムは非AppContainer向け識別子条件のWFPブロックルールにより
  当該接続を拒否する

#### Scenario: AppContainerラップなしのTier2アプリはallow_hostsへの通信が許可される
- **WHEN** `tier2.app_container: false`のTier2ポリシーで`srm run`が実行され、
  対象アプリが`network.allow_hosts`に含まれるホストへの接続を試みる
- **THEN** システムは当該接続を許可する

### Requirement: 非AppContainerのネットワーク遮断は対象アプリが生成する子プロセスにも及ぶ
AppContainerラップを無効化したTier2アプリが子プロセスを生成した場合、
その子プロセスからのアウトバウンド接続にも同じ`network.allow_hosts`強制が
適用されなければならない（MUST）。ユーザーSID条件を用いる場合は子プロセスの
トークン継承により自動的にカバーされ、実行ファイルパス条件のみで運用する
場合は単一プロセス（子プロセスを生成しないアプリ）にスコープを限定しなければ
ならない（MUST、design.mdのALE_APP_IDフォールバックの制約）。

#### Scenario: ユーザーSID条件は子プロセスにも自動的に適用される
- **WHEN** `tier2.app_container: false`かつユーザーSID条件を使う設定で
  対象アプリが子プロセスを生成し、その子プロセスが
  `network.allow_hosts`に含まれないホストへ接続を試みる
- **THEN** 子プロセスは親と同じ制限付きアカウントのトークンを継承しており、
  システムは当該接続を拒否する

#### Scenario: 実行ファイルパス条件のみのフォールバックは単一プロセスアプリに限定される
- **WHEN** ユーザーSID条件の識別子（制限付き専用アカウント）が利用できず
  実行ファイルパス条件のみで運用する設定が選択される
- **THEN** システムは当該ポリシーを子プロセスを生成しない単一プロセスアプリ
  向けとして扱い、子プロセスが生成された場合の通信制御は保証しない

### Requirement: WfpManagerはAppContainerパッケージSID・ユーザーSID・実行ファイルパスの3種類の識別子を条件として扱える
`WfpManager.Install`は、AppContainerパッケージSID・ユーザーSID・実行ファイル
パスのいずれかを識別子として受け取り、対応するWFP条件フィールド
（それぞれ`FWPM_CONDITION_ALE_PACKAGE_ID`・`FWPM_CONDITION_ALE_USER_ID`・
`FWPM_CONDITION_ALE_APP_ID`）でallow/blockルールを構築できなければならない
（MUST）。`RemoveForApp`によるクリーンアップは識別子の種類に関わらず
既存の`appName`由来の決定的な`subLayerKey`のみで動作しなければならない
（MUST、識別子の種類を記憶・引き継ぐ必要をなくすため）。

#### Scenario: 識別子の種類によらずsrm stopでフィルターが除去される
- **WHEN** ユーザーSID条件またはAppパス条件で登録されたWFPフィルターが
  存在する状態で`srm stop`が実行される
- **THEN** システムは登録時の識別子の種類を再取得することなく、`appName`
  から導出した`subLayerKey`に一致する全フィルターを削除する
