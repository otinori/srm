## 参照レコード

- [DC-010](../../../records/DC-010.yaml) — Tier2実行アーキテクチャ（多層防御・srm.exeネストモード・ACL事前付与）
- [DC-011](../../../records/DC-011.yaml) — Tier2ネットワーク制御（Sandbox境界を正、ホスト/ドメイン単位）
- [DC-012](../../../records/DC-012.yaml) — Tier2 provisioning方針（ホスト事前キャッシュ・既定オフライン）
- [DC-013](../../../records/DC-013.yaml) — outbox検疫〜昇格パイプライン
- [DC-002](../../../records/DC-002.yaml) — Tier1/Tier2二段構えの原方針

## プロジェクト構造（追加分）

```
src/
├── Srm.PolicyEngine/
│   └── Models/PolicyModel.cs      # ProvisionPolicy / ToolchainEntry / EvidencePolicy 追加
├── Srm.Runtime/
│   ├── Sandbox/
│   │   ├── SandboxRunPaths.cs         # ホスト側 input/outbox/control ディレクトリレイアウト
│   │   ├── SandboxConfigGenerator.cs  # .wsb (XML) 生成
│   │   ├── SandboxControlChannel.cs   # ready/stop/stopped signal ファイルの読み書き
│   │   ├── SandboxAclPreparer.cs      # VM起動前のSID事前計算・ACL事前付与（DC-010）
│   │   └── SandboxLauncher.cs         # WindowsSandbox.exe 起動オーケストレーション
│   └── Evidence/
│       ├── EvidenceManifestBuilder.cs # SHA256マニフェスト生成
│       ├── EvidenceSanitizer.cs       # パストラバーサル/予約名/reparse point検査
│       └── EvidenceQuarantineStore.cs # 検疫保存・list/show/promote/reject
└── Srm.Cli/
    └── Commands/
        ├── RunCommand.cs      # Tier1/Tier2分岐、--nested 内部モード追加
        └── EvidenceCommand.cs # srm evidence list/show/promote/reject
```

## ポリシースキーマ拡張（Tier2専用・省略可）

```yaml
name: my-tier2-app
tier: 2

provision:
  toolchain:
    - name: node
      version: "20.11.0"
  steps:
    - "unzip: node"
    - "run: npm ci --offline"
  network: false      # DC-012: 既定オフライン。オンライン許可は明示オプトイン

evidence:
  source_path: "%USERPROFILE%\\SrmRuns\\my-tier2-app\\outbox"
  retention_days: 7   # DC-013: logging.retention_days と同じ思想
```

`network.allow_hosts` はTier1と共通のフィールドをそのまま使う（DC-011）。Tier2では
enforcement先がAppContainer+WFPではなくSandbox境界（Hyper-V Firewall）に変わるだけで、
スキーマ上の変更はない。

## host↔guest制御チャネル（DC-010）

`control/` はRWマップフォルダ。ホストとゲスト（`srm.exe run --nested`）が以下のシグナル
ファイルの存在で同期する。

```
control/
 ├─ policy.yaml     host→guest（起動前に書く、ゲストは読み取りのみ）
 ├─ ready.signal    guest→host（ネストしたsrm.exeがAppContainer起動に成功した）
 ├─ stop.signal     host→guest（srm stopで書く）
 └─ stopped.signal  guest→host（evidence flush後、ゲストが書いてVMを閉じる）
```

`SandboxControlChannel` はこれらのファイルの存在・内容をポーリングで監視するだけの
薄いクラスとして実装する（named pipeやソケットのようなライブブローカーは使わない。
DC-008のJSONファイルレジストリ方式と同じ「IPC・デーモンなし」の思想を踏襲）。

## Evidence検疫ストアのレイアウト（DC-013）

```
%ProgramData%\SRM\evidence\<app>\<runid>\
 ├─ manifest.json     # 各ファイルのSHA256/サイズ/相対パス + scan結果ラベル
 └─ files\...          # サニタイズ済みコピー
```

`srm evidence promote` はここから指定先へ**コピー**する（DC-013: moveではなくcopy、
検疫側の控えを監査証跡として残す）。

## 実装上の注意

- `Srm.Runtime`/`Srm.Cli`/`Srm.PolicyEngine` は `net8.0-windows` ターゲットだが、
  P/Invoke宣言のみを含む部分はLinux上でも `dotnet build` が通る（実行はできない）。
  本changeではこれを利用し、Evidence/Sandbox設定生成のような純粋なファイルI/O・
  文字列組み立てロジックはLinux上でユニットテストする。実際にWindows Sandboxを
  起動する`SandboxLauncher`本体・Hyper-V Firewall連携・Windows Defender呼び出しは
  Windows実機でのみ検証できる
- Evidenceサブシステムは意図的にWindows専用APIに依存させない（`System.IO`/
  `System.Security.Cryptography`のみ）。これによりCIやこの開発環境（Linux）でも
  実際にテストを実行でき、Windows実機依存の範囲を最小化できる
- `srm.exe --nested` は既存の`AppContainerLauncher`/`JobObjectManager`をそのまま
  呼び出す（DC-010: ゲスト内エージェントを新規開発しない）。ホスト専用ステップ
  （WFP登録・管理者権限チェック等）は `--nested` 指定時にスキップする
