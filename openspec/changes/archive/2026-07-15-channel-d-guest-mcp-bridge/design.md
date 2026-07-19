## Context

Every existing guest↔host channel is host-initiated: Channel A (`send_key`/
`send_mouse`/`screenshot`) and Channel B (`run_scenario`) are driven by an
MCP tool call from `Srm.Mcp.exe` on the host; the sandboxed guest only
responds. This change is the first *guest-initiated* channel — the
sandboxed agent decides, at a time the host cannot predict, that it wants
to call a host-side MCP tool. That changes what needs to be watching on the
host side: for Channel A/B, the host is already the active party (making
the MCP tool call); here, something on the host must be *waiting* for the
guest to write a request.

Two existing precedents matter for this design:
- Channel A/B/C (via `SandboxControlChannel`) and DC-013/DC-020/021 have all
  converged on the same idiom — request/result JSON files, `requestId`-based
  overwrite (not accumulate), polling with a timeout — precisely because
  every attempt to introduce a different IPC mechanism has hit a real-machine
  wall (Hyper-V Firewall couldn't filter Tier2 network per DC-011/DC-015;
  Hyper-V Sockets PoC never got attempted before Channel C's redesign; named
  pipes are the specific mechanism DC-016 spent weeks discovering is broken
  under an AppContainer token for Bun-based processes).
- `RunOperation.SpawnQuotaMonitor` (`src/Srm.Runtime/Operations/RunOperation.cs`)
  already establishes the pattern of `srm run` launching a second,
  independent `srm.exe` background process in a special internal mode
  (`--quota-monitor`) to watch something continuously on the host side, for
  Tier2. This is the template for this change's host-side watcher, and
  notably is *not* Tier2-specific in its mechanism (it just happens to only
  be used for Tier2's outbox today) — the same launch pattern works
  unchanged for Tier1.

## Goals / Non-Goals

**Goals:**
- Let the sandboxed agent call host-side MCP tools through a mediated,
  policy-allow-listed, audited path, for both Tier1 and Tier2.
- Reuse the proven request/result JSON polling idiom rather than
  introducing a new IPC mechanism — extending it to a guest-initiated
  direction, not replacing it.
- Work from plain CLI usage (`srm run` alone) without requiring
  `Srm.Mcp.exe` to be running, consistent with DC-008/DC-017's
  non-resident-daemon principle.

**Non-Goals:**
- Full MCP protocol fidelity (streaming/progress notifications, resource
  subscriptions, sampling) — this design covers request/response tool calls
  (`tools/list`, `tools/call`) only. Notification-heavy MCP features don't
  fit a polling idiom well and are out of scope until a concrete need
  appears.
- Implementation (this change is design-only, matching this project's
  established proposal→design→specs→tasks convention before code).
- Changing `Srm.Mcp.exe` itself or Channel A/B/C's own code — this change
  only reuses their *idiom*, not their implementation, and does not depend
  on `tier2-channel-c-mapped-folder` (not yet implemented).

## Decisions

### Decision: request/result JSON polling over a shared control folder, not a new IPC mechanism

For Tier2, reuse the existing Windows Sandbox MappedFolder mechanism
(`SandboxControlChannel`'s `control/` folder) — add `mcp-request-<n>.json` /
`mcp-result-<n>.json` following the same `requestId`-based overwrite pattern
as `focus-request.json`/`diag-request.json`. For Tier1 (no VM boundary),
use a plain ACL'd folder: `AclManager.GrantAccess` already grants an
AppContainer SID read/write access to arbitrary folders (that's exactly
what `filesystem.allow_paths` does today), so no new ACL primitive is
needed — just a dedicated control folder granted the same way.

**Alternatives considered:**
- A named pipe SRM creates itself with an explicit DACL for the AppContainer
  SID (Tier1 only) — rejected for this design, even though DC-016's failure
  was specifically about *Bun* creating the pipe without an
  AppContainer-compatible security descriptor (not about named pipes being
  categorically broken under AppContainer). Rejected because: (a) it would
  still need its own real-machine PoC before trusting it, given how deep and
  surprising DC-016's investigation turned out to be for anything touching
  AppContainer + named pipes on this codebase; (b) it would mean Tier1 and
  Tier2 use two different transports for the same logical channel, adding
  implementation and maintenance surface for no capability gain, since the
  folder-based idiom already works for both tiers uniformly with zero new
  risk. If polling latency (see Risks) proves unacceptable in practice, this
  alternative should be revisited with its own dedicated PoC, not assumed.
- HvSocket (`AF_HYPERV`) — same rejection as Channel C's Decision 1: PoC
  ("can the guest open a custom service GUID") was never completed and
  isn't worth attempting now that the same request/result idiom keeps
  working for every other guest↔host need.

### Decision: a new `srm.exe --mcp-bridge-host` internal mode, spawned by `srm run` itself

Add `RunOperation.SpawnMcpBridgeHost` mirroring `SpawnQuotaMonitor` exactly:
resolve `srm.exe` via `Path.Combine(AppContext.BaseDirectory, "srm.exe")`
(works whether the caller is `Srm.Cli` or `Srm.Mcp`), launch it with
`ProcessStartInfo` + `--mcp-bridge-host --control-dir <dir> --policy <path>`
(no window, non-blocking `Process.Start`). `srm run` calls this whenever the
policy's `mcp.allow_servers` is non-empty, for both Tier1 and Tier2 — same
codepath, only `--control-dir` differs (Tier1: the new ACL'd folder;
Tier2: the existing MappedFolder `control/` path).

This background process is what actually spawns and owns the real
host-side MCP server processes declared in `mcp.allow_servers` (one
instance per declared server, for the lifetime of the sandboxed app's run),
polls the control folder for `mcp-request-*.json`, enforces the
`allow_tools` filter, forwards allowed calls to the real server's own
stdio, and writes `mcp-result-*.json`.

**Alternatives considered:**
- Fold this into `Srm.Mcp.exe` — rejected: would make Channel D only work
  when the user happens to be driving `srm` through an MCP client, breaking
  plain `srm run` CLI usage (this project's primary interface; `Srm.Mcp` is
  explicitly the optional, non-resident control plane per DC-008/DC-017).
- Fold this into the existing quota-monitor process instead of a new mode —
  rejected: quota-monitor's lifecycle (Tier2-only, tied to outbox size) and
  Channel D's lifecycle (both tiers, tied to MCP server processes) are
  different enough that sharing one process would conflate two unrelated
  responsibilities for no benefit; a second background process is cheap
  (matches the existing precedent of `srm run` already spawning
  quota-monitor as a *second* independent process rather than
  overloading one).

### Decision: a guest-side stdio stub process is the guest's actual MCP client target

The sandboxed agent's own MCP client config (e.g. Claude Code's
`.mcp.json` equivalent, provisioned into the sandbox alongside the app
itself) points at a new small stdio MCP server binary
(`Srm.McpBridgeGuest.exe` or similar) that runs *inside* the same
Tier1/Tier2 confinement as the agent. It translates each JSON-RPC request
arriving on its stdin into a `mcp-request-<n>.json` write + poll for the
matching `mcp-result-<n>.json`, then writes the JSON-RPC response to
stdout. It needs no privileges beyond read/write on the control folder
(already granted the same way as any other `allow_paths` entry) and execute
access to itself (`AclManager.GrantExecuteAccess`, no new mechanism).

**Alternatives considered:**
- Have the agent write request files directly (skip the stub) — rejected:
  every MCP client speaks stdio JSON-RPC to a subprocess it manages; making
  the agent's own code aware of SRM's file-polling protocol would require
  changing the agent itself (out of scope and not generally possible for
  third-party agents), whereas presenting a normal-looking stdio MCP server
  requires no agent-side changes at all.

### Decision: policy schema `mcp.allow_servers` with optional per-server `allow_tools`

```yaml
mcp:
  allow_servers:
    - name: github
      command: "C:\\path\\to\\github-mcp-server.exe"
      args: "--flag value"
      allow_tools: ["list_issues", "get_pr"]   # optional; omit = all tools on this server allowed
```

Mirrors `network.allow_hosts`/`filesystem.allow_paths`'s shape and spirit
(explicit allow-list, deny by default). The host-side broker
(`--mcp-bridge-host`) is the enforcement point, not the guest-side stub:
it intercepts `tools/call` for tool names outside `allow_tools` and returns
an MCP-level error without forwarding to the real server, and filters
`tools/list` responses to only advertise allowed tools. Enforcing on the
host side (not the guest-side stub) means a compromised or buggy guest-side
stub cannot bypass the allow-list — the real server process only ever
receives calls the host broker has already approved.

**Alternatives considered:**
- Enforce only at the server level (no `allow_tools`) — rejected as the
  primary design: rubber-stamping an entire server when the agent only
  needs one tool from it is a larger blast radius than necessary
  (`allow_tools` is optional, so server-level-only allow-listing remains
  available as the simple case).

## Risks / Trade-offs

- **[Risk]** Polling-based request/response adds latency (matches Channel
  A/B/C's existing ~250ms-class polling interval) that may be too slow for
  MCP clients with tight per-call timeouts → **Mitigation**: this is an
  accepted, already-proven trade-off shared with every other guest↔host
  channel in this project; if a specific MCP tool's latency requirement
  can't tolerate it, that's a signal for a future, narrower low-latency
  channel (analogous to Channel A vs Channel B/C's differing needs), not a
  reason to abandon the proven idiom here.
- **[Risk]** MCP tool arguments/results can smuggle exfiltration payloads
  even through an "allowed" tool (e.g. a permitted GitHub tool's issue-title
  argument could carry sensitive data out) → **Mitigation**: out of scope
  for this design to solve generally (matches DC-013's own acceptance that
  quarantine/audit, not prevention, is the practical bar); the audit
  logging requirement (every call logged via `StructuredLogger`) is the
  primary control, consistent with how this project treats other
  hard-to-fully-prevent risks.
- **[Risk]** Tier1 currently has no persistent host-side process during a
  plain `srm run` — this change adds one (`--mcp-bridge-host`) →
  **Mitigation**: directly mirrors the already-shipped, already-tested
  `--quota-monitor` pattern; no new category of risk, just a second
  instance of an existing one.
- **[Trade-off]** One host MCP server process per declared server per
  sandboxed app run (not shared across concurrent runs) — simpler
  isolation/audit story at the cost of duplicate server processes if
  multiple sandboxed apps use the same server concurrently; acceptable
  given this project's general preference for per-app isolation (mirrors
  per-policy AppContainer SIDs, per-policy WFP sessions).

## Migration Plan

New capability; no existing behavior changes. Rollback is deleting the new
files/policy field; nothing else depends on this channel existing.

## Open Questions

- Exact naming/location for the new guest-side stub binary and the
  `--mcp-bridge-host` mode's control-folder layout (`mcp-request-<n>.json`
  numbering vs a single overwritten pair with `requestId`, matching
  `focus-request.json`'s pattern) — left to tasks.md/implementation.
- Whether `Srm.PolicyEditor` (the WPF policy authoring tool) needs UI
  support for `mcp.allow_servers` — deferred; not required for the policy
  schema itself to work via hand-written YAML.
- Whether concurrent `tools/call` requests from the guest need to be
  supported (today's request/result idiom is single-outstanding-request
  per file pair, per Channel A's `focus-request.json` pattern) — if the
  agent's MCP client can have multiple in-flight calls, this may need
  `requestId`-keyed *multiple* file pairs rather than one, deferred to
  implementation once real usage patterns are known.
