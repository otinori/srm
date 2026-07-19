## 1. 診断モデル・SandboxControlChannel拡張

- [x] 1.1 `DiagRequestModel`（`requestId`・`sampleWindowMs`）・`DiagResultModel`
      （`requestId`・`success`・`reason`・`processId`・`sampleWindowMs`・
      `userTimePercent`・`kernelTimePercent`・`pageFaultsPerSec`・`threadCount`・
      `handleCount`）を`Srm.Runtime.Sandbox.DiagModel`に定義する
- [x] 1.2 `SandboxControlChannel`に`diag-request.json`/`diag-result.json`の
      読み書きメソッド（`WriteDiagRequest`/`TryReadDiagRequest`/
      `WriteDiagResult`/`TryReadDiagResult`/`WaitForDiagResult`）を、
      focus-request/resultと同じ「固定パス上書き・requestId不一致は無視」の
      パターンで追加する
- [x] 1.3 `BlockUntilStop`のオーバーロード連鎖に`onDiagRequestDetected`を追加する
      （`onFocusRequestDetected`と同じ、requestIdの変化を都度検出して毎回
      発火する方式）
- [x] 1.4 ユニットテストを追加する（`SandboxControlChannelTests`に
      diag-request/resultの読み書き・ファイル蓄積が起きないこと・requestId
      不一致時の待機継続・`BlockUntilStop`での複数回発火を確認するテストを
      追加。9件）

## 2. 診断サンプラー本体（DiagCollector）

- [x] 2.1 対象PIDのCPU時間（ユーザー/カーネル）・ページフォールト数を、指定した
      経過時間の前後でサンプリングし差分から秒あたりレート・比率を計算する
      `DiagCollector`（`Srm.Runtime.Diagnostics`）を実装する。ページフォールト数
      は`GetProcessMemoryInfo`（psapi.dll）へのP/Invokeで取得し、新規NuGet
      パッケージには依存しない
- [x] 2.2 対象PIDが存在しない、またはサンプリング中に終了した場合、例外を
      投げずに`Success=false`と理由を返す
- [x] 2.3 ユニットテストを追加する（`DiagCollectorTests`、notepad.exeを実際に
      起動する実Win32テストのため`WindowsOnlyFactAttribute`でLinux上はSkip。
      実行中プロセスへの正常系サンプリング・存在しないPIDでの失敗系の2件）

## 3. Tier2ゲスト内ハンドラ（`--nested`）

- [x] 3.1 `RunCommand.RunNested`のメインループに、診断要求検出時のハンドラ
      （`HandleDiagRequest`）を追加する。起動時に記録済みの対象アプリPIDを
      そのまま`DiagCollector`へ渡す（フォーカス要求と異なりウィンドウ解決は
      不要）
- [x] 3.2 サンプリング結果を`diag-result.json`へ書き、成功/失敗をログに記録する

## 4. ホスト側の入口（Tier1/Tier2振り分け）とCLI

- [x] 4.1 `DiagOperation`（`Srm.Runtime.Operations`）を実装する。
      `RunningAppRegistry`からアプリを解決し、Tier1は`DiagCollector`を直接
      呼び出し、Tier2は`SandboxControlChannel`経由でゲストへ委譲して
      `WaitForDiagResult`する
- [x] 4.2 新規CLIサブコマンド`srm diag <app> [--duration-ms]`
      （`Srm.Cli.Commands.DiagCommand`）を追加し、`Program.cs`に登録する。
      MCPツールとしては公開しない
- [x] 4.3 登録されていないアプリ名を指定した場合にエラーメッセージと
      非ゼロ終了コードを返すことを確認する

## 5. ビルド・テスト検証

- [x] 5.1 `dotnet build Srm.sln`が通ることを確認する
- [x] 5.2 `dotnet test`で新規ユニットテスト11件を含む全テストがパスすることを
      確認する（全133テスト成功）

## 6. 実機確認

- [x] 6.1 Tier1（`notepad-test`）を`srm run`で起動し、`srm diag notepad-test
      --duration-ms 500`が対象PID・CPU内訳・ページフォールト率・スレッド数・
      ハンドル数を正しく返すことを実機確認する
- [x] 6.2 Tier2（`tier2-notepad-test`、実Windows Sandbox VM）を`srm run`で
      起動し、`srm diag`がゲスト内`--nested`経由で対象PIDのサンプリング結果を
      正しく返すことを実機確認する。同一セッションで複数回呼び出しても
      毎回新しいrequestIdで正しく往復することを確認する
- [x] 6.3 登録されていないアプリ名で`srm diag`を呼び、エラーメッセージと
      非ゼロ終了コードが返ることを実機確認する
