## 1. ポリシースキーマ拡張（Srm.PolicyEngine）

- [x] 1.1 `ProvisionPolicy`（toolchain/steps/network）・`ToolchainEntry`・`EvidencePolicy`
      （source_path/retention_days）モデルクラスを `PolicyModel` に追加する
- [x] 1.2 `PolicyValidator` にバリデーションを追加する（tier1で`provision`が指定されたら
      エラー、`evidence.retention_days`は1以上、toolchainエントリは`name`必須）
- [x] 1.3 ユニットテストを追加する（`PolicyValidatorTests`/`PolicyLoaderTests`）

## 2. Evidence検疫パイプライン（Srm.Runtime.Evidence、DC-013）

- [x] 2.1 `EvidenceManifestBuilder` — ディレクトリを走査しSHA256/サイズ/相対パスの
      マニフェストを生成する
- [x] 2.2 `EvidenceSanitizer` — パストラバーサル・予約デバイス名・reparse pointを検出する
- [x] 2.3 `EvidenceQuarantineStore` — 検疫保存・`List`/`Show`/`Promote`（copy）/`Reject`を実装する
- [x] 2.4 保持期限（`retention_days`）を過ぎた未昇格の検疫データを削除する処理を実装する
- [x] 2.5 ユニットテストを追加する（すべてLinux上で実行可能な純粋ファイルI/O）

## 3. Sandbox実行基盤（Srm.Runtime.Sandbox、DC-010/DC-011）

- [x] 3.1 `SandboxRunPaths` — `input`/`outbox`/`control` のホスト側ディレクトリレイアウトを
      決定する
- [x] 3.2 `SandboxConfigGenerator` — `.wsb`（XML）を生成する（MappedFolders・Networking・
      LogonCommand・MemoryInMB）
- [x] 3.3 `SandboxControlChannel` — `ready.signal`/`stop.signal`/`stopped.signal` の
      読み書き・ポーリング待機を実装する
- [x] 3.4 `SandboxAclPreparer` — 対象ポリシーからAppContainer SIDを事前計算し、VM起動前に
      `outbox`等のホストパスへACLを事前付与する（既存`AclManager`/`AppContainerSidFactory`を再利用）
- [x] 3.5 `SandboxLauncher` — `.wsb`生成→ACL事前付与→`WindowsSandbox.exe`起動→
      `ready.signal`待機、のオーケストレーションを実装する
- [x] 3.6 ユニットテストを追加する（`SandboxRunPaths`/`SandboxConfigGenerator`/
      `SandboxControlChannel`はLinux上で実行可能。`SandboxAclPreparer`/`SandboxLauncher`は
      P/Invoke・実プロセス起動を含むためコンパイル確認のみ）

## 4. CLI統合（Srm.Cli）

- [x] 4.1 `RunCommand` のTier1ロジックをメソッドに切り出し、`policy.Tier`で
      Tier1/Tier2に分岐させる
- [x] 4.2 `--nested` 内部オプションを追加する（指定時はホスト専用ステップをスキップし、
      既存の`AppContainerLauncher`/`JobObjectManager`のみを実行、完了時に`ready.signal`を書く）
- [x] 4.3 `srm evidence list <app>` / `show <app> <runid>` / `promote <app> <runid> --dest <path>` /
      `reject <app> <runid>` を実装し `Program.cs` に登録する
- [x] 4.4 `srm stop` のTier2対応（`stop.signal`書き込み→`stopped.signal`待機→
      タイムアウト時は強制終了）を実装する

## 5. ビルド・テスト検証（Linux開発環境で可能な範囲）

- [x] 5.1 `dotnet build Srm.sln` が通ることを確認する（`Srm.PolicyEditor`はWindowsDesktop SDK
      依存のためLinux上でビルド不可、既知の制約として除外）
- [x] 5.2 `dotnet test` で新規ユニットテストがすべてパスすることを確認する

## 6. 実機確認要（Windows実機・まとめて実施）

> **2026-07-03時点の注記**: 6.1/6.2/6.9は実機確認済み。6.5/6.7/6.8はその後
> `StopTier2`への配線・`WindowsDefenderScanner`実装により追加実装・実機確認済み
> （検疫ACLの実バグも1件発見・修正）。6.6も追加実装・実機確認済み（`evidence.
> max_outbox_bytes`ポリシーフィールド＋detached監視プロセス）。6.4も追加実装・
> 実機確認済み（`Provisioner`＋ホスト側toolchain-cacheのROマウント）。6.3は
> 実機PoCの結果DC-011の前提（Hyper-V Firewall）が成立しないと判明したため、
> DC-011を改訂する[DC-015](../../../views/records/DC-015.md)を新たに記録し、
> ゲスト内WfpManager方式で実装・実機確認済み。これで6章全項目が実機確認済みと
> なった。詳細は`TESTING.md`参照。

- [x] 6.1 `srm run <tier2-policy>` でWindows Sandboxが起動し、ゲスト内で
      `srm.exe run --nested` がAppContainer化した対象アプリを起動できる
- [x] 6.2 ホストで事前付与したACLがマップフォルダ経由でゲストにも正しく反映される
      （DC-010のPoC項目）
- [x] 6.3 Hyper-V FirewallでWindows Sandboxのゲスト通信をホスト側からフィルタできる
      （DC-011のPoC項目。不可の場合はゲスト内WFP相当実装への切り替えを再検討）
      — 2026-07-03実機PoCで「Hyper-V VM Network Adapter拡張ACL」「vEthernet
      (Default Switch)へインターフェーススコープしたWindows Defender Firewall
      （Inbound/Outbound両方）」のいずれもWindows Sandboxのゲスト通信を一切
      フィルタできないと判明（前者はOSが変更自体を拒否、後者はルール自体は
      正しく適用されるが効果なし）。review_trigger通りゲスト内WFP相当実装
      （既存`WfpManager`をTier1と同じ形で`RunNested`から呼ぶ）に切り替え、
      [DC-015](../../../views/records/DC-015.md)としてDC-011を改訂・記録した
      上で実装・実機確認済み（許可ホストは成功、未許可ホストは`Bad access`で
      拒否されることをAppContainer内から確認）
- [x] 6.4 `provision`セクションのオフライン展開（unzip/PATH設定/npm ci --offline）が
      ゲスト内で動作する（2026-07-03実装・実機確認済み。ホスト側`%ProgramData%\SRM\
      toolchain-cache\<name>\<version>\*.zip`をROマウントし、ゲスト内`Provisioner`が
      `unzip:`/`run:`ステップを解釈・実行。ダミーツールチェーンで展開→PATH追加→
      `run:`経由でのPATH解決実行→対象アプリ起動、の一連の流れを確認）
- [x] 6.5 `stop.signal`→`stopped.signal`の正常終了フローでevidenceのflushが完了してから
      VMが閉じることを確認する（2026-07-03実装・実機確認済み。`StopTier2`が
      `EvidenceQuarantineStore.Quarantine()`を呼ぶよう配線）
- [x] 6.6 outboxへの書き込みに対するホスト側クォータ監視が実際に機能し、超過時に
      早期終了させられる（2026-07-03実装・実機確認済み。`evidence.max_outbox_bytes`
      指定時のみ`srm run --quota-monitor`をdetachedプロセスとして起動し、5秒間隔で
      ポーリング、閾値超過検出から約6秒で`stop.signal`を送出することを確認）
- [x] 6.7 Windows Defender (`MpCmdRun.exe`) によるスキャンが検疫パイプラインから
      正しく呼び出せる（2026-07-03実装・実機確認済み。`WindowsDefenderScanner`を
      新規実装し`manifest.json`の`scan_result`へ記録。合否ゲートにはしない）
- [x] 6.8 `srm evidence promote`で検疫ストアから指定先へのコピーが正しく行われる
      （2026-07-03実機確認済み。6.5配線後の実runから連続したフローで確認。
      検疫ストアのACLバグ（非管理者の`srm evidence`から書き込めない）も発見・修正）
- [x] 6.9 VM起動〜`ready.signal`までの体感時間が実用範囲に収まる
      （2026-07-03実機確認: 修正後は起動要求から`ready.signal`まで約11秒）
