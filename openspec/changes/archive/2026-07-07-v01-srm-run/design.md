## 参照スペック

- [spec-v01-srm-run](../../../docs/spec-v01-srm-run.yaml) — CLIコマンド・AppContainer・WFP・ログ
- [spec-policy-engine](../../../docs/spec-policy-engine.yaml) — YAMLローダー・バリデーター・環境変数展開
- [spec-policy-integrity](../../../docs/spec-policy-integrity.yaml) — SHA256サイドカー

## プロジェクト構造

```
dist/
├── Srm.sln
├── src/
│   ├── Srm.PolicyEngine/       # YAML読み込み・検証・ENV展開
│   ├── Srm.PolicyIntegrity/    # SHA256サイドカー
│   ├── Srm.Runtime/            # AppContainer・WFP・Job Object
│   └── Srm.Cli/                # srm コマンド（System.CommandLine）
├── policies/
│   └── claude-code.yaml        # サンプルポリシー
└── tests/
    ├── Srm.PolicyEngine.Tests/
    ├── Srm.PolicyIntegrity.Tests/
    └── Srm.Runtime.Tests/
```

## 実装上の注意

- Win32 P/Invoke 定義は `Srm.Runtime/Native/` に集約する
- AppContainer SID は `DeriveAppContainerSidFromAppContainerName` で生成（`userenv.dll`）
- WFP 操作は管理者権限が必要。`setup.ps1` で初期登録、`srm run/stop` で動的フィルター追加・削除
- Job Object は `CreateJobObject` → `SetInformationJobObject` (JOBOBJECT_EXTENDED_LIMIT_INFORMATION) → `AssignProcessToJobObject`
- ログパス: `%ProgramData%\SRM\logs\<app>\YYYY-MM-DD.jsonl`（`C:\SRM\` はProgramDataに変更推奨）
