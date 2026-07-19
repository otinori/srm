## Why

The AI agent running as the actual sandboxed workload (e.g. Claude Code under
`srm run`, inside Tier1 AppContainer or Tier2 Windows Sandbox) sometimes
needs to use MCP tools that only make sense running on the host (a GitHub
MCP server, a host filesystem MCP server, etc.), but today it has no way to
reach them without either raw network/process access to the host (which the
whole sandbox exists to prevent) or manual, unaudited exceptions. This is
the reverse direction from the existing `Srm.Mcp.exe` control plane (DC-017:
host-side MCP client → srm orchestrates the sandbox); this change is
guest-side MCP client → srm mediates access to host-side MCP servers.

## What Changes

- Add a new guest→host channel ("Channel D") that lets the sandboxed agent
  call host-side MCP servers through a mediated, policy-allow-listed,
  audited path — never raw network/process access to the host.
- Reuse the request/result JSON-over-shared-folder polling idiom already
  proven by Channel A/B (`SandboxControlChannel`), DC-013's outbox, and
  DC-020/021's `srm diag` request/result files, rather than introducing a
  new IPC mechanism (named pipes, sockets). For Tier1 this becomes an
  ACL-granted regular folder (no VM boundary to cross); for Tier2 it's the
  same Windows Sandbox MappedFolder mechanism Channel A/B/C already use.
  This is a deliberate continuation of this project's established
  risk-aversion to new IPC mechanisms (see
  `openspec/changes/tier2-channel-c-mapped-folder/design.md` Decision 1,
  and DC-016's unresolved named-pipe/AppContainer incompatibility, which
  this design avoids by construction rather than by working around it).
- Add a new host-side background watcher process
  (`srm.exe --mcp-bridge-host`), following the same internal-mode pattern
  already established by `--quota-monitor` (`RunCommand.RunQuotaMonitor`):
  `srm run` launches it automatically when the policy declares
  `mcp.allow_servers`, so this works from plain CLI usage and does not
  require `Srm.Mcp.exe` (the separate, MCP-client-facing control plane) to
  be running.
- Add a new guest-side stub process: a thin stdio MCP server the sandboxed
  agent's own MCP client config points at, which translates each JSON-RPC
  call into a request/result file round-trip against the shared control
  folder.
- Add a new policy YAML section (`mcp.allow_servers`) declaring which
  host-side MCP server commands are reachable, and optionally which tools
  on each are callable (`allow_tools`) — mirrors the allow-list shape and
  intent of `network.allow_hosts` and `filesystem.allow_paths`.
- Log every bridged tool call via the existing `StructuredLogger` (JSON
  Lines), for audit — matches this project's existing logging model.

## Capabilities

### New Capabilities
- `guest-mcp-bridge`: the transport — request/result JSON polling over a
  shared control folder (ACL'd folder for Tier1, MappedFolder for Tier2),
  the guest-side stdio stub, and the host-side `--mcp-bridge-host` watcher
  that relays to the real host MCP server process.
- `guest-mcp-server-allowlist`: the policy schema (`mcp.allow_servers`,
  per-server `allow_tools`) and the host-side broker's enforcement —
  rejecting connections to non-allow-listed servers and calls to
  non-allow-listed tools before they reach the real server.

### Modified Capabilities
(none — no existing `openspec/specs/` capability covers guest-initiated
host communication; Channel A/B are host-initiated and unaffected)

## Impact

- New `src/Srm.Runtime/Mcp/` (or similar) area: guest-side stdio stub
  process, host-side bridge watcher, shared request/result JSON contract
  for MCP JSON-RPC framing.
- `src/Srm.PolicyEngine/Models/PolicyModel.cs`: new `McpPolicy` /
  `mcp.allow_servers` section.
- `src/Srm.Cli/Commands/RunCommand.cs`: new `--mcp-bridge-host` internal
  mode (mirrors `--quota-monitor`'s `RunQuotaMonitor`); `srm run` launches
  it when `mcp.allow_servers` is non-empty, for both Tier1 and Tier2.
- `src/Srm.Runtime/Sandbox/SandboxControlChannel.cs` (Tier2) and a new
  Tier1-equivalent control-folder helper: add the `mcp-request-*.json` /
  `mcp-result-*.json` request/result pair, following the existing
  `focus-request`/`diag-request` idiom (requestId-based, overwritten not
  accumulated).
- `AclManager`: Tier1's control folder needs an ACL grant to the
  AppContainer SID, using existing `GrantAccess` machinery — no new ACL
  primitive needed.
- Does not touch `Srm.Mcp.exe` (the host-control-plane MCP server) — that
  remains a separate component or the opposite direction of this channel.
- Does not touch Channel A/B/C's own code — this change only reuses their
  proven *idiom*, not their implementation; no shared code dependency is
  introduced on `tier2-channel-c-mapped-folder` (not yet implemented) to
  avoid coupling this change's landing to that one's.
