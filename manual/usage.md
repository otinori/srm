# SRM 使用ガイド

> プログラミング・Windowsの内部構造に馴染みがない場合は、先に
> [beginners-guide.md](beginners-guide.md) を読むと以降の用語（管理者権限・
> コマンドライン・YAMLファイルなど）が分かりやすくなります。

## 目次

- [セットアップ](#セットアップ)
- [コマンドリファレンス](#コマンドリファレンス)
  - [srm run](#srm-run)
  - [srm stop](#srm-stop)
  - [srm list](#srm-list)
  - [srm logs](#srm-logs)
  - [srm validate](#srm-validate)
  - [srm evidence](#srm-evidence)
  - [srm diag](#srm-diag)
  - [srm audit](#srm-audit)
  - [srm transfer](#srm-transfer)
  - [srm cleanup-account](#srm-cleanup-account)
- [MCPサーバー（Srm.Mcp）](#mcpサーバーsrmmcp)
- [ポリシーファイルリファレンス](#ポリシーファイルリファレンス)
- [整合性チェック](#整合性チェック)
- [ネットワークフィルタリング](#ネットワークフィルタリング)
- [ログ](#ログ)
- [トラブルシューティング](#トラブルシューティング)

---

## セットアップ

展開すると次の構成になる（`srm.exe`・`Srm.PolicyEditor.exe` はいずれも `bin\` 配下）：

```
srm-<version>/
  bin/
    srm.exe
    Srm.PolicyEditor.exe
    （+ 依存DLL一式・wwwroot/・appsettings.json）
  policies/
    claude-code.yaml
    smoke-test.yaml
    notepad-test.yaml
  setup.ps1
  usage.md
```

### 初回セットアップ

管理者権限の PowerShell で実行する（WFP 操作に管理者権限が必要）。

```powershell
cd <展開先フォルダ>
.\setup.ps1
```

**setup.ps1 が行うこと：**
- `%ProgramData%\SRM\logs` — 構造化ログの保存先
- `%ProgramData%\SRM\run` — 実行状態の保存先
- `policies\*.sha256` — ポリシー整合性サイドカーの生成

以降のコマンド例は `bin\` に移動してから実行する前提で記載する：

```powershell
cd bin
```

### Tier2（Windows Sandbox）を使う場合の追加セットアップ

`tier: 2` のポリシーを実行するには、`setup.ps1` に加えて Windows Sandbox 機能を
有効化する必要がある（Pro / Enterprise のみ。Home では tier2 非対応）。管理者権限の
PowerShell で:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

実行後、再起動が必要と案内されたら再起動する。有効化されると `%WINDIR%\System32\WindowsSandbox.exe`
が存在するようになる。

### ポリシーの配置

`policies\` フォルダに `.yaml` ファイルを追加し、`bin\srm.exe validate <name> --sign` でサイドカーを生成すれば使える状態になる。YAMLを手書きせずネイティブのフォームで作成・編集したい場合は
`bin\Srm.PolicyEditor.exe` を実行する（ネイティブウィンドウが直接表示される、ローカル専用のWPFアプリ。
HTTPサーバー・ブラウザは一切起動しない）。詳細は
[src/Srm.PolicyEditor/README.md](../src/Srm.PolicyEditor/README.md) を参照。

---

## コマンドリファレンス

### srm run

```
srm run <policy> [--no-integrity-check]
```

ポリシーの `tier` に応じてアプリを AppContainer（tier 1）または Windows Sandbox（tier 2）内で
起動する。起動後すぐに srm.exe 自体は終了し、アプリはバックグラウンドで動作し続ける。
**管理者権限のPowerShellから実行すること**（非管理者で実行した場合は、ACL/WFP操作に進む前に即座に権限エラーで終了する）。

| オプション | 説明 |
|---|---|
| `--no-integrity-check` | sha256 サイドカーの検証をスキップする（開発・テスト時のみ） |

**動作順序（tier: 1 — AppContainer）：**
1. 管理者権限で実行されているか確認（不足していれば即座にエラー終了）
2. ポリシーファイルの整合性を検証（`--no-integrity-check` でスキップ）
3. ポリシーの構文を検証
4. AppContainer SID を生成
5. `allow_paths` の各ディレクトリに AppContainer SID のアクセス権を付与
6. WFP でアウトバウンドをブロックし、`allow_hosts` のみ許可
7. Job Object を作成してプロセスを起動
8. 実行状態を `%ProgramData%\SRM\run\running.json` に記録

**動作順序（tier: 2 — Windows Sandbox）：**
1. `provision.toolchain` が指定されていれば、ホスト側の事前キャッシュ
   （`%ProgramData%\SRM\toolchain-cache\<name>\<version>\`）の存在を確認（無ければ即エラー終了。
   srmはツールチェーンの取得は代行しない）
2. `.wsb` 設定を生成し、対象ポリシーのAppContainer SIDに対してoutbox/input/controlフォルダへの
   ACLをホスト側で事前付与してからWindows Sandboxを起動する
3. ゲスト内で `srm.exe run --nested` が自動実行され、`provision.steps` の展開・実行の後、
   tier 1と同じAppContainer隔離・WFPフィルタでアプリを起動する（ネットワーク制御はゲスト内で
   行う。Hyper-V Firewallはこの用途に使えないため。詳細は
   [DC-015](../views/records/DC-015.md)）
4. `evidence.max_outbox_bytes` が指定されていれば、ホスト側でoutboxサイズを監視する別プロセスを
   起動し、超過時はゲスト側を早期終了させる

**例：**

```powershell
.\srm.exe run claude-code
# 起動しました: claude-code (PID: 12345)

.\srm.exe run my-tier2-app
# 起動しました: my-tier2-app (Tier2, RunID: 20260704-120000, Host PID: 23456)
```

---

### srm stop

```
srm stop <app>
```

指定したアプリとその子プロセスをまとめて終了する（プロセスツリーごと終了。tier 1では
WFP フィルターも同時に除去される）。**管理者権限のPowerShellから実行すること**（`srm run` と同様、非管理者では即座に権限エラーで終了する）。

tier 2の場合、まずゲスト内へ正常終了を要求し、対象アプリの終了・evidenceのquarantine完了を
30秒待つ。応答が無ければホスト側のWindows Sandboxプロセスを強制終了する。どちらの経路でも、
停止確定後にホスト側でoutboxの内容を検疫ストアへ取り込む（`srm evidence` 参照）。

**例：**

```powershell
.\srm.exe stop claude-code
```

アプリがすでに終了している場合はその旨のメッセージを表示して正常終了する。

---

### srm list

```
srm list
```

現在実行中のサンドボックスを一覧表示する。プロセスがすでに終了している場合は自動的に一覧から除外される。

**例：**

```powershell
.\srm.exe list
```

**出力例：**

```
Name          PID    Started
claude-code   12345  2026-06-30 10:00:00 +09:00
smoke-test    67890  2026-06-30 10:05:00 +09:00
```

---

### srm logs

```
srm logs <app> [-n <lines>]
```

アプリの構造化ログ（JSON Lines形式）を表示する。

| オプション | デフォルト | 説明 |
|---|---|---|
| `-n`, `--lines` | 50 | 表示する行数 |

**例：**

```powershell
.\srm.exe logs claude-code -n 100
```

ログファイルは `%ProgramData%\SRM\logs\<app>\YYYY-MM-DD.jsonl` に保存される。

---

### srm validate

```
srm validate <policy> [--sign]
```

ポリシーファイルの構文・値域・整合性を検証する。

| オプション | 説明 |
|---|---|
| `--sign` | ポリシーファイルの SHA256 サイドカーを生成・更新する |

ポリシーを作成または変更した後は必ず `--sign` を実行してからサイドカーを更新すること。

**例：**

```powershell
# 検証のみ（サイドカーが存在していることが前提）
.\srm.exe validate claude-code

# サイドカーを生成・更新する
.\srm.exe validate claude-code --sign
```

`srm validate` は `srm run`/`srm stop` と異なり管理者権限は不要（ファイルの読み取り・ハッシュ計算のみ）。

---

### srm evidence

```
srm evidence list <app>
srm evidence show <app> <runid>
srm evidence promote <app> <runid> --dest <path>
srm evidence reject <app> <runid>
```

Tier2の実行結果（対象アプリがoutboxへ書き出したファイル）は、VM停止確定後にホスト側で
自動的に検疫ストア（`%ProgramData%\SRM\evidence\<app>\<runid>\`）へ取り込まれる
（パストラバーサル・予約デバイス名・reparse pointの検出、SHA256マニフェスト生成、Windows
Defenderによるスキャンを実施。スキャン結果は合否判定には使わずメタデータとして記録するのみ）。
検疫データをユーザーの作業ディレクトリへ反映するかどうかは、常に人間が `promote`/`reject` で
明示的に判断する（自動では一切反映されない）。管理者権限は不要（`srm run`/`srm stop` が
既に検疫ストアへの書き込み権限を用意している）。

| サブコマンド | 説明 |
|---|---|
| `list <app>` | 検疫中の実行一覧を表示する |
| `show <app> <runid>` | マニフェスト・違反検出結果の詳細を表示する |
| `promote <app> <runid> --dest <path>` | 検疫データを指定先へコピーする（検疫側の控えは監査証跡として残るため、moveではなくcopy） |
| `reject <app> <runid>` | 検疫データを明示的に却下する（削除はしない） |

**例：**

```powershell
.\srm.exe evidence list my-tier2-app
.\srm.exe evidence promote my-tier2-app 20260704-120000 --dest C:\Users\me\Downloads\results
```

---

### srm diag

```
srm diag <app> [--duration-ms <ms>]
srm diag --pid <pid> [--duration-ms <ms>]
```

実行中アプリのCPU時間内訳（ユーザー/カーネル比率）・秒あたりページフォールト数・スレッド数・
ハンドル数を、指定した経過時間（既定1000ms）サンプリングして返す。通常のAIエージェント操作
（`send_key`等）とは異なり、DC-016（AppContainer内でCPUを消費し続ける未解決バグ）のような
実機調査時にのみ使う想定の診断コマンドで、MCPツールとしては公開していない。

Tier1（AppContainer、ホストと同一セッション）はホストプロセスから対象PIDを直接サンプリング
する。Tier2（Windows Sandbox）はゲスト内`--nested`プロセスへ委譲してサンプリングする
（`SandboxControlChannel`経由、DC-020）。管理者権限は不要。

`<app>`の代わりに`--pid <pid>`で任意のプロセスを直接指定することもできる（`RunningAppRegistry`
を経由しないため、`srm run`で起動していないプロセスも対象にできる）。AppContainer隔離あり/なし
での挙動比較など、対照実験を行いたい場合に使う（DC-021）。

**例：**

```powershell
.\srm.exe diag my-app --duration-ms 2000
```

```json
{
  "requestId": "...",
  "success": true,
  "processId": 1234,
  "sampleWindowMs": 2006,
  "userTimePercent": 3.2,
  "kernelTimePercent": 1.1,
  "pageFaultsPerSec": 12.5,
  "threadCount": 7,
  "handleCount": 215
}
```

**`--stacktrace`（カーネルCPUスタックトレース、DC-016/DC-020/DC-021拡張）：**

```
srm diag <app> --stacktrace [--duration-ms <ms>] [--top <n>]
```

カーネルモードCPU時間が支配的な問題（DC-016のようなビジーループ）を調査する際、`wpr.exe`
（Windows Performance Recorder）に頼らず、.NET ETW API（`Microsoft.Diagnostics.Tracing.TraceEvent`、
PerfView/dotnet-traceが内部で使うのと同じライブラリ）で直接カーネルCPUサンプリング＋スタック
トレース採取・シンボル解決を行う。`wpr.exe -stop`がCOMアパートメントエラーで機能しない環境が
実機で確認されたため追加した代替手段。管理者権限が必要。初回実行時はMicrosoft公開シンボル
サーバーへ問い合わせるため数秒〜数十秒かかることがある（以降はローカルキャッシュされ高速化する）。

Tier1・Tier2両対応（Tier2はDC-021で追加）。ETWセッションはサンプリング対象と同じカーネル上
でしか動かせないため、Tier2ではゲスト内`--nested`自身が採取する（tier2-channel-c-mapped-folder
の汎用request/resultプリミティブ経由）。**Tier2は実機でTier1よりはるかに時間がかかることを
確認済み**（3秒間の採取要求に対し、実測でアイドルなプロセスは約110秒、CPUを使い切る
プロセスは約14分かかった。Windows Sandboxのネスト仮想化環境がハードウェアパフォーマンス
カウンタベースのCPUサンプリングに大きなオーバーヘッドを持ち込むと見られる、原因未確定）。
コマンドが応答しないように見えても、この待ち時間は想定内なので気長に待つこと。

同じカーネルセッションで名前付きパイプの作成イベント（`FileIOInit`キーワード）も採取し、
`NamedPipesCreated`として返す。`NtCreateNamedPipeFile`が上位フレームに出た場合、実際に
どのパイプ名が作成されているかを追加の手動調査（Process Explorer等）なしに確認できる
（作成呼び出しが完了に至っていない場合は空リストになる。それ自体も診断情報になる）。

**例：**

```powershell
.\srm.exe diag my-app --stacktrace --duration-ms 5000 --top 15
```

```json
{
  "Success": true,
  "ProcessId": 1234,
  "TotalSamples": 8419,
  "TopFrames": [
    { "Frame": "NtQueryInformationToken", "Count": 1001, "Percent": 11.9 },
    { "Frame": "KiSystemServiceUser", "Count": 404, "Percent": 4.8 }
  ],
  "NamedPipesCreated": [
    { "Name": "\\Device\\NamedPipe\\mypipe", "Count": 3 }
  ]
}
```

**`--watch`（継続的なプロセス監視、Tier2専用、tier2-process-monitor、DC-025）：**

```
srm diag <app> --watch [--interval-ms <ms>] [--count <n>]
```

`--duration-ms`の単発サンプリングと異なり、対象アプリのJob Object配下のプロセスツリー
全体（本体＋子プロセス）のCPU累積時間・スレッド数・ハンドル数・ワーキングセットを、
指定間隔（既定1000ms）で継続的にポーリングし、`--count`件（既定5件）のスナップショットを
順に出力する。ゲスト側は最新スナップショット1つだけを上書きし続け履歴を蓄積しない
（DC-013/DC-020と同じ「蓄積しないファイルペア」方針）ため、時系列が必要な場合は
ホスト側でこの複数回の出力を比較する。Tier2専用（`tier2.app_container`の値は問わない）。
Tier1は対象プロセスがホストから直接見えるため、この経路ではなく通常の`--duration-ms`を
繰り返し呼び出せば同等の情報が得られる。

**既知の制限**: `tier2.app_container: true`（既定）のTier2アプリに対しては、この
`--watch`だけでなく単発の`srm diag`自体も、実機でPID解決に失敗する既知の問題が
DC-025で見つかっている（原因未特定、`tier2.app_container: false`では問題なく動作する）。

**例：**

```powershell
.\srm.exe diag my-tier2-app --watch --interval-ms 500 --count 3
```

```json
{
  "requestId": "...",
  "success": true,
  "timestampUtc": "2026-07-15T02:27:27.96Z",
  "processes": [
    { "processId": 4540, "totalProcessorTimeMs": 31.25, "threadCount": 3, "handleCount": 77, "workingSetBytes": 4186112 },
    { "processId": 4600, "totalProcessorTimeMs": 31.25, "threadCount": 3, "handleCount": 71, "workingSetBytes": 3690496 }
  ]
}
```

---

### srm audit

```
srm audit <policy> [--scope <path>]... [--no-integrity-check]
```

`srm run`はポリシー外のファイル/ネットワークアクセスを拒否するが、`srm audit`は
**拒否せず**、対象アプリが実際にどのパス・どのホストへアクセスしようとしたかを
記録する（resource-access-audit-logging）。未知のアプリのポリシーを最小権限で
書く際の下調べや、信頼していないアプリ・AIエージェントの挙動をセキュリティリスク
評価目的で観測する用途を想定している。

**現時点ではTier1（AppContainer）ポリシーのみに対応**（Tier2は今後の拡張対象）。
管理者権限が必要（ETWカーネルセッションを使うため）。

**隔離しないことに関する重要な注意**: `srm audit`実行中は、

- `--scope`で指定したパス（省略時は`application.working_directory`）への
  **広い読み取りアクセス**を対象アプリに付与する（書き込みは許可しない）。
- ネットワークは`network.allow_hosts`の内容に関わらず**無制限に許可**する。

つまり`srm audit`はサンドボックスとして機能しない。信頼できないバイナリ・
エージェントを観測する場合は、VM境界のあるTier2ポリシーとの併用を推奨する
（Tier2対応が入るまでは、少なくとも使い捨て可能な環境で実行すること）。

`srm audit`は`srm run`と異なりフォアグラウンドで動き続け、対象アプリが終了するか
Ctrl+Cで停止するまでアクセス試行を記録し続ける。終了時にコンソールへサマリー
（許可済み/未許可の件数、未許可だったパス・接続先の一覧）を表示し、詳細は
`srm logs <policy>`で確認できる（`audit_fs`/`audit_net`エントリ）。

**例：**

```powershell
.\srm.exe audit unknown-tool --scope C:\Users\me\Downloads\unknown-tool
```

```
[警告] srm audit は隔離を行いません。...
監視を開始しました: unknown-tool (PID: 4821)。Ctrl+Cで停止します。

=== audit サマリー: unknown-tool ===
ファイルアクセス: 42件（許可済み 0件 / 未許可 42件）
ネットワーク接続: 3件（許可済み 0件 / 未許可 3件）
未許可のパス（srm runなら拒否されていたはず）:
  - C:\Users\me\Downloads\unknown-tool\config.json
  - C:\Users\me\Downloads\unknown-tool\cache\index.db
未許可の接続先（srm runなら拒否されていたはず）:
  - 203.0.113.10:443
詳細ログ: srm logs unknown-tool
```

（この例では`--scope`の範囲＝`allow_paths`が空のポリシーを想定しているため、
`--scope`内の全アクセスが「未許可」と判定されている。`--scope`の**外側**への
アクセスは、AppContainerのDACLにより拒否され観測できない場合があるが、
`C:\Program Files`や`C:\Windows\System32`配下など、Windowsの既定ACLで元々
ALL APPLICATION PACKAGESに読み取りが許可されている場所は`--scope`に含めて
いなくても普通に読めてしまい、観測はされる（`WouldBlock`のまま記録される）。
本当に「観測すらされず拒否」になるのは、他ユーザーのプロファイル等、真に
ACLで保護された場所に限られる。詳細は
`openspec/changes/2026-08-14-resource-access-audit-logging/design.md`の
Open Questionsと[DC-028](../views/records/DC-028.md)を参照。）

**既知の制限事項（2026-08-15実機検証で判明）**:

- 短命な子プロセス（数十ms程度で終了するコマンド等）のファイル/ネットワーク
  アクセスは記録されないことがある。`JobObjectManager`によるPID追跡が500ms
  間隔のポーリングのため、間に合わないケースがある。詳細は[DC-028](../views/records/DC-028.md)を参照。
- Windowsセキュリティの「コントロールされたフォルダー アクセス」が有効な
  環境では、`srm.exe`が保護ディレクトリのACL変更を試みた際にブロックされ、
  ハングしているように見えることがある。その場合は`srm.exe`を許可アプリに
  追加すること（`Add-MpPreference -ControlledFolderAccessAllowedApplications
  <srm.exeのパス>`）。

---

### srm transfer

```
srm transfer put <app> <host-source> <guest-dest>
srm transfer get <app> <guest-source>
```

tier2-file-transfer（DC-025）: 実行中のTier2ゲストとホスト間で、任意の1ファイルを
オンデマンド・双方向に転送する。Tier2専用（VM境界を越える転送のため）。転送元/転送先の
ゲスト側パスは、対象ポリシーの`filesystem.allow_paths`の範囲内でなければならない
（範囲外・パストラバーサルは拒否される）。

- `put`: ホスト側のファイルを、実行中のゲスト内の指定パスへコピーする。
- `get`: ゲスト内の指定パスのファイルを`outbox\transfer\<requestId>\`へ配置する。
  **`srm evidence`の既存の検疫フロー（`srm stop`時のスキャン→`srm evidence promote`）を
  経由する必要があり、ユーザーの作業ディレクトリへは自動反映されない**（DC-013の
  「スキャン結果を自動ゲートにしない」方針をここでも維持しているため）。

実機計測（100MB、DC-025）: `C:\srm\outbox`のようなマップフォルダへの転送は
約20MB/s、それ以外のallow_paths範囲内のゲストローカルな場所への転送は
約100〜150MB/s（マップフォルダ境界を横断する回数が少ない分速い）。分割転送・
進捗報告は現時点では実装していない（数百MB以上の巨大ファイルでは相応の待ち時間になる）。

**例：**

```powershell
.\srm.exe transfer put my-tier2-app C:\local\input.txt C:\srm\outbox\input.txt
.\srm.exe transfer get my-tier2-app C:\srm\outbox\result.txt
# get後は通常のTier2フローと同じく:
.\srm.exe stop my-tier2-app
.\srm.exe evidence list my-tier2-app
.\srm.exe evidence promote my-tier2-app <run-id> --dest C:\local\out
```

---

### srm cleanup-account

```
srm cleanup-account <policy>
```

restricted-account-app-isolation（`tier2.app_container: false`）用に作成された
専用ローカルアカウントを削除する。このアカウントは`srm stop`では削除されない
（AppContainerプロファイルを実行間で使い回す既存方針と揃えるため、design.md参照）。
ポリシーの利用をやめる際、このコマンドで明示的に片付ける。存在しないアカウント名を
指定してもエラーにはならない（冪等）。管理者権限が必要。

**例：**

```powershell
.\srm.exe cleanup-account my-tier2-app
```

---

## MCPサーバー（Srm.Mcp）

`Srm.Mcp.exe`（`bin\` 配下）は、AIエージェント（Claude Code等のMCPクライアント）が
SRMのサンドボックス実行をCLIのシェル呼び出しではなく構造化されたツール呼び出しで
オーケストレーションできるようにする、stdio経由のMCPサーバー。`srm.exe`本体とは別の
実行ファイルで、MCPクライアントが必要な時だけ起動・終了する薄いプロセスであり、
常駐サービスではない（DC-008/DC-017）。

### セットアップ

MCPクライアント側の設定ファイルに`Srm.Mcp.exe`のフルパスを登録する（クライアントに
よって設定ファイルの形式・場所は異なる。以下はstdio transportを使う一般的なMCP
クライアント設定の例）：

```json
{
  "mcpServers": {
    "srm": {
      "command": "C:\\path\\to\\srm-<version>\\bin\\Srm.Mcp.exe"
    }
  }
}
```

`srm.exe`と同じ`bin\`フォルダに配置しておくこと（`policies\`フォルダの自動探索や、
`evidence.max_outbox_bytes`監視プロセスの起動時に`srm.exe`を隣接パスから探すため）。
`srm run`/`srm stop`相当のツールは管理者権限が必要なため、MCPクライアント自体を
管理者権限で起動しておく必要がある。

### 公開されるツール

| ツール | 対応するCLIコマンド | 備考 |
|---|---|---|
| `srm_run` | `srm run` | |
| `srm_stop` | `srm stop` | |
| `srm_list` | `srm list` | |
| `srm_logs` | `srm logs` | |
| `srm_validate` | `srm validate` | |
| `srm_evidence_list`/`show`/`promote`/`reject` | `srm evidence ...` | |
| `send_key` / `send_mouse` / `screenshot` | （新規） | チャネルA。対話的なGUI操作 |
| `run_scenario` / `get_scenario_result` | （新規） | チャネルB。Tier2専用のオートパイロット |

### チャネルA: 対話的ライブ制御（`send_key`/`send_mouse`/`screenshot`）

`srm_run`で起動済みのアプリ（Tier1/Tier2どちらも対応）に対して、キー入力・
マウスクリック・画面キャプチャを行う。

- `send_key(app, text)`: `text`をUnicode文字としてそのまま送信する（Enter等の
  特殊キー送信は未対応）
- `send_mouse(app, x, y)`: 対象ウィンドウのクライアント領域内の相対座標`(x, y)`へ
  左クリックを送る
- `screenshot(app)`: 対象ウィンドウをPNGでキャプチャして返す（Tier2の場合は
  ゲストデスクトップ全体が写る。個別アプリのみのクロップは無い）

送信の直前に毎回、対象ウィンドウの実体（所有プロセス・実行ファイルパス・
ウィンドウクラス・座標範囲・フォアグラウンド状態）を再検証し、SRMが起動・管理
していないウィンドウには一切送信しない（`WindowGuard`、fail-closed）。拒否された
場合はエラーメッセージに理由が含まれる。

`screenshot`が返す画像はウィンドウ全体（タイトルバー・メニューバー等を含む）だが、
`send_mouse`の座標系はクライアント領域基準のため原点が一致しない。`screenshot`の
レスポンスに含まれる`client_offset`を、画像上で決めたピクセル座標から差し引いてから
`send_mouse`に渡すこと。

Tier2では、`send_key`/`send_mouse`とも実際の送信前に短い（数百ms程度の）待機が
発生する。ホスト側からは対象アプリがゲスト内で実際に入力フォーカスを持っているかを
直接検証できないため、送信前にゲスト内`--nested`へフォーカス要求を出し、ゲスト側で
確実にフォーカスを設定・確認してから送信する（`tier2-channel-a-focus-guarantee`）。
ゲスト側でフォーカス設定に失敗した場合、またはこの確認がタイムアウトした場合は
送信されずエラーになる。

### チャネルB: オートパイロット（`run_scenario`/`get_scenario_result`、Tier2専用）

対話的に1手ずつ送るのではなく、手順の並び（シナリオ）をまとめて1回投入し、
ゲスト内で自律的に最後まで実行させる。host↔guest間の通信が一時的に不安定でも
投入後は完走できる。

```jsonc
// run_scenarioの steps 引数の例
[
  { "type": "send_key", "text": "Hello, world!" },
  { "type": "screenshot", "label": "after-typing" },
  { "type": "send_mouse", "x": 100, "y": 20 }
]
```

`run_scenario`は投入するだけで即座に返る（非同期）。`get_scenario_result(app,
waitSeconds)`で結果を取得する（`waitSeconds`を指定すると、完了するかタイムアウト
するまでポーリング待機する）。`screenshot`ステップの画像は`outbox`経由で既存の
evidence検疫パイプラインに乗るため、`srm stop`後に`srm evidence show`/`promote`で
確認・取り出せる。

チャネルA/BのTier2部分は2026-07-12にWindows実機（Hyper-V/Windows Sandbox有効）で
検証済み（`TESTING.md`「MCPサーバー実機テスト」9.1〜9.7節）。予期しない拒否や
エラーが出た場合は`srm_logs`で`guard_rejected`イベントを確認するとともに、
`records/DC-017.yaml`・`records/DC-019.yaml`のreview_triggerを参照すること。

### チャネルD: サンドボックス内エージェントからホスト側MCPサーバーを使う（`mcp.allow_servers`）

チャネルA/B/CがホストからゲストへのAI操作用チャネルなのに対し、チャネルDは逆方向
（`srm run`で起動されるサンドボックス内エージェント自身が、ホスト側で動く実MCP
サーバーを使う）。ポリシーの`mcp.allow_servers`にサーバーを1件以上宣言すると、
`srm run`は自動的に以下を起動する：

- ホスト側`srm.exe --mcp-bridge-host`（内部モード。`--quota-monitor`と同じ
  パターンで`srm run`自身が起動する。実MCPサーバーをサブプロセスとして保持し、
  `allow_tools`の許可判定・監査ログ記録を行う）
- ゲスト側`Srm.McpBridgeGuest.exe`（サンドボックス内エージェント自身のMCP
  クライアント設定がこれを指すよう構成しておく。エージェント視点では通常の
  stdio MCPサーバーに見えるが、実体はcontrolフォルダ経由でホスト側へ中継する
  プロキシ）

サンドボックス内エージェント自身のMCPクライアント設定（`.mcp.json`等、エージェント
ごとに形式が異なる）に、`Srm.McpBridgeGuest.exe`をコマンドとして指すサーバー定義を
追加しておく必要がある（srmはエージェントごとの設定形式を自動生成しない）。Tier1では
`srm.exe`と同じフォルダ、Tier2ではゲスト内`C:\srm\tooling\`配下に配置される。

```yaml
mcp:
  allow_servers:
    - name: github
      command: "C:\\tools\\github-mcp-server.exe"
      allow_tools: ["list_issues", "get_pr"]
```

`allow_tools`を省略したサーバーは全ツールを許可する。許可・拒否を問わずすべての
呼び出しは通常のログ（`srm logs`）に記録される。Tier1・Tier2どちらも実機で
動作確認済み（[DC-024](../views/records/DC-024.md)参照。詳細な設計・
検証結果は`openspec/changes/channel-d-guest-mcp-bridge/`）。

---

## ポリシーファイルリファレンス

ポリシーファイルは `policies\<name>.yaml` に配置する。ファイル名（`.yaml` を除いた部分）がアプリ名として使われる。

### 全フィールド

```yaml
name: <string>          # 必須。アプリを識別する名前（ファイル名と一致させること）
description: <string>   # 任意。説明文

tier: 1                 # 必須。1 = AppContainer（Home含む全エディション対応）
                        #        2 = Windows Sandbox（Pro/Enterprise以上限定。要事前有効化）

variables:              # 任意。ポリシー内で %VAR% として参照できるユーザー定義変数
  MY_DIR: "%USERPROFILE%\\Projects"

application:
  executable: <string>  # 必須。実行ファイルのフルパス（%VAR% 展開あり）
  arguments: <string>   # 任意。コマンドライン引数
  working_directory: <string>  # 任意。作業ディレクトリ

filesystem:
  allow_paths:
    - path: <string>    # 必須。許可するパス（%VAR% 展開あり）
      access: r|rw      # 必須。r = 読み取り専用, rw = 読み書き

network:
  allow_hosts:          # アウトバウンドを許可するホスト名のリスト
    - "api.example.com"
    - "*.example.com"   # ワイルドカードは apex ドメインとして解決（v0.1）

process:
  allow_child_processes: true   # 子プロセスを許可するか
  max_processes: 10             # Job Object 内の最大プロセス数（1以上）

logging:
  level: debug|info|warn|error  # ログレベル
  retention_days: 7             # ログ保持日数（1以上）

provision:               # 任意。tier: 2 専用（tier: 1 で指定するとバリデーションエラー）
  toolchain:              # ホスト側に事前キャッシュしたツールチェーンの宣言
    - name: node          # %ProgramData%\SRM\toolchain-cache\<name>\<version>\*.zip を
      version: "20.11.0"  # 事前配置しておく必要がある（srmは取得・バージョン解決を代行しない）
  steps:                  # ゲスト内で実行する手順（この2種類のみ対応）
    - "unzip: node"       # toolchainのzipをゲスト内ローカルへ展開しPATHへ追加
    - "run: npm ci --offline"  # cmd.exe経由でコマンド実行。非ゼロ終了で全体を失敗させる
  network: false          # provisionフェーズのネットワーク許可（既定オフライン）

evidence:                 # 任意。tier: 2 専用
  source_path: <string>   # 任意。参考用（現状バリデーション・実行には使用しない）
  retention_days: 7       # 検疫データの保持日数（1以上、既定7）。Promoted/Rejectedは対象外
  max_outbox_bytes: 104857600  # 任意。指定時のみoutboxクォータ監視を有効化し、超過で早期終了

tier2:                    # 任意。tier: 2 専用
  app_container: true     # 既定true。ゲスト内ネスト実行をAppContainerでラップするか
                           # （DC-010の既定動作）。falseにするとAppContainerを経由せず、
                           # 代わりに専用ローカルアカウント＋Low Integrity Level（書き込み
                           # バリア）による起動になる（DC-016でAppContainerと非互換と
                           # 判明したアプリ向け、restricted-account-app-isolation、
                           # [DC-023](../views/records/DC-023.md)）。allow_pathsフォルダは
                           # Low ILラベルを付与した上で専用アカウントへ書き込み権を付与し、
                           # そのアカウントはLow整合性レベルへ引き下げたトークンで起動される
                           # ため、allow_paths以外のMedium IL以上のオブジェクトへの書き込みは
                           # OSのMandatory Integrity Controlにより拒否される。ただし読み取りは
                           # 制限しない（書き込みバリアのみ、AppContainer相当の隔離ではない）
                           # 点に注意。srm validateが警告を表示する

mcp:                      # 任意。tier: 1/2 両対応（Channel D）
  allow_servers:           # サンドボックス内エージェントが呼び出せるホスト側MCPサーバー
    - name: github          # 必須。ゲスト側スタブがtools/call時にどのサーバーを指すか
                             # 判別するための識別名（実サーバーとの通信自体には出てこない）
      command: <string>     # 必須。ホスト側で実行する実MCPサーバーのコマンド
      args: <string>        # 任意。コマンドライン引数（スペース区切り）
      allow_tools:           # 任意。省略時はこのサーバーの全ツールを許可する
        - list_issues        # 明示的に許可した名前のツール以外はtools/call/tools/listから
                              # 除外される（specs/guest-mcp-server-allowlist参照）
```

### 環境変数展開

`%VAR%` 形式でシステム環境変数とユーザー定義変数（`variables:` セクション）を展開できる。

よく使う変数：

| 変数 | 展開例 |
|---|---|
| `%USERPROFILE%` | `C:\Users\username` |
| `%APPDATA%` | `C:\Users\username\AppData\Roaming` |
| `%LOCALAPPDATA%` | `C:\Users\username\AppData\Local` |
| `%TEMP%` | `C:\Users\username\AppData\Local\Temp` |
| `%WINDIR%` | `C:\Windows` |
| `%PROGRAMFILES%` | `C:\Program Files` |

---

## 整合性チェック

SRM はポリシーファイルの改ざん検出のため、SHA256 サイドカーファイル（`<name>.yaml.sha256`）を使って整合性を検証する。

**ワークフロー：**

```
ポリシー作成 → srm validate --sign → srm run（自動検証）
ポリシー変更 → srm validate --sign → srm run（自動検証）
```

サイドカーが存在しない場合や内容が一致しない場合、`srm run` はエラーで終了する。開発・テスト時は `--no-integrity-check` で検証をスキップできる。

---

## ネットワークフィルタリング

`srm run` 実行時、WFP（Windows Filtering Platform）を使って AppContainer のアウトバウンド通信を制御する。

- **デフォルト**: すべてのアウトバウンドをブロック
- **許可**: `allow_hosts` に列挙したホスト名を DNS 解決し、解決された IP アドレスへの通信のみ許可

```yaml
network:
  allow_hosts:
    - "api.anthropic.com"     # A/AAAA 両方解決して許可
    - "statsig.anthropic.com"
    - "*.example.com"         # apex ドメイン (example.com) として解決（v0.1仕様）
```

**注意**: フィルターは `srm run` 実行時に DNS 解決した結果の IP アドレスに対して設定される。起動後に対象ホストの IP が変わった場合は `srm stop` → `srm run` で再適用が必要。

`srm stop` 実行またはプロセス終了時にフィルターは自動で除去される。

**tier: 2 の場合**: Windows SandboxはHyper-V Firewallによるゲスト通信フィルタに対応していない
（実機検証済み、[DC-015](../views/records/DC-015.md)）ため、ホスト側ではなくゲスト内で
上記と全く同じ仕組み（AppContainer SIDベースのWFP）を使う。`.wsb`側の`Networking`設定は
VM全体のオン/オフ（`allow_hosts`かprovisioningのネットワークが必要な場合のみ有効化）という
粗い制御のみを担い、ホスト/ドメイン単位の細かいallow/denyはゲスト内フィルタが行う。

`tier2.app_container: false`の場合、AppContainer SIDの代わりに
restricted-account-app-isolationの専用アカウントSID（`FWPM_CONDITION_ALE_USER_ID`）を
条件にしたフィルタになる（[DC-022](../views/records/DC-022.md)・
[DC-023](../views/records/DC-023.md)）。同一アカウントで起動された子プロセスの通信も
同じフィルタでカバーされる（AppPath方式と異なりユーザーSID単位のため）。

---

## ログ

ログは JSON Lines 形式で `%ProgramData%\SRM\logs\<app>\YYYY-MM-DD.jsonl` に保存される。

**ログエントリの例：**

```json
{"timestamp":"2026-06-30T10:00:00.000+09:00","level":"info","message":"srm run 開始","data":{"policy":"claude-code","tier":1}}
{"timestamp":"2026-06-30T10:00:01.000+09:00","level":"info","message":"プロセス起動","data":{"pid":12345,"exe":"C:\\...\\node.exe"}}
```

`retention_days` で指定した日数より古いログファイルは次回の `srm run` 時に自動削除される。

---

## トラブルシューティング

### `[権限エラー] srm run/stop には管理者権限が必要です`

管理者権限で実行されていない。PowerShell を「管理者として実行」してから再試行する。`srm run`/`srm stop`
はこのチェックを最初に行うため、非管理者では他の処理（ACL・WFP操作）に進む前に即座にこのエラーで
終了する。

> 以前は管理者権限が無い状態でWFPエンジンへの接続（`FwpmEngineOpen0`）を試みるとエラーにならず
> 無応答のままハングすることがあったが、このチェックにより解消されている。

### `整合性サイドカーが見つかりません`

```powershell
.\srm.exe validate <policy> --sign
```

でサイドカーを生成する。

### `ポリシーファイルが改ざんされています`

ポリシーを変更した後にサイドカーを更新していない。変更後は必ず `srm validate <policy> --sign` を実行する。

### `プロセスの起動に失敗しました`

- 実行ファイルのパスが正しいか確認する（`srm validate <policy>` で展開後のパスを確認できる）
- AppContainer SID に対してファイルへのアクセス権が付与できているか確認する
- 管理者権限で実行しているか確認する

### `Homeエディションではtier2非対応`

ポリシーの `tier: 2` を `tier: 1` に変更するか、Windows Pro / Enterprise 以上の環境で実行する。

### WFP フィルターが残留している

`srm stop` を実行する。それでも残る場合は管理者権限の PowerShell で：

```powershell
netsh wfp show filters
```

でフィルターを確認し、必要であれば PC を再起動する（WFP フィルターは再起動で消える）。

### `[provisionエラー] ツールチェーンキャッシュが見つかりません`

`provision.toolchain` に指定した `name`/`version` に対応する
`%ProgramData%\SRM\toolchain-cache\<name>\<version>\*.zip` が存在しない。srmはツールチェーンの
取得・バージョン解決を代行しないため、対象のポータブル配布物（zip）を事前に手動で配置する必要がある。

### `[Tier2起動警告] ゲスト内での起動確認(ready.signal)がタイムアウトしました`

Windows Sandboxの起動自体には成功しているが、ゲスト内での準備完了通知が2分以内に届かなかった。
`srm list` で状態を確認しつつ、Sandboxウィンドウが実際に開いているか確認する。開いていない場合は
Windows Sandbox機能が正しく有効化されているか（[セットアップ](#tier2windows-sandboxを使う場合の追加セットアップ)参照）を確認する。
