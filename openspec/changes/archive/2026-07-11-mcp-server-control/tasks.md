## 1. Srm.Mcp プロジェクト基盤

- [x] 1.1 `Srm.sln`に`Srm.Mcp`プロジェクトを追加し、`Srm.Runtime`/`Srm.PolicyEngine`/
      `Srm.PolicyIntegrity`を参照させる（`net8.0-windows`）
- [x] 1.2 MCP C# SDKを導入し、stdio transportでサーバーを起動する`Program.cs`を実装する
      （公式`ModelContextProtocol` 1.4.1。stdout保全のため`builder.Logging.AddConsole(o
      => o.LogToStandardErrorThreshold = LogLevel.Trace)`が必須と判明・対応済み。
      Linux上のスタンドアロンprobeプロジェクトで`initialize`/`tools/call`のJSON-RPC
      往復を実機的に確認済み）
- [x] 1.3 ツール定義（名前・入力スキーマ・説明）の登録機構を実装する
      （`[McpServerToolType]`+`[McpServerTool]`+`WithToolsFromAssembly()`。
      インスタンスツールクラスへのコンストラクタDI注入で`PoliciesDir`を渡す方式を
      probeで検証済み）

## 2. CLI相当のMCPツール化（`RuntimeTools`/`EvidenceTools`）

- [x] 2.1 `srm_run`: `RunOperation`（Phase1でCLIから抽出済み）を直接呼び出す
- [x] 2.2 `srm_stop`/`srm_list`/`srm_logs`/`srm_validate`をそれぞれ`StopOperation`/
      `RunningAppRegistry`/`StructuredLogger`/`ValidateOperation`の直接呼び出しで
      ツール化する
- [x] 2.3 `srm_evidence_list`/`srm_evidence_show`/`srm_evidence_promote`/
      `srm_evidence_reject`を`EvidenceQuarantineStore`直接呼び出しでツール化する
- [x] 2.4 ユニットテストを追加する（`RuntimeToolsTests`: 検証成功・
      `SrmOperationException`→`McpException`変換・整合性エラー・list/logsの空応答を
      確認。`dotnet test`でLinux上で実行・パス確認済み。`EvidenceTools`は
      `EvidenceCommand`同様の薄いラッパーのため、下層の`EvidenceQuarantineStore`側の
      既存テストでカバーし個別テストは追加しない）

## 3. WindowGuard（Tier1・チャネルA・チャネルBゲスト内実行の共通コンポーネント）

- [x] 3.1 対象ウィンドウの所有PIDが、起動時記録済みプロセス集合（Tier1: 名前付き
      Job Object配下PID、`JobObjectManager.TryGetProcessIds`で別プロセスから
      再取得）に属するかを検証する処理を実装する（Tier2の`SandboxLauncher`起動
      プロセス版はPhase4で追加）
- [x] 3.2 所有PIDの実行ファイルパスを`QueryFullProcessImageName`で取得し、期待する
      バイナリと一致するか検証する処理を実装する（`ExpectedExecutablePath`未指定時は
      スキップ。Tier1はJob Object所属で十分と判断し必須にしない。Tier2では
      Phase4でWindowsSandbox.exe側の検証として必須にする）
- [x] 3.3 `GetClassName`でウィンドウクラス名を検証する処理を実装する（同上、
      指定時のみ）
- [x] 3.4 マウス操作時、変換後のホスト座標が送信直前に取得したクライアント領域内に
      収まっているかを検証する処理を実装する
- [x] 3.5 `SetForegroundWindow`後に実際のフォアグラウンドウィンドウが対象と一致するか
      確認する処理を実装する
- [x] 3.6 いずれかの検証に失敗した場合、入力を発行せず`StructuredLogger`に
      `guard_rejected`イベント（理由付き）を記録する
- [x] 3.7 ユニットテストを追加する（`WindowGuardTests`: notepad.exeを実際に起動し
      実Win32 API呼び出しで検証。`WindowsOnlyFactAttribute`でLinux上はSkip、
      CIのwindows-latestランナーでは実行される。`JobObjectManagerTests`も同様に
      named job objectの作成→別ハンドルでの再オープン→PID一覧取得の往復を追加）

## 4. チャネルA: 対話的ライブ制御

- [x] 4.1 `Tier1WindowResolver`: `RunningAppRegistry`が記録した`job_name`から
      `JobObjectManager.TryGetProcessIds`でPID一覧を取得し、`EnumWindows`+
      `GetWindowThreadProcessId`で可視ウィンドウを列挙する
- [x] 4.2 `Tier2SandboxWindowResolver`: `RunningApp.Pid`（`SandboxLauncher`の
      ホストプロセス）と`CreateToolhelp32Snapshot`で辿ったその子孫プロセス全体を
      候補PID集合にする（**未検証**: 実際にレンダリングウィンドウを持つのが
      `WindowsSandbox.exe`自身か子プロセスかは実機PoC待ち、DC-017 review_trigger）。
      `WindowGuard`の実行ファイルディレクトリ/ファイル名部分一致チェックで
      `%SystemRoot%\System32`配下の`WindowsSandbox*`を要求する
- [x] 4.3 `send_key`/`send_mouse`ツールを`SendInput` P/Invokeで実装し、送信直前に
      `WindowGuard`を通す（`send_key`はUnicode文字送信のみ対応。Enter等の特殊キー
      送信は未実装、将来の拡張課題として残す）。Tier1/Tier2両対応
- [x] 4.4 `screenshot`ツールを`PrintWindow`+`System.Drawing.Common`で実装し、
      `WindowGuard`を通したうえでMCPの`ImageContentBlock`としてPNG画像を返す
      （Tier2の場合はゲストデスクトップ全体が写る。個別アプリのクロップは無し）
- [x] 4.5 対象ウィンドウが存在しない/`WindowGuard`に拒否された場合のエラー
      ハンドリングを実装する（`McpException`で理由を呼び出し元に伝える）。複数
      ウィンドウがある場合は現状「最も手前（Zオーダー先頭）」を自動選択する簡易
      実装とし、特定ウィンドウを狙い撃つ機能は無い（将来の拡張課題）

## 5. チャネルB: オートパイロット（Tier2専用）

- [x] 5.1 `SandboxControlChannel`に`WriteScenario`（host）・`ReadScenario`/
      `WriteScenarioResult`（guest）・`TryReadScenarioResult`/
      `WaitForScenarioResult`（host）を追加する
- [x] 5.2 `RunCommand`の`--nested`メインループを変更する。**実装時に判明**:
      `scenario.json`は`policy.yaml`と違いVM起動前に配置できない（`srm_run`が
      `ready.signal`受信までブロックして戻るため、MCPクライアントが`run_scenario`を
      呼べるのはその後）。そのため`SandboxControlChannel.BlockUntilStop`に
      「`stop.signal`を待つ間ずっと`scenario.json`の出現を監視するコールバック」
      オーバーロードを追加し、起動直後の単発チェックではなく継続監視にした
      （`scenario.json`が最後まで現れない場合は従来の挙動と完全に等価であることを
      確認した）
- [x] 5.3 `ScenarioExecutor`（`Srm.Runtime.Sandbox`、ゲスト内から呼ばれる）を実装した。
      各ステップは4章の`WindowGuard`とチャネルAのTier1と同じ`Tier1WindowResolver`/
      `InputSender`/`WindowCapture`をそのまま再利用する（新規のウィンドウ解決
      トリックは不要）
- [x] 5.4 ステップごとの証跡（スクリーンショット）を`SandboxConfigGenerator.
      GuestOutboxDir`に書く。既存のevidence検疫パイプライン（`EvidenceQuarantineStore`、
      `srm stop`時の`QuarantineOutbox`）にそのまま流れる（配線変更不要）
- [x] 5.5 `run_scenario`（host: `scenario.json`書き込み、非同期）・
      `get_scenario_result`（`scenario-result.json`の即時確認/`waitSeconds`指定での
      ポーリング待機）ツールを実装した
- [x] 5.6 ユニットテストを追加した。`SandboxControlChannelTests`に scenario系の
      読み書き・往復・`BlockUntilStop`コールバックのテストを追加（Linux上で実行・
      パス確認済み）。`ScenarioExecutorTests`はnotepad.exeを実際に起動する実Win32
      テストのため`WindowsOnlyFactAttribute`でLinux上はSkip、CIのwindows-latest
      ランナーでは実行される

## 6. 監査ログ統合

- [x] 6.1 `StructuredLogger`に入力注入（`send_key`/`send_mouse`）・画面キャプチャ
      （`screenshot`）・シナリオ実行（`run_scenario`、ゲスト内の`シナリオ実行開始/
      完了`）・`guard_rejected`（ガード拒否時、理由付き）のイベントを追加した
      （`InteractiveInputTools`/`AutopilotTools`/`RunCommand.RunScenario`）
- [x] 6.2 MCPツール呼び出しのうち、状態変更を伴う操作（`send_key`/`send_mouse`/
      `screenshot`/`run_scenario`/`srm_evidence_promote`/`srm_evidence_reject`）は
      既存ログパイプラインに統合済み。読み取り専用ツール（`srm_list`/`srm_logs`/
      `srm_evidence_list`/`srm_evidence_show`/`get_scenario_result`）は監査価値が
      低いためログ対象外とした（既存CLIの`list`/`logs`/`evidence list`/`evidence show`
      も同様に無記録）

## 7. ドキュメント整合

- [x] 7.1 READMEに`Srm.Mcp.exe`の説明・配布物構成（`bin\Srm.Mcp.exe`）を追加した。
      `spec-srm-architecture`（PKMPの`Approved`状態の正式スペック）の非ゴール
      「GUIアプリ対応（v0.1はコンソールアプリのみ）」はv0.1時点の記述として現在も
      正確なため変更していない（`notepad-test`によるGUI**起動**はv0.1から可能、
      本changeが追加するのはその先の**操作**という区別は`design.md`の実装上の注意に
      既に明記済み）。承認済み文書への追記が必要と判断した場合は別途レビューを
      依頼すること
- [x] 7.2 `manual/usage.md`にMCPサーバーのセットアップ手順（MCPクライアント側の
      `mcpServers`設定例）・公開ツール一覧・チャネルA/Bの使い方（`scenario.json`の
      ステップスキーマ含む）・実機未検証事項の注記を追記した。`tools/package.ps1`に
      `Srm.Mcp`のpublishステップを追加し、リリースzipに`bin\Srm.Mcp.exe`が
      実際に含まれるようにした

## 8. ビルド・テスト検証（Linux開発環境で可能な範囲）

- [x] 8.1 `dotnet build Srm.sln`が通ることを確認する（Linux開発環境に.NET 8 SDKを
      新規インストールし確認済み。`Srm.PolicyEditor`はWindowsDesktop SDK依存のため
      既知の制約としてLinux上ではビルド対象外）
- [x] 8.2 `dotnet test`で新規ユニットテストがすべてパスすることを確認する
      （Linux上では`WindowsOnlyFactAttribute`によりWin32依存テストはSkip。
      CI（windows-latest、GitHub Actions PR #1）では実際にnotepad.exeを起動する
      `WindowGuardTests`/`JobObjectManagerTests`が実行され、Phase3コミット時点で
      全件パス済みを確認した）

## 9. 実機確認要（Windows実機・まとめて実施）

> **2026-07-11時点の注記**: CI（windows-latest）が`WindowGuard`/`JobObjectManager`の
> Win32メカニクス（同一セッション内でのSendInput/EnumWindows/
> QueryFullProcessImageName等）を実際にWindows上で検証済み（8.2参照）。ただし
> これは合成的なテスト（notepad.exeを直接起動）であり、9.1/9.2が求める
> 「AppContainer/MCPクライアント経由の実際のエンドツーエンド動作」の確認までは
> 至っていない。9.3〜9.6（Tier2固有）はWindows Sandboxを実際に起動できる環境
> （Hyper-V有効なWindows 10/11 Pro・Enterprise）が必要で、開発者が現時点で
> 保有していないため未着手。

- [x] 9.1 MCPクライアント（Claude Code等）から`Srm.Mcp`をstdio経由で起動し、
      `srm_run`等の既存CLI相当ツールが正しく動作する（2026-07-12実機確認。
      検証中に発見した`srm.exe`CLIのパッケージング破壊バグは`records/DC-018.yaml`で
      修正済み。stdout混入の既知課題は`records/DC-017.yaml`review_trigger参照）
- [x] 9.2 Tier1で起動したGUIアプリ（`notepad-test`等）に対し、チャネルAの
      `send_key`/`send_mouse`/`screenshot`が正しく動作する（2026-07-12実機確認。
      screenshot/send_mouseの座標系不一致の既知課題は`records/DC-017.yaml`
      review_trigger参照）
- [x] 9.3 Tier2で`WindowsSandbox.exe`が開くウィンドウのクラス名・所有プロセス構成が
      設計通りであることを確認し、チャネルAの`send_key`/`send_mouse`/`screenshot`が
      RDP経由でゲストに正しく反映される（2026-07-12実機確認。実際にレンダリング
      ウィンドウを持つのは`WindowsSandboxClient.exe`＝`WindowsSandbox.exe`の子
      プロセスで、実装の期待値と一致。ホスト側チャネルAのゲスト内フォーカス保証の
      限界は`records/DC-017.yaml`review_trigger参照）
- [x] 9.4 `WindowGuard`が、対象外のウィンドウ（サンドボックス外の別ウィンドウ、
      別runIdのサンドボックス等）に対して入力発行を正しく拒否することを確認する
      （DC-017 decision 4のスコープ制約が実装上も守られていることの確認）
      （2026-07-12実機確認。管理下notepad-testと管理外notepadを並行させ、
      入力が混入しないことを確認）
- [x] 9.5 Tier2で`--nested`が`scenario.json`を検出し、ゲスト内で各ステップを自律実行
      できる。証跡が`outbox`経由でevidence検疫ストアに正しく反映される
      （2026-07-12実機確認。run_scenario/get_scenario_result/evidence検疫まで
      成功）
- [x] 9.6 `run_scenario`投入後、host↔guest間の通信が一時的に不安定でも投入済み
      シナリオが完走し、`scenario-result.json`が正しく書き戻される
      （2026-07-12実機確認。MCPサーバープロセス終了後も、完全に別の新規
      セッションから結果を取得できることを確認）
- [x] 9.7 SRMが管理していないプロセス・ウィンドウに対して`send_key`等が拒否される
      ことを確認する（decision 5のスコープ制約の確認）（2026-07-12実機確認。
      9.4の検証で同時に確認済み）
