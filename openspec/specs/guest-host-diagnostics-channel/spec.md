## Requirements

### Requirement: srm diagはTier1/Tier2どちらの実行中アプリもサンプリングできる
`srm diag <app>`は、`RunningAppRegistry`に登録済みの実行中アプリに対して、CPU時間内訳（ユーザー時間比率・カーネル時間比率）・秒あたりページフォールト数・スレッド数・ハンドル数を返さなければならない（MUST）。対象アプリのTierに関わらず同じCLIコマンド・同じ結果形式で使えなければならない（MUST）。

#### Scenario: Tier1アプリのサンプリングに成功する
- **WHEN** Tier1で起動済みのアプリに対して`srm diag <app>`が呼ばれる
- **THEN** システムはホストプロセスから対象PIDのCPU時間・ページフォールト数を直接サンプリングし、結果を返す

#### Scenario: Tier2アプリのサンプリングに成功する
- **WHEN** Tier2で起動済みのアプリに対して`srm diag <app>`が呼ばれる
- **THEN** システムはゲスト内`--nested`プロセスへ診断要求を発行し、ゲストが対象PIDをサンプリングした結果を受け取って返す

#### Scenario: 登録されていないアプリ名を指定するとエラーになる
- **WHEN** `RunningAppRegistry`に登録されていないアプリ名で`srm diag`が呼ばれる
- **THEN** システムはサンプリングを行わず、実行中のアプリが見つからない旨のエラーを返す

### Requirement: Tier2の診断要求プロトコルは蓄積しないファイルペアで実装する
ホスト↔ゲスト間の診断要求/結果のやり取りは、`control/`配下の固定パス2つ（`diag-request.json`/`diag-result.json`）を都度上書きする方式で実装しなければならず（MUST）、呼び出し回数に応じて新規ファイルが追加され蓄積する実装をしてはならない（SHALL NOT、DC-017が却下した`alt-percommand-file-queue`と同じ問題の再導入を避けるため）。

#### Scenario: 複数回の診断要求がファイルを蓄積させない
- **WHEN** 同一のTier2セッション内で`srm diag`が複数回呼ばれる
- **THEN** `control/`配下の`diag-request.json`/`diag-result.json`はそれぞれ1ファイルのまま上書きされ続け、呼び出し回数に応じてファイル数が増加しない

#### Scenario: requestIdの不一致時は結果を無視して待機を継続する
- **WHEN** ホストが新しい`requestId`で`diag-request.json`を上書きした直後に、ゲストが処理中の古い`requestId`の結果を`diag-result.json`へ書き込む
- **THEN** ホストは`requestId`が一致する結果が書き込まれるまでポーリングを継続し、不一致の結果は無視する

### Requirement: サンプリング対象プロセスが消失した場合はエラーとして返す
対象PIDのプロセスが解決できない、またはサンプリング中に終了した場合、システムは例外を発生させずに失敗を示す結果（理由付き）を返さなければならない（MUST）。

#### Scenario: 存在しないPIDを指定した場合
- **WHEN** 対象PIDのプロセスが存在しない状態で診断サンプリングが実行される
- **THEN** システムは`Success=false`と理由を含む結果を返す

#### Scenario: サンプリング中に対象プロセスが終了した場合
- **WHEN** CPU時間・ページフォールト数の2回目のサンプリング取得前に対象プロセスが終了する
- **THEN** システムは例外をスローせず、`Success=false`と理由を含む結果を返す
