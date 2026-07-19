## Requirements

### Requirement: ポリシーで宣言されたホストMCPサーバーのみ到達可能
サンドボックス内エージェントは、ポリシーの`mcp.allow_servers`に明示的に
宣言されたホスト側MCPサーバーにのみ到達できなければならず（MUST）、
宣言されていないコマンドをホスト側ブリッジが起動・仲介してはならない
（SHALL NOT）。

#### Scenario: allow_serversに無いサーバーへは接続できない
- **WHEN** ゲスト側スタブが`mcp.allow_servers`に含まれないサーバー名/コマンドへの
  接続を試みる
- **THEN** ホスト側`--mcp-bridge-host`は当該サーバーの起動・仲介を拒否し、
  エラーをゲスト側スタブへ返す

### Requirement: allow_toolsを指定したサーバーはツール単位で許可リストを適用する
サーバーエントリに`allow_tools`が指定されている場合、ホスト側ブリッジは
`tools/call`のツール名が`allow_tools`に含まれない呼び出しを実サーバーへ
転送してはならず（SHALL NOT）、MCPレベルのエラーを返さなければならない
（MUST）。`allow_tools`が省略されたサーバーは、そのサーバーが提供する
全ツールを許可する。

#### Scenario: allow_tools外のツール呼び出しは実サーバーへ転送されない
- **WHEN** `allow_tools: ["list_issues"]`を指定したサーバーに対し、
  ゲスト側エージェントが`list_issues`以外のツール（例: `delete_repo`）を
  呼び出そうとする
- **THEN** ホスト側ブリッジは当該呼び出しを実サーバーへ転送せず、
  許可されていないツールである旨のエラーを返す

#### Scenario: allow_tools内のツール呼び出しは実サーバーへ転送される
- **WHEN** `allow_tools: ["list_issues"]`を指定したサーバーに対し、
  ゲスト側エージェントが`list_issues`を呼び出す
- **THEN** ホスト側ブリッジは当該呼び出しを実サーバーへ転送し、結果を
  ゲスト側スタブへ返す

#### Scenario: allow_tools省略時は全ツールが許可される
- **WHEN** `allow_tools`を省略したサーバーエントリに対し、ゲスト側エージェントが
  そのサーバーの任意のツールを呼び出す
- **THEN** ホスト側ブリッジは当該呼び出しを実サーバーへ転送する

### Requirement: tools/list応答は許可されたツールのみに絞り込む
`allow_tools`が指定されたサーバーについて、ホスト側ブリッジは実サーバーからの
`tools/list`応答を`allow_tools`に含まれるツールのみへ絞り込んだ上でゲスト側
スタブへ返さなければならない（MUST）。

#### Scenario: tools/listはallow_tools外のツールを含まない
- **WHEN** `allow_tools: ["list_issues"]`を指定したサーバーに対し、
  ゲスト側エージェントが`tools/list`を呼び出す
- **THEN** 返される一覧には`list_issues`のみが含まれ、実サーバーが提供する他の
  ツールは含まれない

### Requirement: すべてのブリッジ呼び出しを構造化ログに記録する
ホスト側ブリッジは、許可・拒否を問わずすべての`tools/call`呼び出しを、
既存の`StructuredLogger`（JSON Lines）で記録しなければならない（MUST）。
ログにはツール名・許可/拒否の結果・対象サーバー名を含めなければならない
（MUST）。

#### Scenario: 許可された呼び出しがログに記録される
- **WHEN** allow_tools内のツールが正常に呼び出される
- **THEN** `%ProgramData%\SRM\logs\<app>\`配下のログに、ツール名・サーバー名・
  許可結果を含むエントリが記録される

#### Scenario: 拒否された呼び出しもログに記録される
- **WHEN** allow_tools外のツール、またはallow_servers外のサーバーへの呼び出しが
  拒否される
- **THEN** 拒否された呼び出しについても同様にツール名・サーバー名・拒否結果を
  含むエントリがログに記録される
