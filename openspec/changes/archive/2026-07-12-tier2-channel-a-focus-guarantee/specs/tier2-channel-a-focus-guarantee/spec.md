## ADDED Requirements

### Requirement: Tier2チャネルAはゲスト内フォーカスを送信前に保証する
Tier2の`send_key`および`send_mouse`ツールは、ゲスト内`--nested`プロセスへのフォーカス要求でゲスト内対象ウィンドウが実際に入力フォーカスを持ったことを確認するまで`SendInput`を発行してはならない（SHALL NOT）。ゲスト側がフォーカス設定に失敗した場合、または確認がタイムアウトした場合、システムは入力を発行せずエラーを返さなければならない（MUST、fail-closed）。

#### Scenario: フォーカス確認成功後にsend_keyが入力を発行する
- **WHEN** Tier2で起動済みのアプリに対して`send_key`が呼ばれる
- **THEN** システムはゲスト内`--nested`へフォーカス要求を発行し、ゲストが対象
  ウィンドウへのフォーカス設定に成功したことを確認したうえで、ホストから
  `WindowsSandboxClient.exe`ウィンドウへ`SendInput`する

#### Scenario: ゲスト側フォーカス設定失敗時は入力を発行しない
- **WHEN** ゲスト内`--nested`が対象アプリのウィンドウを解決できない、または
  フォーカス設定に失敗する
- **THEN** システムは`SendInput`を発行せず、理由を含むエラーを呼び出し元に返す

#### Scenario: フォーカス確認がタイムアウトした場合は入力を発行しない
- **WHEN** ゲスト内`--nested`からのフォーカス確認結果が既定のタイムアウト
  （2秒）以内に得られない
- **THEN** システムは`SendInput`を発行せず、タイムアウトを示すエラーを
  呼び出し元に返す

### Requirement: フォーカス要求プロトコルは蓄積しないファイルペアで実装する
ホスト↔ゲスト間のフォーカス要求/結果のやり取りは、`control/`配下の固定パス2つ（`focus-request.json`/`focus-result.json`）を都度上書きする方式で実装しなければならず（MUST）、呼び出し回数に応じて新規ファイルが追加され蓄積する実装をしてはならない（SHALL NOT、DC-017が却下した`alt-percommand-file-queue`と同じ問題の再導入を避けるため）。

#### Scenario: 複数回のフォーカス要求がファイルを蓄積させない
- **WHEN** 同一のTier2セッション内で`send_key`/`send_mouse`が複数回呼ばれる
- **THEN** `control/`配下の`focus-request.json`/`focus-result.json`はそれぞれ
  1ファイルのまま上書きされ続け、呼び出し回数に応じてファイル数が増加しない

#### Scenario: requestIdの不一致時は結果を無視して待機を継続する
- **WHEN** ホストが新しい`requestId`で`focus-request.json`を上書きした直後に、
  ゲストが処理中の古い`requestId`の結果を`focus-result.json`へ書き込む
- **THEN** ホストは`requestId`が一致する結果が書き込まれるまでポーリングを
  継続し、不一致の結果は無視する

### Requirement: フォーカス保証はTier1チャネルAおよびチャネルBの動作を変更しない
本機能はTier2チャネルAにのみ適用されなければならず（MUST）、Tier1チャネルA（`send_key`/`send_mouse`/`screenshot`）およびチャネルB（`run_scenario`/`get_scenario_result`）の既存の動作・パフォーマンスに影響を与えてはならない（SHALL NOT）。

#### Scenario: Tier1のsend_keyは追加の待機なしに動作する
- **WHEN** Tier1で起動済みのアプリに対して`send_key`が呼ばれる
- **THEN** システムはフォーカス要求プロトコルを経由せず、従来通り直接
  `WindowGuard`の検証後に`SendInput`する

#### Scenario: run_scenarioの実行時間は変化しない
- **WHEN** Tier2アプリに対して`run_scenario`が呼ばれる
- **THEN** シナリオ実行はゲスト内`--nested`が同一セッション内で直接
  `SendInput`する既存の経路をそのまま使い、フォーカス要求プロトコルを
  経由しない
