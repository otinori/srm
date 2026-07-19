## Requirements

### Requirement: サンドボックス内エージェントはホスト側MCPサーバーをrequest/resultポーリング経由で呼び出せる
`mcp.allow_servers`が指定されたポリシーで`srm run`が実行された場合、システムは
サンドボックス内で動作するスタブMCPサーバー（ゲスト側スタブ）と、ホスト側で
実際のMCPサーバープロセスを保持・仲介する`srm.exe --mcp-bridge-host`を起動
しなければならない（MUST）。ゲスト側スタブが受け取ったJSON-RPCリクエストは、
Tier1では管理者側がACL付与した専用フォルダ、Tier2では既存のMappedFolder
（`SandboxControlChannel`の`control/`）を経由するrequest/resultファイルの
ポーリングによってホスト側へ中継されなければならない（MUST）。

#### Scenario: allow_serversを指定したTier1ポリシーでブリッジが起動する
- **WHEN** `mcp.allow_servers`に1件以上のサーバーを指定したtier: 1のポリシーで
  `srm run`が実行される
- **THEN** システムはゲスト側スタブとホスト側の`--mcp-bridge-host`プロセスの
  両方を起動する

#### Scenario: allow_serversを指定したTier2ポリシーでブリッジが起動する
- **WHEN** `mcp.allow_servers`に1件以上のサーバーを指定したtier: 2のポリシーで
  `srm run`が実行される
- **THEN** システムは既存のMappedFolder（`control/`）を経由してゲスト側スタブと
  ホスト側`--mcp-bridge-host`プロセスの両方を起動する

#### Scenario: allow_serversが空のポリシーではブリッジを起動しない
- **WHEN** `mcp.allow_servers`が指定されていない、または空のポリシーで
  `srm run`が実行される
- **THEN** システムはゲスト側スタブも`--mcp-bridge-host`も起動しない

### Requirement: ホスト側ブリッジはSrm.Mcp.exeの起動有無に関わらず動作する
`--mcp-bridge-host`は`srm run`単体（CLI直接実行）から自動的に起動されなければ
ならず（MUST）、`Srm.Mcp.exe`（MCPクライアント向け制御プレーン）が起動している
ことに依存してはならない（SHALL NOT）。

#### Scenario: Srm.Mcp.exeを起動せずにCLIから直接実行してもブリッジが機能する
- **WHEN** `Srm.Mcp.exe`を一切起動しない状態で、`mcp.allow_servers`を含む
  ポリシーを`srm.exe run`から直接実行する
- **THEN** ゲスト側スタブとホスト側`--mcp-bridge-host`が正常に起動し、
  ホスト側MCPサーバーの呼び出しが機能する

### Requirement: request/resultファイルは蓄積せずrequestIdベースで上書きする
Channel A/B・DC-013・DC-020/021が確立した既存のidiomに従い、
`mcp-request.json`/`mcp-result.json`（または同等のペア）は呼び出し回数に応じて
新規ファイルが増加してはならず（SHALL NOT）、`requestId`の一致によって
対応するリクエスト/レスポンスを判別しなければならない（MUST）。

#### Scenario: 複数回のツール呼び出しがファイルを蓄積させない
- **WHEN** 同一のサンドボックス実行内でゲスト側エージェントが複数回MCPツールを
  呼び出す
- **THEN** request/resultファイルはそれぞれ1ファイル（または少数の固定ファイル）
  のまま上書きされ続け、呼び出し回数に応じてファイル数が増加しない
