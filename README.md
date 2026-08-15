---
name: "SRM"
description: "AppContainer / Windows Sandboxを組み合わせ、任意のWindowsアプリケーション・AIエージェントをポリシーベースのOSレベルサンドボックス内で実行するCLIツール"
background: "AIエージェント（Claude Codeのような、実際にファイル操作やコマンド実行を代行するAI）に作業を任せると、意図しないファイル削除や許可していない相手への通信といった事故が起こりうる。アプリ側のプログラムを一切書き換えずに、OS（Windows）標準の仕組みだけでこれを構造的に防ぎたい"
version: "0.1.0.0"
nature: "実験的"
status: "開発中"
goal: "任意のWindowsアプリケーション・AIエージェントを、ポリシーで宣言的に制限したOSレベルのサンドボックス（AppContainer / Windows Sandbox）内で実行し、ファイル・ネットワーク・プロセスへのアクセスを許可した範囲に限定する"
target_audience: "開発者"
language: ["C#", "PowerShell"]
frameworks: [".NET 8", "WPF"]
license: "MIT"
last_updated: 2026-07-19
---

# SRM — Secure Runtime Manager

---

> **これは実験的なリポジトリです。** 個人が興味・検証目的で作成しているもので、「完成」を
> ゴールとはしていません。機能追加・設計変更・後方互換性のない破壊的変更が予告なく行われる
> ことがあります。根本原因が未解決のまま残っている既知の問題（[DC-016](views/records/DC-016.md)
> 等）や、恒久的な制限として受け入れている項目（下記「既知の制限事項」参照）も含んでいます。
> 本番環境や重要な用途での利用は想定していません。

---

## SRMって何？（専門用語なしの説明）

- AIエージェント（Claude Codeのような、実際にファイル操作やコマンド実行を代行してくれるAI）は
  便利な一方で、「意図しないファイルを消してしまう」「許可していない相手に情報を送ってしまう」
  といった事故が起こり得ます。
- SRMは、そういったアプリやAIエージェントを、あらかじめ決めた**「立ち入っていい場所」「通信して
  いい相手」だけに閉じ込めて**動かすためのツールです。イメージとしては、鍵のかかった作業部屋を
  用意して、そこでだけ作業させるようなものです。
- 部屋の外に出ようとする操作（許可していないファイルへのアクセス、許可していない相手への通信）
  は、SRMがOS（Windows）の仕組みを使って強制的にブロックします。アプリ側のプログラムを一切
  書き換える必要はありません。
- 「どの場所を許可するか」「どの相手との通信を許可するか」は、`policies\`フォルダの設定ファイル
  （YAML、後述）1つで指定します。

初めて触る方は [manual/beginners-guide.md](manual/beginners-guide.md) から読むと、
このページ以降で出てくる用語（管理者権限・コマンドライン・YAMLファイルなど）を順番に理解
できます。以降のセクションはある程度PC操作に慣れている方向けの、技術的な内訳です。

## 概要

### 開発動機

AIエージェント（Claude Code のような、実際にファイル操作やコマンド実行を代行してくれるAI）に
作業を任せると、意図しないファイル削除や許可していない相手への通信といった事故が起こりうる。
アプリ側のプログラムを一切書き換えずに、OS（Windows）標準の仕組みだけでこれを構造的に防ぐ
手段が欲しかったため作成した。

### ゴール

任意のWindowsアプリケーション・AIエージェントを、ファイルシステム・ネットワーク・プロセスへの
アクセスをYAMLポリシーで宣言的に制限したサンドボックス（AppContainer / Windows Sandbox）の
内側で実行できるようにする。
開発作業におけるAppContainerと、 Windows Sandboxの可能性を実際に使ってみることで確認する。

### 何ができるのか

- **AppContainer 隔離（Tier 1）** — ファイルシステムアクセスをポリシーで指定したパスのみに制限する
- **WFP ネットワークフィルタ** — アウトバウンド通信を `allow_hosts` に列挙したホストのみ許可する
- **Job Object 管理** — 起動したプロセスと子プロセスを一括で終了できる
- **Windows Sandbox 隔離（Tier 2）** — より強い分離が必要な場合、VM境界の内側にさらに
  AppContainerを重ねた多層防御で実行する（Pro/Enterprise限定）。ホスト側で事前キャッシュした
  ツールチェーンのオフライン展開（`provision`）、実行結果の検疫〜人間による明示的な昇格
  （`srm evidence`）にも対応する
- **専用アカウント＋Low Integrity Level隔離（`tier2.app_container: false`）** — AppContainerと
  非互換なアプリ（下記「既知の制限事項」参照）向けのTier2代替経路。書き込みバリアのみを
  構造的に保証し、ネットワーク遮断はユーザーSIDベースのWFPフィルタで子プロセスまで含めて
  維持する
- **オンデマンドのファイル転送・プロセス監視（Tier2）** — 実行中のTier2ゲストに対し、
  `srm transfer put/get`でホスト⇄ゲスト間のファイルを随時やり取りし、`srm diag --watch`で
  プロセスツリーのリソース使用状況を継続的にポーリングできる
- **MCPサーバー（`Srm.Mcp.exe`）** — AIエージェント（Claude Code等）がCLIをシェル経由で
  叩くのではなく、構造化されたツール呼び出しでサンドボックス実行を直接オーケストレーション
  できる。常駐デーモンではなくMCPクライアントが必要な時だけ起動する薄いプロセス。
  `srm run/stop/list/logs/validate/evidence` 相当に加え、起動済みアプリへのキー・マウス
  入力送信やスクリーンショット取得（対象ウィンドウの実体を毎回再検証するfail-closedガード付き）、
  Tier2向けのテストシナリオ自動実行にも対応する。詳細は
  [manual/usage.md](manual/usage.md#mcpサーバーsrmmcp) を参照
- **サンドボックス内エージェントからホスト側MCPサーバーを利用（チャネルD）** — サンドボックス内
  で動くAIエージェント自身が、ポリシーで許可（`mcp.allow_servers`）したホスト側MCPサーバーを
  仲介経由で呼び出せる。ホスト側ブリッジが許可リストを強制し、全呼び出しを監査ログへ記録する

### 現在の状態

初期リリース `0.1.0.0` を公開済み（[CHANGELOG](CHANGELOG.md)参照）。既知の問題・恒久的な
制限事項は下記「既知の制限事項」を参照。

## 動作環境

### 前提条件

- 管理者権限の PowerShell（セットアップ・WFP操作に必要）
- **.NET ランタイムの別途インストールは不要**。配布される `srm.exe`/`Srm.Mcp.exe`/
  `Srm.PolicyEditor.exe`/`Srm.McpBridgeGuest.exe` はいずれも自己完結型（.NET 8、win-x64）
  ビルドで、実行に必要なランタイムを内包している
- （tier 2 = Windows Sandbox を使う場合のみ）Windows Sandbox 機能の有効化と再起動が別途必要。
  管理者権限の PowerShell で:

  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
  ```

  実行後、再起動が必要と案内されたら再起動する。
- （`srm diag --stacktrace` を使う場合のみ）初回実行時にMicrosoft公開シンボルサーバーへ
  アクセスするため、インターネット接続が必要（2回目以降はローカルキャッシュされる）。それ
  以外の全コマンドはオフラインで動作する
- （ソースからビルド・`dotnet run` で `Srm.PolicyEditor` を起動する場合のみ）.NET 8 SDK が
  別途必要。[Releases](../../releases) の配布物を使うだけであれば不要

### 対応プラットフォーム

Windows 10 / 11 Pro または Enterprise（Homeでは tier 2 非対応）。Windows 10は既にサポート
終了しておりSRMの正式ターゲットではないが、開発・検証は現状Windows 10機（Pro）で行っており
技術的には動作する。Windows 11が本来の主要ターゲット。

## 既知の制限事項

- **AppContainer非互換のCLIツールがある場合がある**（例: Bunでコンパイルされたバイナリ）。
  名前付きパイプを使う一部の処理がAppContainer内で完了せず無限リトライに陥ることがある
  （原因調査済み、srm側からの修正手段なし。[DC-016](views/records/DC-016.md)）。回避策として、
  該当アプリはTier2で`tier2.app_container: false`を指定する（専用アカウント＋Low Integrity
  Levelによる隔離に切り替わる）。なお`claude-code` CLIでこの症状が実際に確認されていたのは
  v2.1.197時点で、2026-08-15にv2.1.233で再検証したところ再現しなくなっていた
  （[DC-029](views/records/DC-029.md)。根本原因が修正されたと確定したわけではなく、
  最小限の非対話1回呼び出しでの確認に留まる点に注意）
- `tier2.app_container: false`は**書き込みバリアのみ**を保証し、読み取りは制限しない
  （AppContainer相当の隔離ではない）。`srm validate`が該当ポリシーに警告を表示する
- Tier2のネットワーク遮断は、VM境界そのものではなく**ゲスト内部のWFPエンジン**で行っている
  （Hyper-V FirewallがWindows Sandboxのゲスト通信を一切フィルタできないため）。実質的に
  Tier1と同じ信頼境界の強さ
- `allow_tools`はMCPツール名のみを検証し、引数の内容までは検証しない
- `srm diag --stacktrace`のTier2実行はTier1よりはるかに時間がかかる（実測で数分〜十数分）

詳細・その他の制限事項は [manual/usage.md](manual/usage.md) のトラブルシューティング節、
および `views/records/` 配下の DecisionRecord/InvestigationRecord を参照。

## インストール

[Releases](../../releases) から最新の `srm-<version>.zip` をダウンロードし、任意のフォルダに展開する。

```
srm-0.1.0.0/
  bin/
    srm.exe
    Srm.Mcp.exe
    Srm.PolicyEditor.exe
    Srm.McpBridgeGuest.exe
    （+ 依存DLL一式）
  policies/
    claude-code.yaml
    smoke-test.yaml
    notepad-test.yaml
  setup.ps1
  usage.md
```

管理者権限の PowerShell で初回セットアップを実行する：

```powershell
cd srm-0.1.0.0
.\setup.ps1
```

`%ProgramData%\SRM\logs` と `%ProgramData%\SRM\run` が作成され、同梱ポリシーの `.sha256` サイドカーが生成される。

## クイックスタート

`srm.exe` は `bin\` 配下にある。

```powershell
cd bin

# ポリシーの構文チェック
.\srm.exe validate smoke-test

# AppContainer 内で cmd.exe を実行
.\srm.exe run smoke-test

# AppContainer 内でメモ帳を起動（GUIアプリの動作確認用）
.\srm.exe validate notepad-test --sign
.\srm.exe run notepad-test

# 実行中のサンドボックスを一覧表示
.\srm.exe list

# 停止
.\srm.exe stop smoke-test
```

## 主な使い方

代表的なコマンドのみ記載する。詳細は [manual/usage.md](manual/usage.md) を参照。

| コマンド | 説明 |
|---|---|
| `srm run <policy>` | ポリシーに従ってアプリをサンドボックス内で起動する（tierに応じてAppContainerまたはWindows Sandbox） |
| `srm stop <app>` | 実行中のアプリとその子プロセスを終了する |
| `srm list` | 実行中のサンドボックスを一覧表示する |
| `srm logs <app>` | 構造化ログを表示する |
| `srm validate <policy>` | ポリシーファイルの構文と整合性を検証する |
| `srm evidence list/show/promote/reject <app>` | Tier2の検疫データ（outbox）を確認・昇格・却下する |
| `srm diag <app> [--stacktrace \| --watch]` | 実行中アプリのCPU時間・カーネルスタック・プロセス一覧を診断用にサンプリングする |
| `srm audit <policy> [--scope <path>]` | ブロックせずファイル/ネットワークへのアクセス試行を記録する（隔離はしない、Tier1専用） |
| `srm transfer put/get <app>` | 実行中のTier2ゲストとホスト間でファイルをオンデマンド転送する |
| `srm cleanup-account <policy>` | `tier2.app_container: false`用に作成された専用ローカルアカウントを削除する |

## 構成

```
srm/
├── src/                      # プロダクトコード（.NET 8）
│   ├── Srm.Cli/               # srm.exe 本体（CLIエントリポイント）
│   ├── Srm.Runtime/           # AppContainer / Windows Sandbox 実行基盤
│   ├── Srm.PolicyEngine/      # ポリシーYAMLの読み込み・検証
│   ├── Srm.PolicyIntegrity/   # SHA256サイドカーによる整合性検証
│   ├── Srm.Mcp/               # MCPサーバー（Srm.Mcp.exe）
│   ├── Srm.McpBridgeGuest/    # チャネルDのゲスト側ブリッジ
│   ├── Srm.PolicyEditor/      # ポリシー編集用WPFアプリ
│   └── Srm.Diagnostics.Kernel/ # ETWカーネルスタックサンプリング
├── tests/                    # 各プロジェクトの単体テスト
├── tools/                    # 開発用の補助ツール（GUIスモークテスト等）
├── policies/                 # サンプルポリシーYAML
├── manual/                   # 詳細な使い方ガイド（beginners-guide.md / usage.md）
├── openspec/                 # 仕様駆動開発（SDD）で使う変更提案・仕様
├── views/records/            # 設計判断・調査記録（DecisionRecord/InvestigationRecord）
├── setup.ps1                 # 初回セットアップスクリプト
└── Srm.sln
```

## ポリシーファイル

`policies/` フォルダに YAML ファイルを作成してアプリを定義する。

```yaml
name: my-app
tier: 1

application:
  executable: "C:\\path\\to\\app.exe"
  arguments: "--flag"
  working_directory: "%USERPROFILE%\\Projects"

filesystem:
  allow_paths:
    - path: "%USERPROFILE%\\Projects"
      access: rw

network:
  allow_hosts:
    - "api.example.com"

process:
  max_processes: 10

logging:
  level: info
  retention_days: 7
```

`tier: 2` にすると同じスキーマのままWindows Sandboxで実行される。`provision`（ホスト事前
キャッシュのツールチェーンをオフライン展開）・`evidence`（検疫のクォータ・保持期限）といった
Tier2専用セクションもあり、詳細は [manual/usage.md](manual/usage.md) を参照。

ポリシーを作成・変更したら整合性サイドカーを更新する：

```powershell
bin\srm.exe validate my-app --sign
```

YAMLを手書きせずネイティブのフォームで作成・編集したい場合は、`bin\Srm.PolicyEditor.exe`
（`srm-<version>.zip`に同梱）を実行するか、ソースから
`dotnet run --project src/Srm.PolicyEditor` で起動できる（ローカル専用のWPFアプリ）。
詳細は [src/Srm.PolicyEditor/README.md](src/Srm.PolicyEditor/README.md) を参照。

## ライセンス

MIT

## 謝辞（Third-party OSS）

SRMは以下のオープンソースソフトウェアを利用しています（配布物 `bin\` にバイナリとして
同梱されるもののみ記載。開発・テスト時限定の依存は含まない）。

| パッケージ | ライセンス | 用途 |
|---|---|---|
| [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) | Apache-2.0 | `Srm.Mcp.exe`/`Srm.McpBridgeGuest.exe`/チャネルDのMCPサーバー実装（公式C# SDK） |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) | MIT | MCPサーバーのホスティング基盤 |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | MIT | `srm.exe` のCLI引数解析 |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | MIT | ポリシーファイル（YAML）のパース |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) | MIT | `srm diag --stacktrace` のETWカーネルスタックサンプリング |
| [System.Drawing.Common](https://github.com/dotnet/runtime) | MIT | `screenshot` ツールのPNGエンコード |
| [System.Text.Json](https://github.com/dotnet/runtime) / [System.IO.Pipelines](https://github.com/dotnet/runtime) | MIT | ホスト⇄ゲスト間の各種JSONプロトコル |
| [.NET](https://github.com/dotnet/runtime) 自体（自己完結型ランタイム） | MIT | 実行基盤 |

各パッケージの正確なライセンス全文は、ビルド後の `bin\*.dll` に対応するNuGetパッケージの
公式配布元（上記リンク）を参照してください。
