## ADDED Requirements

### Requirement: request/result JSONポーリングの共通プリミティブを提供する
`SandboxControlChannel`は、`control/`配下の`{kind}-request.json`/`{kind}-result.json`という命名規則で、任意の`kind`に対するrequest/result JSONポーリングを行える汎用プリミティブを提供しなければならない（MUST）。新規に追加するcapability（ファイル転送・プロセスモニタ等）は、この共通プリミティブの上に構築しなければならない（MUST）。scenario/focus/diag/mcpという既存4パターンは、実機検証済みの安定動作を壊すリスクを避けるため、この共通プリミティブへの移行を必須としない（実装判断として現状維持を選択した。移行自体は将来いつでも行える）。

#### Scenario: 新しいkindのrequest/resultペアを追加コードなしで利用できる
- **WHEN** 新しいcapabilityが独自の`kind`文字列（例: `"file-transfer"`）を指定して汎用プリミティブを呼び出す
- **THEN** `SandboxControlChannel`は`control/file-transfer-request.json`/`control/file-transfer-result.json`という命名規則でファイルの読み書きを行い、既存のscenario/focus/diag向けの専用コードを変更せずに新しいkindを扱える

### Requirement: request/resultファイルは蓄積しない
すべてのkindについて、request/resultファイルはそれぞれ固定パス1つを都度上書きする方式で実装しなければならず（MUST）、呼び出し回数に応じて新規ファイルが追加され蓄積する実装をしてはならない（SHALL NOT、DC-017が却下した`alt-percommand-file-queue`と同じ問題の再導入を避けるため）。

#### Scenario: 同一kindへの複数回の要求がファイルを蓄積させない
- **WHEN** 同一のTier2セッション内で同じkindのrequestが複数回発行される
- **THEN** `control/`配下の該当する`{kind}-request.json`/`{kind}-result.json`はそれぞれ1ファイルのまま上書きされ続け、呼び出し回数に応じてファイル数が増加しない

### Requirement: requestIdの不一致時は結果を無視して待機を継続する
ホスト側の`WaitForResult`は、自分が発行した`RequestId`と一致する結果が書き込まれるまでポーリングを継続しなければならず（MUST）、古い`RequestId`を持つ結果（直前の要求の使い回し）は無視しなければならない（MUST）。

#### Scenario: 新しい要求の直後に古い結果が書き込まれても無視される
- **WHEN** ホストが新しい`RequestId`で`{kind}-request.json`を上書きした直後に、ゲストが処理中だった古い`RequestId`の結果を`{kind}-result.json`へ書き込む
- **THEN** ホストは`RequestId`が一致する結果が書き込まれるまでポーリングを継続し、不一致の結果は無視する

### Requirement: ゲスト側のポーリングループは複数kindを同時に監視できる
ゲスト内`--nested`メインループ（`BlockUntilStop`）は、登録された複数のkindのrequestハンドラを単一のポーリングループ内で同時に監視できなければならない（MUST）。汎用プリミティブ経由で追加する新しいkindは、`kind`文字列とハンドラをハンドラ辞書へ登録するだけでよく、ポーリングループ本体の実装を個別に拡張してはならない（SHALL NOT）。

#### Scenario: 複数kindのrequestが同一ポーリング周期内で検出される
- **WHEN** ホストが同じポーリング周期内に`focus-request.json`と`file-transfer-request.json`の両方を書き込む
- **THEN** ゲスト側の単一のポーリングループが両方の変化を検出し、それぞれ対応するハンドラを呼び出す
