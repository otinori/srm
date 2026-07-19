## ADDED Requirements

### Requirement: 実行中のTier2ゲストとオンデマンドで単一ファイルを転送できる
`tier2-file-transfer`は、Tier2ゲストが実行中の任意のタイミングで、ホスト⇄ゲスト間の単一ファイル転送をオンデマンドで行えなければならない（MUST）。転送方向はホスト→ゲスト・ゲスト→ホストの両方をサポートしなければならない（MUST）。

#### Scenario: ホストからゲストへファイルを転送する
- **WHEN** ホストが`Direction: HostToGuest`を指定してファイル転送を要求する
- **THEN** 指定したファイルがゲスト内の指定パスに配置され、完了がホストへ通知される

#### Scenario: ゲストからホストへファイルを転送する
- **WHEN** ホストが`Direction: GuestToHost`を指定してファイル転送を要求する
- **THEN** ゲスト内の指定ファイルがホスト側で参照可能になり、完了がホストへ通知される

### Requirement: 転送実体はInput/Outboxマップフォルダ経由で運び、control配下にはメタデータのみを置く
ファイル転送の実データは、既存の`Input`（ホスト→ゲスト）・`Outbox`（ゲスト→ホスト）マップフォルダ配下の`transfer/<RequestId>/`サブフォルダに置かれなければならず（MUST）、`control/`配下の`file-transfer-request.json`/`file-transfer-result.json`には転送元/転送先パス・方向・完了フラグ等のメタデータのみを含めなければならない（MUST）。新規のMappedFolderマウントを追加してはならない（SHALL NOT）。

#### Scenario: 転送要求のメタデータと実データが分離される
- **WHEN** ファイル転送が要求され完了する
- **THEN** `control/file-transfer-result.json`にはファイルの実体を含まず、実体は`input/transfer/<RequestId>/`または`outbox/transfer/<RequestId>/`に置かれる

### Requirement: 転送パスはfilesystemポリシーの許可範囲内に制限される
転送元/転送先パスは、対象ポリシーの`filesystem.allow_paths`で許可された範囲内でなければならず（MUST）、`..`を含む相対パスや許可範囲外の絶対パスを指定した要求は拒否されなければならない（MUST）。

#### Scenario: 許可範囲外のパスを指定すると拒否される
- **WHEN** `allow_paths`に含まれないパスを転送先として指定した要求が発行される
- **THEN** システムは転送を実行せず、許可範囲外である旨のエラーを結果として返す

#### Scenario: パストラバーサルを含むパスを指定すると拒否される
- **WHEN** `..`を含む相対パスを転送元または転送先として指定した要求が発行される
- **THEN** システムは転送を実行せず、不正なパスである旨のエラーを結果として返す

### Requirement: ゲスト→ホスト方向の転送結果はDC-013の検疫モデルに従う
ゲストからホストへ転送されたファイルは、転送完了時点では`outbox/transfer/<RequestId>/`に配置されるのみで、既存の検疫パイプライン（DC-013、`srm stop`時のoutboxスキャン）へ新規の即時反映処理を追加してはならない（SHALL NOT）。`srm evidence`から参照可能になるのは、既存のDC-013のフロー通り`srm stop`後の検疫完了後であり、ユーザーの作業ディレクトリへの自動反映は行われてはならず（SHALL NOT）、DC-013と同じ人間による明示的な昇格操作（`srm evidence promote`）を経なければならない（MUST）。

#### Scenario: 転送直後は作業ディレクトリへ自動反映されない
- **WHEN** ゲストからホストへのファイル転送が完了する
- **THEN** 転送されたファイルは`outbox/transfer/<RequestId>/`に置かれるのみで、ユーザーの作業ディレクトリには自動的にコピーされない

#### Scenario: srm evidenceから参照できるのはsrm stop後
- **WHEN** ゲストからホストへのファイル転送が完了した直後（`srm stop`実行前）に`srm evidence list`を実行する
- **THEN** 転送されたファイルはまだ検疫ストアに現れない。`srm stop`を実行し既存のDC-013検疫フローが完了して初めて`srm evidence list`/`show`から参照できるようになる
