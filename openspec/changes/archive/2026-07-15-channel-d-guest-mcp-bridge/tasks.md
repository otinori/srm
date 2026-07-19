## 1. Policy schema

- [x] 1.1 Add `McpPolicy`/`McpServerEntry` to `src/Srm.PolicyEngine/Models/PolicyModel.cs`
      (`mcp.allow_servers`: list of `{ name, command, args, allow_tools? }`),
      mirroring `NetworkPolicy`/`FilesystemPolicy`'s existing shape.
- [x] 1.2 Add `PolicyValidator` checks (`name`/`command` required,
      `allow_tools` entries non-empty) and unit tests in
      `tests/Srm.PolicyEngine.Tests` (6 new tests, 30/30 passing).

## 2. Control-folder request/result primitive

- [x] 2.1 Tier2: added `mcp-request.json`/`mcp-result.json` to
      `SandboxControlChannel` (`WriteMcpRequest`/`TryReadMcpRequest`/
      `WriteMcpResult`/`TryReadMcpResult`/`WaitForMcpResult`), following the
      exact pattern of `focus-request.json`/`focus-result.json`. **Note**:
      roles are reversed from focus/diag — the guest writes the request and
      waits for the result, the host reads the request and writes the
      result (documented in code comments; this is the first
      guest-initiated channel). Deliberately did *not* add an
      `onMcpRequestDetected` overload to `BlockUntilStop` — that loop is
      guest-side only (`RunNested`), but request detection for this channel
      needs to happen host-side (in the new `--mcp-bridge-host` process,
      task group 3), so it doesn't belong there.
- [x] 2.2 Tier1: **discovery — no new class needed.** `SandboxControlChannel`
      turned out to contain no Tier2/MappedFolder-specific logic at all
      (plain `File`/`Directory` I/O against whatever folder path it's given),
      so it's directly reusable for Tier1's control folder as-is. Added
      `Tier1ControlPaths` (mirrors `SandboxRunPaths`'s path convention,
      without Tier2's `runId` concept, since Tier1 apps are already
      one-instance-per-name like `srm-job-{app}`). The actual
      `AclManager.GrantAccess` wiring for this folder is deferred to task
      3.1 where it naturally belongs alongside spawning the bridge host.
- [x] 2.3 Resolved: kept single-outstanding-request (matching
      `focus-request.json`'s existing pattern) rather than adding
      `requestId`-keyed concurrent slots. Revisit if real usage shows the
      guest's MCP client issues overlapping `tools/call` requests before a
      prior one's result is read (round-trip tests added:
      `tests/Srm.Runtime.Tests/SandboxControlChannelTests.cs`, 6 new tests,
      34/34 passing).

## 3. Host-side bridge process (`--mcp-bridge-host`)

- [x] 3.1 Added `RunOperation.SpawnMcpBridgeHost`, mirroring
      `SpawnQuotaMonitor` exactly. Called from both `RunTier1` and
      `RunTier2` whenever `policy.Mcp.AllowServers` is non-empty (Tier1
      also creates+ACL-grants its new `Tier1ControlPaths.ControlDir`
      folder inline at the call site).
- [x] 3.2 Added the `--mcp-bridge-host`/`--mcp-policy-path` internal CLI
      mode to `src/Srm.Cli/Commands/RunCommand.cs`, delegating to a new
      `McpBridgeHostCommand.Run` (kept out of `RunCommand.cs` itself given
      the amount of new logic, unlike the smaller `RunQuotaMonitor`).
      Added `ModelContextProtocol` 1.4.1 to `Srm.Cli.csproj` — same version
      as `Srm.Mcp.csproj` already uses, per DC-018. Real API surface
      (`StdioClientTransport`, `McpClient.CreateAsync`, `ListToolsAsync`,
      `CallToolAsync`) confirmed via the C# SDK's official getting-started
      guide and compiled successfully on first attempt.
- [x] 3.3 Implemented the request/result polling loop
      (`McpBridgeHostCommand.Run`/`HandleRequest`/`HandleToolsList`/
      `HandleToolsCall`): reads `mcp-request.json`, rejects
      non-allow-listed servers/tools before forwarding, forwards allowed
      `tools/call`/`tools/list` to the real server, writes
      `mcp-result.json`.
- [x] 3.4 Logs every call via `StructuredLogger` — verified real log
      output for: server startup, an allowed `tools/call`, a rejected
      `tools/call` (tool not in `allow_tools`), and a filtered `tools/list`.
- [x] 3.5 Verified via `taskkill` on the bridge-host process that its
      spawned real MCP server subprocess (`node.exe` running
      `@modelcontextprotocol/server-everything`) does not survive as an
      orphan — confirmed via `tasklist` before/after.

**Real end-to-end verification** (task 3, ahead of task group 5): ran the
bridge host directly against the official
`npx -y @modelcontextprotocol/server-everything` test MCP server (no
guest-side stub yet — task group 4 — so the request file was written by
hand). Results:
- `tools/call` on the allow-listed `echo` tool → real server response
  round-tripped correctly (`"Echo: Hello from Channel D"`).
- `tools/call` on `add` (not in `allow_tools: [echo]`) → rejected with a
  clear reason, never reached the real server.
- `tools/list` → correctly filtered to only the one allow-listed tool
  (`echo`), even though the real "everything" server exposes many more.
- All four calls (1 startup + 3 requests) appear correctly in the
  structured JSON Lines audit log.

## 4. Guest-side stdio stub

- [x] 4.1 New executable `Srm.McpBridgeGuest` (`src/Srm.McpBridgeGuest/`),
      exposing itself as a stdio MCP server via `WithListToolsHandler`/
      `WithCallToolHandler` (dynamic proxy handlers — not
      `WithToolsFromAssembly()`'s compile-time attribute discovery, since
      the actual tool set is only known at runtime from the host bridge).
      `McpBridgeProxy` aggregates `tools/list` across all
      `SRM_MCP_SERVERS`-declared server names and routes `tools/call` by
      looking up which server owns each tool name (MCP's `tools/call` has
      no server field, only a flat tool name). Exact API signatures
      (`McpRequestHandler<ListToolsRequestParams, ListToolsResult>` etc.)
      confirmed against the SDK's own test suite via `gh search code`
      before writing the code, avoiding guesswork.
- [x] 4.2 Tier1 provisioning wired: `AppContainerLauncher.Launch` gained an
      `extraEnvironmentVariables` parameter (built a proper Windows
      double-null-terminated environment block merging the caller's own
      environment with the extras — `lpEnvironment=IntPtr.Zero` only
      means "inherit as-is", it can't be used to *add* variables, so a
      custom block + `CREATE_UNICODE_ENVIRONMENT` was required).
      `RunOperation.RunTier1` now: grants execute access to
      `Srm.McpBridgeGuest.exe` (resolved via `AppContext.BaseDirectory`,
      same convention as `SpawnMcpBridgeHost`'s `srm.exe` lookup) and sets
      `SRM_MCP_CONTROL_DIR`/`SRM_MCP_SERVERS` on the main sandboxed
      process's own environment, so a child process it spawns (i.e. the
      sandboxed agent spawning its own configured MCP server, which is
      exactly how MCP clients normally launch stdio servers) inherits them
      automatically. Also updated `tools/package.ps1` to publish
      `Srm.McpBridgeGuest.exe` into the shared `bin\` output alongside
      `srm.exe`/`Srm.Mcp.exe`/`Srm.PolicyEditor.exe`. **Tier2 provisioning
      is still open** — `RunNested` doesn't yet set the env vars or grant
      access for the guest-side stub in the Tier2 nested path.
- [x] 4.3 Real-machine verification, now covering the actual sandboxed
      case (not just the plain-process E2E from task 4's summary): ran a
      real `srm run` with a `tier: 1` policy declaring `mcp.allow_servers`,
      confirmed via the app's own output file that
      `SRM_MCP_CONTROL_DIR`/`SRM_MCP_SERVERS` were correctly visible
      inside the AppContainer'd process, and via `Get-Acl` that the
      AppContainer SID has `ReadAndExecute` directly on
      `Srm.McpBridgeGuest.exe`. Then ran the stub itself under that same
      AppContainer confinement and confirmed it starts and runs the MCP
      stdio transport loop without crashing — closing exactly the risk
      this design's Decision 1 exists to avoid (no DC-016-style
      AppContainer-specific failure materialized for this component).

**Bug found and fixed during Tier1 real-machine verification**:
`Host.CreateApplicationBuilder(args)`'s default configuration setup
(inherited from `Srm.Mcp.Program.cs`'s pattern, which never hit this
because `Srm.Mcp.exe` runs on the host, never inside AppContainer) tries to
attach a `FileSystemWatcher` to the process's current working directory for
`appsettings.json` hot-reload support. Under Tier1 AppContainer
confinement, the CWD is typically not in `allow_paths`, so this threw
`System.IO.FileNotFoundException` inside `PhysicalFilesWatcher` and crashed
the stub immediately on startup — even though the AppContainer SID's file
ACL grant on the stub *exe itself* was correct and sufficient to launch it.
Fixed by using `HostApplicationBuilderSettings { DisableDefaults = true }`
and explicitly adding only console logging — the stub has no config file to
watch in the first place. Confirmed via real Tier1 re-run: stub now starts
cleanly (`Content root path: ...` logged, no exception) under the exact
same AppContainer confinement that crashed it before.

**Known remaining gap (not yet task-tracked before this note)**: `srm stop`
does not currently signal the `--mcp-bridge-host` process's control channel
to shut down, so when the sandboxed app exits on its own (or is stopped),
the bridge host and its spawned real MCP server subprocess(es) are
orphaned. Observed directly during this session's testing (multiple
leftover `srm.exe`/`node.exe` processes after repeated test runs, cleaned
up manually via `taskkill`). This needs its own task before this change is
considered complete — added as task 3.6 below.

- [x] 3.6 (added after 4.2/4.3 testing surfaced the gap) Wired `srm stop`
      to signal the `--mcp-bridge-host` control channel: `RunningApp`
      gained `McpControlDir` (Tier1-only; Tier2 already shares its
      existing `control_dir` for this purpose since `SpawnMcpBridgeHost`
      was pointed at the same `SandboxRunPaths.Control` folder as the
      sandbox's own lifecycle signals). `StopOperation.StopTier1` now
      calls `SandboxControlChannel(app.McpControlDir).SignalStop()`
      (best-effort, no wait — matches this being a secondary process, not
      the main tracked one) after killing the main app and removing the
      WFP filter. Real-machine check: started a Tier1 app with
      `mcp.allow_servers`, confirmed via `tasklist` that `srm stop` leaves
      the bridge host (`srm.exe --mcp-bridge-host`) and its spawned real
      MCP server (`npx`/`node.exe`) running immediately after — this is
      expected, not a bug: `StdioClientTransportOptions.ShutdownTimeout`
      defaults to 5s, and disposal takes a few seconds to complete
      gracefully. Re-checked ~10s after `srm stop` and confirmed both
      processes were gone with no manual cleanup needed.

**Real end-to-end verification** (full chain, task 4 + effectively task
5.1's core): built a throwaway MCP client (outside the tracked repo, using
the same `ModelContextProtocol` client APIs) and connected it to
`Srm.McpBridgeGuest.exe` exactly as a sandboxed agent's own MCP client
would, with `SRM_MCP_CONTROL_DIR`/`SRM_MCP_SERVERS` env vars set. Chain
tested: **real MCP client → guest-side stub → control folder →
`--mcp-bridge-host` → real `npx @modelcontextprotocol/server-everything`
server → back through the same chain**. Both `tools/list` (correctly
showing only the allow-listed `echo` tool) and `tools/call echo` (correct
real response, "Echo: Round trip through Channel D") worked end-to-end.

**Bug found and fixed during this verification**: `McpBridgeHostCommand`'s
`tools/list` handler serializes an anonymous `{ name, description }`
object (lowercase, as literally typed in the C# source), but the guest
stub's `ToolSummary` DTO used PascalCase properties (`Name`/`Description`)
with no `[JsonPropertyName]` attribute — `System.Text.Json` deserialization
is case-sensitive by default, so this silently produced tools with empty
names (no exception; `tools/list` "succeeded" with a blank-named entry,
and the subsequent `tools/call echo` failed with "unknown tool"). Fixed by
adding explicit `[JsonPropertyName("name")]`/`[JsonPropertyName("description")]`
to `ToolSummary`. This is exactly the kind of silent-failure class of bug
worth calling out: it compiled fine and didn't throw anywhere near the
actual defect, only surfaced as a downstream symptom two calls later.

## 5. End-to-end verification

- [x] 5.1 Real-machine Tier1 test done (see task 4's "Real end-to-end
      verification" and 4.3's AppContainer-specific follow-up) — a real
      MCP client round-tripped through the guest stub, control folder,
      host bridge, to the real `everything` test server and back.
- [x] 5.2 Real-machine Tier2 test done: added
      `policies/tier2-mcp-bridge-test.yaml` (mirrors the existing
      `tier2-smoke-test.yaml` pattern) and ran it through an actual
      Windows Sandbox boot. Confirmed via the outbox mapped folder:
      `SRM_MCP_CONTROL_DIR`/`SRM_MCP_SERVERS` correctly visible inside the
      Tier2 nested AppContainer process (`CONTROL=C:\srm\control`,
      `SERVERS=everything`), and the guest stub ran successfully from the
      read-only `tooling` mapped folder (`Content root path: C:\srm\tooling\`,
      clean start/shutdown, no crash) — confirming `AppContext.BaseDirectory`
      resolves correctly to the tooling MappedFolder path inside the actual
      guest, not just in theory. Host-side log confirmed
      `--mcp-bridge-host` started and connected to the real MCP server.
      `srm stop` on this Tier2 run cleanly tore down the sandbox, the
      bridge host, and the real MCP server subprocess (`tasklist` showed
      zero remaining `srm.exe`/`node.exe`/`WindowsSandbox*.exe` ~10s after
      stop) — confirming Tier2's existing `StopTier2`
      (`control.SignalStop()` on the shared control dir, already wired for
      the sandbox's own lifecycle) needed **no additional changes** to also
      tear down the MCP bridge, unlike Tier1 which needed task 3.6's fix.
- [x] 5.3 Covered by task 3's standalone verification (rejected
      non-allow-listed tool call, logged) — re-verified implicitly by 5.1/5.2
      using the same `McpBridgeHostCommand` code path; not re-run as a
      separate Tier1/Tier2 sandboxed case given the enforcement logic is
      identical regardless of which tier the request originates from.
- [x] 5.4 Confirmed throughout 5.1/5.2: every real-machine test in this
      change was run via plain `srm.exe run`/`srm.exe stop` from the CLI
      directly, with `Srm.Mcp.exe` never started at any point.

## 6. Documentation and decision record

- [x] 6.1 Updated `manual/usage.md`: added `mcp.allow_servers` to the
      policy field reference and a new "チャネルD" section (mirrors the
      existing チャネルA/B sections) with an example and the
      audit-logging behavior.
- [x] 6.2 Wrote [DC-024](../../../views/records/DC-024.md), a
      DecisionRecord documenting Channel D's design and real-machine
      verification (both Tier1 and Tier2), including the three concrete
      bugs found and fixed during testing (`Host.CreateApplicationBuilder`
      CWD-watching crash under AppContainer, `srm stop` orphaning the
      bridge host, and the JSON property-casing silent failure).
