# SRM v0.1 実機テスト手順

`openspec/changes/v01-srm-run/tasks.md` のPhase 8（受け入れ基準）は、
AppContainer・WFP・Job ObjectがすべてWindows固有APIのため、Windows実機での
ビルド・実行が必須。本手順はLinux開発環境では検証不可能な部分を埋めるためのもの。

## 前提条件

- Windows 10/11（Pro/Enterprise可。8.7はHomeエディション機が別途必要なため対応不要と判断し見送り済み。詳細は8.7節参照）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 管理者権限のPowerShell（`setup.ps1` 実行・WFP操作に必要）
- インターネット接続（8.3のテストで `api.anthropic.com` 宛の通信確認に使用）
- （8.2のテストのみ）Node.js + `npm install -g @anthropic-ai/claude-code` 済みであること
  （2026-07-01時点の現行パッケージは `%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`
  というネイティブバイナリを直接実行する構成。`policies/claude-code.yaml` は既にこの構成に
  合わせて更新済み。異なる場所にインストールしている場合はポリシーの
  `executable`/`arguments`/`allow_paths` を実環境に合わせて書き換える）

## ビルド

```powershell
dotnet restore Srm.sln
dotnet build Srm.sln -c Release
dotnet test Srm.sln -c Release    # Srm.PolicyEngine.Tests / Srm.PolicyIntegrity.Tests が通ることを確認
```

`srm` 実行ファイルを使う場合は `Srm.Cli` を発行する:

```powershell
dotnet publish src/Srm.Cli/Srm.Cli.csproj -c Release -r win-x64 --self-contained -o publish
# publish\srm.exe が生成される。以下の手順では `dotnet run --project src/Srm.Cli -- <args>` と
# `publish\srm.exe <args>` のどちらでも可（出力例は srm を使う前提で記載）
```

## セットアップ

管理者権限のPowerShellで:

```powershell
.\setup.ps1
```

`%ProgramData%\SRM\logs` `%ProgramData%\SRM\run` が作成され、`policies\*.yaml` に
対応する `.sha256` サイドカーが生成されることを確認する（リポジトリ直下の`policies\`には
`claude-code`・`smoke-test`・`notepad-test`（配布用サンプル）に加え`diag-appcontainer`・
`gui-smoke-test`（実機診断用、`tools/`配下のexeが必要）があり、setup.ps1は`policies\`配下の
`*.yaml`すべてに対して無条件でサイドカーを生成する）。

---

## 8.1 スモークテスト — cmd.exeのAppContainer内実行

```powershell
srm validate smoke-test          # exit code 0 になることを確認
srm run smoke-test
```

確認項目:
- `hello` が標準出力に表示される
- 通常のユーザーセッションとは異なる制限されたトークンで動作していること
  （Process Explorer で起動した `cmd.exe` のプロパティ → Security タブから
  AppContainer SID を確認できる）
- 管理者権限のないPowerShellから `srm run smoke-test` / `srm stop <app>` を実行すると、
  ハングせず即座に `[権限エラー]` メッセージ（exit code 3）で終了すること
  （`AdministratorChecker.IsAdministrator()` による事前チェック。管理者権限が無い状態で
  WFPエンジンへの接続 (`FwpmEngineOpen0`) を試みるとRPCハンドシェイクがエラーを返さず
  無応答のままハングすることが判明したため追加した）

> **注記（2026-07-01確定）**: `whoami /groups` / `whoami /all` の出力には
> `AppContainer SID` や `ALL APPLICATION PACKAGES` グループが**表示されない**が、
> これはAppContainer化に失敗しているわけではない。Package SID は `TokenGroups`
> ではなく別のトークン情報クラス（`TokenAppContainerSid`）に格納されており、
> `whoami` はそれを列挙しない。AppContainer内で動作していることの確認は
> `whoami /groups` ではなく、特権数が大幅に絞られていること（通常は数十個
> あるところ `SeChangeNotifyPrivilege` など2個のみ）と `Mandatory Label` が
> `Low Mandatory Level` (`S-1-16-4096`) であることで判定する。
>
> なお、子プロセス・孫プロセスへのAppContainerトークン伝播（`cmd.exe`が
> `SECURITY_CAPABILITIES`属性なしで起動した子が同じAppContainerトークンを
> 引き継ぐか）は、`tools/TokenSelfCheck`（自己申告専用の診断exe。
> `GetTokenInformation(TokenIsAppContainer)`を直接呼ぶ）で3世代
> （top/child/grandchild）とも `IsAppContainer=1`・同一のPackage SIDであることを
> 実機で確認済み。正しく伝播している。

## 8.2 claude-code（claude.exe）の隔離実行とファイルアクセス制限

```powershell
srm run claude-code
```

確認項目:
- claude.exe が起動し、Claude Codeが通常通り動作する
- `%USERPROFILE%\Projects` 配下のファイル読み書きは成功する
- ポリシーで許可していないパス（例: `%USERPROFILE%\Desktop` や `C:\Windows\System32\drivers\etc\hosts`）
  への読み書きを試みると Access Denied になることを、Claude Code内から
  ファイル操作コマンドを実行して確認する

> **2026-07-01 実機確認済み**: `arguments: "--version"` での起動確認により、AppContainer内で
> `claude.exe` が正常に起動・実行できることを確認（このテストで `FWP_ACTION_PERMIT` の値誤り・
> `weight`指定の2つのWFPバグを発見・修正した）。
>
> `-p/--print`（非対話モード）での本格テストでは、Claude Code CLI固有の別の問題
> （祖先ディレクトリの属性読み取りが拒否されるとハングする問題。発見・修正済み。
> `AclManager.cs`参照）や、修正後も残る原因不明のCPU消費（約60秒、Claude Code内部の
> ターミナル検出周りの問題と推測、未解決）に阻まれ、Claude Code自体での
> ファイルアクセス制限の直接確認は完了しなかった。
>
> **代わりに `notepad.exe`（`policies/notepad-test.yaml`）で本来の受け入れ基準を確認**:
> 許可パス（`%TEMP%\srm-smoke-test`）への保存は成功、許可していないパス（デスクトップ）
> への保存はエラーになることを実機で確認済み。ファイルアクセス制限のACL実装自体は
> 正しく機能している。
>
> **2026-07-07 再調査（未解決、詳細は[DC-016](views/records/DC-016.md)参照）**:
> claude-code CLIがnpmインストールからネイティブ単体バイナリ（`%USERPROFILE%\
> .local\bin\claude.exe`）に変わったため`policies/claude-code.yaml`を実機に
> 合わせて更新した上で再検証した。「60秒のCPU消費」のうち一部は`AclManager`が
> `%USERPROFILE%\AppData`のような初回未付与フォルダへACL付与する際のNTFS
> 継承伝播の遅さ（初回のみ約100〜180秒、2回目以降は高速）だったことが判明した。
> それとは別に、`-p`実行時にサンドボックス化されたclaude.exeがカーネルモードCPUを
> 消費し続けて終わらない（19分34秒経過時点でも未収束）現象を再現し、独自WFP
> ネットワーク制限の無効化・`.claude`フォルダのACL継承確認・Process Monitorに
> よるシステムコール記録では原因を特定できなかった（ACCESS_DENIEDは一切発生せず、
> 可視化されるI/O量も少ないままカーネルCPUだけが支配的）。次回はWPR/WPAによる
> CPUサンプリングスタックトレースでの追跡が必要。
>
> また、GUIアプリがAppContainer内で正常に表示・操作（クリック・キー入力）できることも
> `tools/GuiSmokeTest`（自作の最小WinFormsアプリ）と`notepad.exe`の両方で確認済み。
> srmの用途（Minecraft等のMod化アプリ、対話的な開発ツールのサンドボックス実行）において
> 重要な確認点。

## 8.3 WFPによるアウトバウンド制限

`srm run claude-code` 実行中（または smoke-test 相当のポリシーに任意の
`allow_hosts` を設定した状態）で、AppContainer内のプロセスから:

```powershell
# 許可ホストへの接続 — 成功するはず
curl.exe https://api.anthropic.com -v

# 許可していないホストへの接続 — タイムアウトまたは接続拒否になるはず
curl.exe https://example.com -v --max-time 5
```

確認項目:
- 許可ホスト（`api.anthropic.com` 等）への接続は成功する
- 許可していないホストへの接続はブロックされる（タイムアウト/RST）
- `srm stop` 後、WFPフィルターが除去され、同じプロセスを通常実行した場合に
  両方の接続が成功することを確認する（クリーンアップの検証）

参考: `wfpdiag.exe` や `netsh wfp show filters` でフィルター登録状況を確認できる。

> **2026-07-01 実機確認済み**: このテストで3つの重大バグを発見・修正した。
> 1. `FWP_ACTION_PERMIT`の定数値誤り・フィルターweightの型誤り（許可ルール追加時に
>    `srm run`自体が例外クラッシュしていた）。
> 2. AppContainerに`internetClient`ケーパビリティが一切付与されておらず、srm独自の
>    WFPルールとは無関係にWindows組み込みのネットワーク隔離が全通信をブロックしていた
>    （許可したはずのホストへのDNS解決すらタイムアウトしていた）。
> 3. **`srm run`のWFPフィルターが、対象アプリ起動直後にほぼ即座に削除されてしまい、
>    実行中はネットワーク制限が実質的に効いていなかった**（最重要）。
>
> 上記全て修正済み。修正後の実機検証では: 許可ホストへの接続成功、未許可ホストへの
> 接続失敗（該当ページのダウンロードなし）、実行中はセッション固有のフィルターが
> 存在し`srm stop`後は完全に0件になる、をそれぞれ確認済み。

> **注記（2026-07-01確定）**: `ping`/`timeout`はAppContainer内で使えない
> （それぞれIPドライバへのアクセス拒否・コンソール入力へのアクセス拒否で即失敗する）ため、
> 8.4/8.5の検証で「しばらく生存し続けるテスト対象」が必要な場合は、
> `tools/TokenSelfCheck/TokenSelfCheck.exe sleep <秒>`
> （`Thread.Sleep`するだけで、ネットワーク・コンソール入力に一切触れない）を使うこと。

## 8.4 `srm list`

`srm run smoke-test` または `srm run claude-code` を実行した状態で別ターミナルから:

```powershell
srm list
```

確認項目:
- アプリ名・PID・起動時刻が表形式で表示される
- プロセスが終了済みの場合は一覧から自動的に消えること（`RunningAppRegistry` の
  生存PIDフィルタリングの検証）

> **2026-07-01 実機確認済み**: `TokenSelfCheck.exe sleep 60` で起動したプロセスに対し
> `srm list` を実行し、NAME/PID/STARTED/POLICYが正しく表形式で表示されることを確認。
> また `ping`失敗直後（プロセスが自然終了した状態）で`srm list`を実行し、
> 一覧から自動的に消えることも確認済み。

## 8.5 `srm stop`

```powershell
srm stop claude-code
```

確認項目:
- 対象プロセスとその子プロセス（Job Object配下）がすべて終了する
  （タスクマネージャーで子プロセスが残っていないことを確認）
- `srm list` から該当エントリが消える
- WFPフィルターがクリーンアップされる（8.3のクリーンアップ確認と合わせて検証）

> **2026-07-01 実機確認済み（単一プロセスのみ）**: `TokenSelfCheck.exe sleep 60` に対し
> `srm stop` を実行し、`停止しました`と表示、直後の`srm list`が空になり、
> プロセス自体も終了していることを確認。ただし今回は子プロセスを持たない単一プロセスでの
> 検証のため、「子プロセス（Job Object配下）も含めて全て終了する」という項目自体は
> 未検証（`StopCommand.cs`は実際には`Process.Kill(entireProcessTree: true)`を使っており、
> コマンド説明文の「Job Object経由で停止する」とは実装が異なる点にも注意）。

## 8.6 ログ出力

```powershell
srm run smoke-test
srm logs smoke-test
```

確認項目:
- `%ProgramData%\SRM\logs\smoke-test\YYYY-MM-DD.jsonl` が生成されている
- 各行が妥当なJSON Lines形式である（`Get-Content ... | ForEach-Object { $_ | ConvertFrom-Json }` でパースできる）
- 8日以上前の日付のダミーファイルを作成して `srm run` を実行し、ローテーションで
  自動削除されることを確認する（任意・手動でファイル作成→実行→削除確認）

> **2026-07-01 実機確認済み（ローテーション部分は任意項目のため未検証）**: `srm logs` で
> 妥当なJSON Lines形式のログ（`ts`/`level`/`app`/`msg`/`extra`フィールド）が読み出せることを確認。
> 複数回の`srm run`で同じ日付ファイルに追記されることも確認済み。

## 8.7 Homeエディションでのtier2エラー — 対応不要（実機検証見送り）

Windows Home エディション機を用意する機会がないため、実機での動作確認は行わない
方針とした。`WindowsEditionDetector.ThrowIfTier2OnHome`によるコード上のガード
（`tier: 2`かつレジストリの`EditionID`が"Home"を含む場合に、起動前にエラー終了する
実装）自体は残しているが、この分岐が実機で意図通り動作するかは未検証のまま。

もし将来Home機（または`EditionID`をHome相当に書き換えたテスト環境）で検証する
機会があれば、以下の手順・確認項目で行う:

```powershell
# tier: 2 を指定したテストポリシーを用意した上で
srm run <tier2-test-policy>
```

確認項目:
- 起動前にエラーで終了する（exit code 非0）
- エラーメッセージに「Homeエディションではtier2非対応」である旨と、代替案
  （tier1を使うか、Pro以上へのアップグレード）が明示されている

## Tier2 (Windows Sandbox) 実機テスト

`openspec/changes/tier2-windows-sandbox/tasks.md` の「6. 実機確認要」に対応する。
Tier1と異なり、Windows Sandbox機能の有効化・Hyper-V有効化が別途必要（Pro/Enterprise機、
`setup.ps1`相当のWindows Sandbox機能有効化コマンドは別途用意する）。DC-010〜DC-013で
設計した内容（多層防御・ACL事前付与・Sandbox境界でのネットワーク制御・provisioningの
オフライン既定・outbox検疫パイプライン）を実機で1件ずつ検証する。項目の詳細・成功条件は
`openspec/changes/tier2-windows-sandbox/tasks.md` の6.1〜6.9を参照。全項目確認できたら
該当チェックボックスを`[x]`に更新すること。

> **2026-07-03 実機確認済み（6.1/6.2/6.9）**: `policies/tier2-smoke-test.yaml`
> （`tier: 2`、`cmd.exe`をゲスト内AppContainerで起動し`C:\srm\outbox\hello.txt`へ
> 書き込むだけの最小構成）で検証。6.3〜6.8は該当コードが存在せず実機テスト不可
> （詳細は`tasks.md`の6章注記を参照）。
>
> このテストで3件の実装バグを発見・修正した（すべて`SandboxLauncher`/`StopCommand`）。
> 1. **LogonCommandで起動されたプロセスに有効なコンソールが無く、標準出力への
>    リダイレクトが無いとnested srm.exeが`ready.signal`到達前に失敗する**
>    （原因究明が長引いた最大の要因。失敗が一切ホストに伝わらず、Task Managerにも
>    プロセスの痕跡が残らないため「起動すらしていないように見える」状態になる。
>    `cmd.exe /c "... > C:\srm\control\nested.log 2>&1"`でラップして解決。このログは
>    ゲスト側の障害調査に今後も使える）
> 2. **`policy.yaml`をcontrolフォルダへコピーする処理が`SandboxLauncher.Launch()`
>    （`WindowsSandbox.exe`起動〜`ready.signal`待機）の後に書かれていたため、
>    ゲスト内nestedが起動直後に読もうとした時点でファイルがまだ存在せず
>    `FileNotFoundException`で毎回失敗していた**（1と直交する別バグ。1のリダイレクト
>    修正で初めて例外の中身が見えるようになり発覚した）。`Launch()`内、VM起動前に
>    コピーするよう修正
> 3. **`srm stop`の正常終了フロー（`stopped.signal`受信）ではWindows Sandbox VM
>    自体を閉じる処理が無く、ウィンドウが開いたまま残る**（強制終了タイムアウト
>    経路にしか`KillHostProcess`が無かった。正常終了時にも呼ぶよう修正）
>
> 修正後の実機検証: `srm run tier2-smoke-test`実行から`ready.signal`まで約11秒
> （6.9）、`outbox\hello.txt`に`hello-tier2`が書き込まれゲスト内AppContainerから
> ホスト事前付与ACL経由での書き込みを確認（6.2）、`srm stop`でVMウィンドウも
> 含めて正常にクリーンアップされることを確認。
>
> **2026-07-03 追加修正（Tier2の範囲外、実機テストで新規発見）**:
> - `Srm.PolicyEditor`が実機（Windows）で初めてビルドされ、`UseWPF=true`のSDK構成
>   では`System.IO`が暗黙usingに含まれず31箇所でコンパイルエラーになることが判明
>   （Linux開発環境では`dotnet build`自体できず未発見だった）。6ファイルへ
>   `using System.IO;`を追加して解消。さらにビルド後の動作確認（新規ポリシー作成
>   画面を開く）で`'BoolToVis'という名前のリソースが見つかりません`という別の
>   XAMLクラッシュを発見・修正（`App.xaml`の`Application.Resources`で定義した
>   `BooleanToVisibilityConverter`が、遅延ロードされる`PolicyEditView`からの
>   `StaticResource`参照時に解決できていなかった。`PolicyEditView.xaml`の
>   `UserControl.Resources`にローカル定義するよう変更）。8.8のその他の項目は
>   2026-07-03に別途実機確認済み（8.8節末尾の追記を参照）
> - `Srm.Runtime.Tests`の`EvidenceSanitizerTests`/`EvidenceQuarantineStoreTests`が
>   実機Windowsで2件失敗する問題を修正。表面的には`CON.txt`のような予約デバイス名
>   ファイルを`File.WriteAllText`で作成するテストの前提が環境依存で崩れることが
>   原因に見えたが（`\\?\`プレフィックスで確実に作成するよう修正）、調査の結果
>   `EvidenceSanitizer`本体にも実バグがあることが判明：`Path.GetRelativePath`が
>   予約デバイス名パスを`\\.\CON`のような別物に正規化してしまい列挙元によって
>   結果が不一致になる問題（`GetSafeRelativePath`という正規化APIを経由しない
>   単純な文字列切り出しに置き換えて解消）と、`File.GetAttributes`が予約デバイス名
>   パスに対して例外を投げ、その`catch { continue; }`が予約名検出ロジックごと
>   丸ごとスキップしてしまっていた問題（チェックの順序を独立させて解消）。
>   `Srm.Runtime.Tests`は29件全てパス
>
> **2026-07-03 6.5/6.7/6.8を追加実装・実機確認済み**: `StopTier2`が`srm stop`の
> 最後に`EvidenceQuarantineStore.Quarantine()`を呼ぶよう配線（6.5）。
> `WindowsDefenderScanner`（`MpCmdRun.exe`を新規/呼び出し、結果は合否ゲートにせず
> `manifest.json`の`scan_result`へ記録するのみ、DC-013）を新規実装（6.7）。
> `srm run`→`srm stop`→`srm evidence list/show/promote`を実runで確認、
> `manifest.json`に`"scan_result": "clean"`が記録され、`promote`で指定先への
> コピーと状態遷移(`Quarantined`→`Promoted`)が正しく行われることを確認（6.8）。
>
> この過程で検疫ストアのACLバグを発見・修正: `%ProgramData%\SRM\evidence\`配下の
> ファイルは既定の継承ACLだと`BUILTIN\Users`が読み取り専用（`RX`）になり、
> 管理者権限の`srm run/stop`が作成した検疫データを非管理者の`srm evidence
> promote/reject`から更新しようとすると`UnauthorizedAccessException`になっていた
> （`srm evidence`系コマンドは非管理者で動く設計、DC-013）。`Quarantine()`実行時に
> evidenceルートへ`BUILTIN\Users`の継承可能な書き込み権限を一度だけ追加するよう
> 修正（`EvidenceQuarantineStore.EnsureUserWritable`）。この修正より前に作成された
> 検疫データは古いACLのままなので、確認は新しいrunで行うこと。
>
> **2026-07-03 6.6を追加実装・実機確認済み**: `evidence.max_outbox_bytes`
> ポリシーフィールドを新設。指定時のみ`srm run`が`srm run <app> --quota-monitor
> --control-dir ... --outbox-dir ... --max-bytes ...`をdetachedプロセスとして
> 追加起動する（`srm run`自体は起動後すぐ戻る設計のため、実行中ずっとポーリング
> し続ける役目は別プロセスに担わせる。DC-008と同じ「IPC・デーモンなし」の思想）。
> `policies/tier2-quota-test.yaml`（`max_outbox_bytes: 4096`、cmd.exeが
> outboxへ無停止で追記し続けるループ）で検証: 監視プロセス起動から約5.7秒後
> （ポーリング間隔5秒と一致）にoutboxが7998バイトへ達したことを検出して
> `stop.signal`を送出し、ゲスト内nestedが対象プロセスを終了、最終的に
> `growing.txt`は500,000回中約404回で打ち切られた（完走なら約21MB相当）。
> 監視プロセスが行うのはゲスト内対象アプリの早期終了のみで、VM自体
> （`WindowsSandbox.exe`）のクローズとevidence検疫は従来通り`srm stop`実行時
> にのみ行われる（監視プロセス自身がVM終了までは担わない設計）。
>
> **2026-07-03 6.4を追加実装・実機確認済み**: ホスト側`%ProgramData%\SRM\
> toolchain-cache\<name>\<version>\*.zip`（srmが取得・展開はせず、事前配置は
> ユーザー側の責務、DC-012）を`provision.toolchain`指定時のみ`C:\srm\provision`
> として読み取り専用マウントするよう`SandboxConfigGenerator`/`SandboxLauncher`
> を拡張。ゲスト内`Provisioner`（新規）が`provision.steps`の`"unzip: <name>"`
> （ゲスト内ローカル`C:\SrmProvision\<name>\`へ展開しPATH追加）と
> `"run: <コマンド>"`（cmd.exe経由で実行、非ゼロ終了コードは例外化してprovision
> 全体を失敗させる）の2種類のみ解釈する単純なDSLとして実装。ホスト側は
> VM起動前にtoolchain-cacheの存在を検証し、無ければ即エラー終了する
> （srmはダウンロードを代行しないため、起動後に気づいても手の打ちようが無い）。
> `policies/tier2-provision-test.yaml`（ダミーツールチェーン`mytool.zip`を
> 展開→PATH追加→`run:`経由で`mytool.cmd`を実行→対象アプリ起動、の一連の
> 流れ）で検証: `outbox\provision-run.txt`に`run:`ステップの実行結果
> （`mytool-was-here`、PATH解決成功の証跡）が正しく書き込まれ、続けて対象アプリ
> （`outbox\app-ran.txt`）も問題なく起動、evidenceの検疫にも両ファイルが
> 正しく取り込まれることを確認。
>
> **2026-07-03 6.3を実機PoC→DC-015記録→実装・実機確認済み**: DC-011が正と
> していたHyper-V Firewallによるゲスト通信フィルタが実現不可能と実機PoCで
> 判明した。検証した3手段はいずれも失敗:
> 1. `Add-VMNetworkAdapterExtendedAcl`（Hyper-V VM Network Adapter拡張ACL）
>    — `Get-VM`はWindows Sandboxを一切返さず、`Get-VMNetworkAdapter -All`が
>    返す"Container NIC"も`VMName`が空で個々のSandboxインスタンスと対応付け
>    できない。そもそもAPI自体が「自動インターネット接続共有スイッチは変更
>    できません」というエラーで拒否される
> 2. `vEthernet (Default Switch)`にインターフェーススコープした通常の
>    Windows Defender Firewallの`Outbound`ブロックルール — ルールは正しく
>    作成・スコープされ有効化されたことを`Get-NetFirewallInterfaceFilter`で
>    確認したが、Sandbox内からのHTTPS接続は何の影響も受けず成功し続けた
> 3. 同インターフェースへの`Inbound`ブロックルール — 同様に効果なし
>
> Windows SandboxはHyper-V Manager配下の伝統的なVMオブジェクトモデルに乗って
> おらず、恐らく通常のWFP評価層を経由しない専用の高速パスでネットワークを
> 処理していると推測される。DC-011のreview_trigger通り、[DC-015](views/records/DC-015.md)
> としてDC-011を改訂し、ゲスト内WFP相当実装（既存`WfpManager`をTier1と全く
> 同じ形で`RunCommand.RunNested`から呼ぶだけで、Sandbox境界向けの新規実装は
> 不要だった）に切り替えた。`policies/tier2-network-test.yaml`
> （`allow_hosts: [example.com]`、対象アプリのAppContainer内からexample.comと
> neverssl.comの両方へ`curl`する）で検証: 許可ホスト（example.com）は
> `HTTP/1.1 200 OK`で成功、未許可ホスト（neverssl.com）は`Bad access`で
> 拒否されることを、ホスト側ではなくゲスト内AppContainerから確認した。

## 8.8 ポリシーエディタ（`Srm.PolicyEditor`）

`srm.exe` には組み込まれていないローカル専用のWPFアプリ（DC-014でBlazor Serverから
書き直し）。詳細は [src/Srm.PolicyEditor/README.md](src/Srm.PolicyEditor/README.md) を参照。
このLinux開発環境では`Microsoft.NET.Sdk.WindowsDesktop`ワークロードが無いため
`dotnet build`自体ができず、当初は下記すべて未検証だった。

> **2026-07-03 実機確認済み**（ビルドエラー・XAMLクラッシュの修正は上記6.3の節を参照）。
> UI Automationで全項目を検証: 既存ポリシー（smoke-test）を開いて全フィールドの反映
> （name/description/tier/application/allow_paths/access/max_processes/
> retention_days/logging.level/allow_child_processes）を確認、`variables`/
> `filesystem.allow_paths`/`network.allow_hosts`の行を追加・削除（3種とも正常）、
> 新規ポリシー作成→保存→`srm.exe validate`が成功しYAML互換性を確認、`.sha256`
> サイドカーが64文字小文字hex・改行無しで生成されることを確認、`max_processes: 0`
> でのバリデーションエラー表示とファイル非上書きを確認、「対象フォルダから生成」
> （`publish`フォルダ指定）で実行ファイル候補・allow_paths候補・ホスト候補
> （`system.io`等の意図された誤検出例も含む）が数秒以内に表示されホスト候補の
> allow_hosts反映も確認、`tools\package.ps1`で再現したリリースzip内の
> `srm.exe`/`Srm.PolicyEditor.exe`両方の動作を確認。
>
> この過程で2件の実バグを発見・修正した:
> 1. `Button`/`CheckBox`の`Content`・`GroupBox`の`Header`にプレーン文字列で
>    `allow_hosts`等アンダースコア入りの文字列を指定すると、WPFの既定の
>    `AccessText`（ニーモニック）解釈により単一の`_`が消費され、画面表示・
>    アクセシビリティ名の両方から消えてしまう（`allow_hosts`→`allowhosts`等、
>    4箇所）。XAML側で`_`を`__`にエスケープして解消
> 2. （調査したが実バグではなかった）参照ダイアログがエージェントの自動起動
>    プロセスからは一切表示されず`NativeDialogService.PickFolder()`が空文字列を
>    返しフィールドを上書きする現象を観測したが、ユーザーが自身のデスクトップから
>    直接起動した場合は正常に表示されることを確認。エージェントの起動コンテキスト
>    （デスクトップ/ウィンドウステーションのアクセス制限）に起因する誤検知と判断。
>    念のためオーナーウィンドウを明示する変更（`ShowDialog(Application.Current.
>    MainWindow)`）は実害が無く、より正しい呼び出し方のため残した

```powershell
dotnet run --project src/Srm.PolicyEditor
```

確認項目:
- 起動するとネイティブウィンドウ（`MainWindow`）が直接表示され、ポリシー一覧が表示されること
  （`.sha256` サイドカーは一覧に混ざらないこと。HTTPサーバー・ブラウザは一切起動しない）
- 既存ポリシー（例: `smoke-test`）を開き、全フィールドが正しく反映されていること
- `variables` / `filesystem.allow_paths` / `network.allow_hosts` の行を複数追加・削除できること
- 新規ポリシーを作成して保存し、生成されたYAMLに対して
  `dotnet run --project src/Srm.Cli -- validate <作成したポリシー名>` が成功すること
  （フォームで保存したYAMLが `PolicyLoader` と完全互換であることの確認）
- `.sha256` サイドカーが64文字の小文字hex・改行無しで生成されていること
- `tier: 3` や `max_processes: 0` など無効な値を入力して保存を試み、バリデーションエラーが
  表示されファイルが上書きされないこと
- 「対象フォルダから生成」に実在するアプリのフォルダ（例: `publish`）を指定して解析し、
  実行ファイル候補・`allow_paths`・`allow_hosts` 候補が妥当な時間内（数秒程度）に表示されること。
  候補はあくまで下書きであり、明らかな誤検出を手動で除外できること
- 各パス入力欄（対象ディレクトリ・保存先フォルダ・対象フォルダから生成・`application.executable`・
  `application.working_directory`・`filesystem.allow_paths` の各行）の「参照...」ボタンから
  OSのネイティブなダイアログが表示され、選択結果が対応するテキスト欄に反映されること
- `application.executable` の「参照...」ボタンのみファイル選択ダイアログ（フィルタ「実行ファイル
  (*.exe)」）になり、他の項目はすべてフォルダ選択ダイアログになること
- 参照ダイアログでキャンセルした場合、対象のテキスト欄の値が変更されないこと
- テストで作成した一時ポリシーファイルは確認後に削除する

リリース配布物（`srm-<version>.zip` に `srm.exe` と同梱されている `Srm.PolicyEditor.exe`。
単一exe化はしていない）を検証する場合、`tools\package.ps1` で実際のリリースzipと同じものを
ローカルに再現できる:

```powershell
tools\package.ps1 -Version 0.1.0.0-localtest
# artifacts\packages\srm-0.1.0.0-localtest.zip を別ディレクトリに展開し、bin\ の中で
# .\Srm.PolicyEditor.exe と .\srm.exe の両方が正常に動作することを確認する
```

---

## MCPサーバー（Srm.Mcp、DC-017）実機テスト

`openspec/changes/archive/2026-07-11-mcp-server-control/tasks.md` の「9. 実機確認要」に
対応する。チャネルA（対話的ライブ制御）・チャネルB（オートパイロット）とも、CIの
`windows-latest`ランナーで`WindowGuard`/`JobObjectManager`のWin32メカニクス
（`notepad.exe`を直接起動してのSendInput/EnumWindows等）は検証済みだが、
AppContainer/Windows Sandbox越し・MCPクライアント経由のエンドツーエンド動作は
Windows実機（Windows Sandbox機能有効化済み）でのみ確認できる。

### 前提条件（8章の前提に加えて）

- MCPクライアント（Claude Code等）がインストール済みで、`mcpServers`設定に
  `Srm.Mcp.exe`のフルパスを登録できること（[manual/usage.md](manual/usage.md#mcpサーバーsrmmcp)参照）
- ウィンドウのクラス名・所有プロセスを調べるツール（Spy++、Inspect.exe、または
  Process Explorer）— 9.3で使用

### ビルド・配置

```powershell
dotnet publish src/Srm.Mcp/Srm.Mcp.csproj -c Release -r win-x64 --self-contained -o publish
# publish\Srm.Mcp.exe が生成される。srm.exeと同じフォルダに置くこと
# （evidence.max_outbox_bytes監視プロセスの起動やpolicies\探索がsrm.exeを
# 隣接パスから探すため）
```

### 9.1 MCPクライアントからのSrm.Mcp起動とCLI相当ツールの動作確認

MCPクライアント設定に`publish\Srm.Mcp.exe`を登録し、管理者権限で起動したクライアント
から`srm_run`（`smoke-test`）→`srm_list`→`srm_stop`を順に呼び出す。

確認項目:
- 各ツール呼び出しが構造化データ（JSON）として返り、CLIの`srm run`/`list`/`stop`と
  同じ結果になること
- `srm_validate`/`srm_logs`/`srm_evidence_list`も一通り呼び、CLI版と結果が一致すること

**実施結果（2026-07-12、Windows実機）**: 成功。stdio JSON-RPCで
`initialize`/`tools/list`/`tools/call`（`srm_run`/`srm_list`/`srm_logs`/
`srm_validate`/`srm_evidence_list`/`srm_stop`）を実際に往復し、CLI版と同じ
構造化データが返ることを確認した。

検証の過程で2件の問題を発見した:
1. **【DC-018で修正済み】** `tools\package.ps1`が生成する結合`bin\`フォルダの
   `srm.exe`は、`System.Text.Json`/`System.IO.Pipelines`のバージョン競合により
   `list`/`run`/`stop`が無条件でクラッシュしていた（Srm.Mcp単体publishでは
   MCPツール自体は問題なく動作するため、9.1のMCP経由テストでは気づきにくい。
   `bin\srm.exe list`をCLIから直接叩いて発覚）。`Srm.Runtime.csproj`への
   バージョン固定で解消（DC-018）。
2. **【修正済み・DC-017 review_trigger】**: `srm_run`で起動したアプリが
   コンソール出力を行うと、その出力がMCPサーバーのstdout（JSON-RPC伝送路）に
   生テキストとして混入する（`smoke-test`ポリシーの`echo hello`で再現）。
   `AppContainerLauncher.Launch`に`redirectStdioToNul`パラメータを追加し、
   Srm.Mcp経由の`srm_run`のみ対象アプリの標準入出力をNULへリダイレクトするよう
   修正した。再検証で混入が解消したことを確認済み。

### 9.2 チャネルA（Tier1）: send_key/send_mouse/screenshot

`srm_run`で`notepad-test`を起動した状態で:
- `send_key`で任意のテキストを送り、`screenshot`で画面キャプチャを取得して、
  メモ帳に実際にテキストが入力されたことを画像で確認する
- `send_mouse`でメニュー項目等をクリックし、期待通りの操作結果になることを確認する

確認項目:
- 送信の都度、対象ウィンドウの実体再検証（`WindowGuard`）が行われても正常系では
  拒否されないこと
- 存在しないアプリ名を指定した場合にエラー（`実行中のアプリが見つかりません`）に
  なること

**実施結果（2026-07-12、Windows実機）**: 成功。`notepad-test`起動→`send_key`→
`screenshot`で、実際にメモ帳へテキストが入力されたことを画像で確認した。

`send_mouse`はSendInput自体は成功する（`ClientToScreen`で正しく座標変換される）
が、**【修正済み・DC-017 review_trigger】**として1件発見した:
`screenshot`はウィンドウ全体（タイトルバー・メニューバー含む`GetWindowRect`+
`PrintWindow`）を返すのに対し、`send_mouse`の座標はクライアント領域基準
（`GetClientRect`+`ClientToScreen`）であり、両者の原点が一致しない。
screenshotの画像を見て素朴にピクセル座標を指定すると、タイトルバー・メニュー
バーの高さ分だけ実際のクリック位置がずれる（メニューバーを狙ったつもりが
テキスト編集領域をクリックしていたことを実機で再現・確認した）。
`screenshot`ツールが画像と併せて`client_offset`をテキストブロックで返すよう
修正し、画像上のピクセル座標からこのoffsetを差し引いた座標でsend_mouseを
呼ぶと意図した位置（メニューバー）を正しくクリックできることを再検証で確認した。

### 9.3 チャネルA（Tier2）: WindowsSandbox.exeウィンドウの実体確認【最重要】

`tier2-smoke-test`等のTier2ポリシーを`srm_run`で起動した直後、実際に開いた
Windows Sandboxウィンドウに対してSpy++/Inspect.exeで以下を記録する:
- ウィンドウクラス名
- 所有プロセスの実行ファイルフルパス（`WindowsSandbox.exe`自身か、
  `WindowsSandboxClient.exe`等の別プロセスか）

記録した実際の値を、実装が仮定している期待値（`Tier2SandboxWindowResolver`:
`%SystemRoot%\System32`配下・ファイル名に"WindowsSandbox"を含む）と突き合わせる。

- **一致する場合**: `send_key`/`send_mouse`/`screenshot`を実際に呼び、RDP経由で
  ゲストに正しく反映されるか（`screenshot`はゲストデスクトップ全体が写るはず）を確認する
- **一致しない場合**: `records/DC-017.yaml`のreview_triggerに従い、
  `Tier2SandboxWindowResolver`のPID/実行ファイル解決ロジックを実際の構成に
  合わせて修正し、新しいDecisionRecordとして記録する

**実施結果（2026-07-12、Windows実機）**: **一致した**。実際にレンダリング
ウィンドウを持つのは`WindowsSandbox.exe`（`RunningApp.Pid`が指す本体）
自身ではなく、その直接の子プロセスとして起動される`WindowsSandboxClient.exe`
（`%SystemRoot%\System32\WindowsSandboxClient.exe`、`Get-CimInstance
Win32_Process`で親子関係を確認）。`Tier2SandboxWindowResolver`の期待値
（実行ファイルディレクトリ・ファイル名部分一致"WindowsSandbox"）と一致して
おり、実装の修正は不要と確認した。

`screenshot`（PrintWindowベース）はRDP描画のウィンドウでも正常にPNGを返した。
`run_scenario`経由（ゲスト内`--nested`が同一セッションでSendInput、9.5参照）は
send_key/screenshotとも完全に成功した。一方、ホスト側MCPサーバーから
`WindowsSandboxClient.exe`ウィンドウへ直接`send_key`する経路（チャネルA）では、
WindowGuardのフォアグラウンド検証（ホスト側の1枚のRDP描画ウィンドウ）を
通過しても、ゲスト内のnotepad.exeが実際にフォーカスを持っているとは限らず、
キー入力がゲスト内の意図した場所に届かないことがあった（新規の懸念事項として
`records/DC-017.yaml`のreview_triggerに記録。**対応済み**: `send_key`の
ツール説明文に、Tier2ではこの保証が無いこと・送信前後でscreenshotによる
目視確認を徹底すべきことを明記した）。

パッケージング修正前は、`ToolingHostDir`（ゲストへマップされる`srm.exe`一式）が
9.1で発見した壊れた結合`bin\`フォルダを指していたため、ゲスト内`srm --nested`が
起動直後にクラッシュし、`ready.signal`がタイムアウトしてnotepad.exeが一切
起動しない状態だった（`control\nested.log`で特定）。DC-018の修正後は
`readySignalTimedOut: false`となりnotepad.exeが正常に起動することを確認した。

### 9.4 WindowGuardの拒否確認（対象外ウィンドウ）

サンドボックス外のメモ帳を手動で別途開いた状態で、SRM管理下のアプリ名を指定して
`send_key`等を呼ぶ（＝正しいapp名だが、たまたま別の無関係なウィンドウが前面に
出ているような状況を再現する）。

確認項目:
- SRMが起動・管理しているウィンドウ以外には一切入力が送られないこと
- `srm_logs`に`guard_rejected`イベント（拒否理由付き）が記録されること

**実施結果（2026-07-12、Windows実機）**: 成功。`notepad-test`をSRM管理下で
起動した状態で、別途手動起動した無関係な`notepad.exe`（SRM管理外）を並行して
存在させ、`send_key`で`notepad-test`宛にテキストを送信した。SRM管理下の
メモ帳には正しくテキストが入力され、手動起動した管理外メモ帳は完全に無傷
（空のまま）であることをそれぞれのウィンドウキャプチャで確認した。
`Tier1WindowResolver`がJob Object配下PIDのみを候補にするため、管理外プロセスは
そもそも候補にすら入らず、構造的に混入し得ないことを実機で確認した。

### 9.5 チャネルB: run_scenario/get_scenario_result

Tier2アプリを`srm_run`で起動後、`run_scenario`で`send_key`→`screenshot`を含む
シナリオを投入し、`get_scenario_result`（`waitSeconds`指定）で結果を取得する。

確認項目:
- ゲスト内`--nested`が`scenario.json`を検出し、各ステップを自律実行できること
- `screenshot`ステップの証跡が`outbox`経由でevidence検疫ストアに取り込まれ、
  `srm_evidence_show`で確認できること
- `scenario-result.json`のステップごとの成否が実際の実行結果と一致すること

**実施結果（2026-07-12、Windows実機）**: 成功。`send_key`→`screenshot`の
2ステップシナリオを投入し、`get_scenario_result`で
`{"success":true,"steps":[...すべてsuccess:true...]}`を取得した。`srm stop`後、
`srm_evidence_list`で`state:"Quarantined"`・`file_count:1`（違反0件）を確認し、
証跡がevidence検疫パイプラインに正しく取り込まれることを確認した。

### 9.6 host↔guest間の通信不安定時の完走確認

`run_scenario`でシナリオを投入した直後にMCPクライアント（`Srm.Mcp.exe`）を
意図的に終了させ、しばらく待ってから新しいMCPクライアントセッションで
`get_scenario_result`を呼び直す。

確認項目:
- MCPサーバー側が非常駐（都度起動）であっても、投入済みシナリオはゲスト内で
  完走していること
- 新しいセッションから`get_scenario_result`で正しく結果が取得できること
  （host側の状態はファイル経由でのみ引き継がれるため、MCPサーバーの再起動に
  影響されないことの確認）

**実施結果（2026-07-12、Windows実機）**: 成功。`run_scenario`投入直後に
MCPサーバープロセス（`Srm.Mcp.exe`）が完全終了したことをプロセス一覧で確認した
うえで、十分待ってから完全に別のMCPクライアントプロセスを新規起動し
`get_scenario_result`を呼んだところ、`success:true`で完了済みの結果を正しく
取得できた。host側の状態がファイル（`scenario-result.json`）経由でのみ
引き継がれ、MCPサーバーの再起動に影響されない設計であることを実機で確認した。

### 9.7 SRM管理外プロセスへの拒否確認（スコープ制約）

SRMが起動していない任意のプロセス（例えば手動起動したメモ帳）のウィンドウに対し、
別のSRM管理下のapp名を装って`send_key`等を試みても、そのプロセスには一切到達
できないこと（`WindowGuard`のPID所属チェックで必ず拒否される）を確認する。

**実施結果（2026-07-12、Windows実機）**: 成功。9.4で実施した検証（SRM管理下の
`notepad-test`と手動起動した管理外メモ帳を並行させ、`send_key`が管理下の
ウィンドウにのみ届き管理外メモ帳が無傷であることを確認）がそのまま9.7の
要件も満たす。`Tier1WindowResolver`のJob Object PIDスコープにより、
管理外プロセスのウィンドウは候補列挙の時点で構造的に排除されるため、
app名を装っても到達し得ないことを確認した。

---

## Tier2チャネルAフォーカス保証（DC-019）実機テスト

`openspec/changes/tier2-channel-a-focus-guarantee/tasks.md`の「6. 実機確認要」に
対応する。9.3で判明したTier2チャネルAのフォーカス未検証問題に対する恒久対策
（ゲスト内`--nested`経由のフォーカス確認プロトコル）の検証。

**6.1（フォーカス保証の実効性）**: 成功。`tier2-notepad-test`起動後、`send_key`を
4回連続で呼び、すべてゲスト内notepadへ正しくテキストが入力されることを確認した。
1回目の直後（93ms後）に撮ったスクリーンショットが空に見えるという事象があったが、
これはPrintWindow/RDP描画の反映遅延によるものであり、少し待ってから撮った
スクリーンショットでは正しく反映されていた（フォーカス保証の不具合ではない）。
DC-017 9.3で発生していた「フォーカスが無く入力が全く届かない」事象は再現しなかった。

**6.2（ゲスト無応答時のフェイルセーフ）**: 部分的に確認。`WindowsSandbox.exe`
（VM本体）のみを強制終了し`WindowsSandboxClient.exe`（ホスト側RDPウィンドウ）を
残す形でゲスト無応答状態を再現したところ、`RunningAppRegistry`側がホスト
プロセスの消失を検知し「実行中のアプリが見つかりません」を即座に返した。狙って
いたフォーカス確認タイムアウト経路（`EnsureGuestFocus`の`WaitForFocusResult`）
そのものは、より手前のガードで先にエラーになるため実機では未到達だったが、
タイムアウトのロジック自体は`InteractiveInputToolsTests.
EnsureGuestFocus_Tier2_TimesOutWhenGuestNeverResponds`でユニットテスト済み。
いずれの経路でもハングせず境界のある時間で明確なエラーが返ることを確認した。

**6.3（追加レイテンシ）**: 実測。初回（ゲスト起動直後）961ms、2回目以降
292ms/288ms。対話的操作として体感上問題ないレベルと判断し、
`SandboxControlChannel.PollInterval`（250ms）の変更は不要と判断した。

**6.4（チャネルBへの影響なし）**: 成功。`run_scenario`（send_key+screenshot）が
`success:true`で完走し、`get_scenario_result`が約2.9秒で結果を返すことを確認した。
DC-017 9.5で確認済みの挙動から変化は見られなかった。

---

## 結果の記録

各項目の結果（成功/失敗・ログ・スクリーンショット）は `records/` に
DecisionRecord/LearningRecordとして手動で記録する（`.pkmp/bin/pkmp.js` をCLIから直接実行、
または `registry/records.yaml` への登録まで含めて手作業で作成する。AIエージェントから
自然に呼び出せるスキル導線は現時点で無い）。全項目成功したら `tasks.md` の該当チェックボックスを
`[x]` に更新し、OpenSpec changeのarchive可否を判断する。

**2026-07-12実施済み**: 9.1〜9.7すべてWindows実機（Hyper-V/Windows Sandbox有効）で
実施し、結果を各項目の「実施結果」に記録した（`openspec/changes/archive/
2026-07-11-mcp-server-control/tasks.md`の該当チェックボックスも`[x]`に更新済み）。
検証中に発見したパッケージング不具合の修正は`records/DC-018.yaml`、未修正の
既知の懸念事項（stdout混入・座標系不一致・Tier2チャネルAのフォーカス保証の限界）は
`records/DC-017.yaml`のreview_triggerに記録した。
