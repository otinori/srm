## Context

`WfpManager` (`src/Srm.Runtime/WfpManager.cs`) currently keys every allow/block
filter on the AppContainer package SID
(`FWPM_CONDITION_ALE_PACKAGE_ID`, see `BuildSidCondition`/`AddAllowIpRule`).
The SID is passed in from `AppContainerLauncher`'s deterministic
`DeriveAppContainerSidFromAppContainerName` derivation (per DC-010) and is
reused unchanged for Tier2 via `RunOperation.RunNested` (per DC-015 — the
guest calls the same `WfpManager.Install`). This SID condition is what gives
today's filters "whole process tree for free": every child process spawned
by the AppContainer-wrapped app inherits the same AppContainer token/SID, so
one filter rule covers the tree without extra bookkeeping.

DC-016 shows this AppContainer token is itself the thing that breaks Bun
(Claude Code). Removing AppContainer for a given app therefore also removes
the "free" process-tree coverage the SID condition provided, and the design
must find a replacement identity to key the WFP condition on.

## Goals / Non-Goals

**Goals:**
- Define a WFP filter-keying mechanism that enforces `network.allow_hosts`
  for a nested Tier2 process that is *not* wrapped in an AppContainer.
- Preserve the existing AppContainer-SID-keyed path unchanged for apps that
  keep AppContainer nesting (default behavior, no regression).
- Ensure the replacement mechanism covers the full process tree, not just
  the top-level exe, with the same "no extra bookkeeping per spawn" property
  the SID condition has today.

**Non-Goals:**
- Redesigning Tier1's AppContainer-SID filtering — unchanged.
- Solving Channel C / file-transfer leak vectors — separate, already
  in-flight (`openspec/changes/tier2-channel-c-mapped-folder`).
- Implementing the dedicated-restricted-account mechanism itself (item ②
  from the parent discussion) — this design only depends on its *existence*
  as an identity to filter on; ② is tracked as its own change.

## Decisions

### Decision: prefer `FWPM_CONDITION_ALE_USER_ID` (local token SID) over `FWPM_CONDITION_ALE_APP_ID` (exe path) as the non-AppContainer key

WFP's ALE layers support conditioning on the connecting process's primary
token SID (`FWPM_CONDITION_ALE_USER_ID`) in addition to its executable path
(`FWPM_CONDITION_ALE_APP_ID`, resolved via `FwpmGetAppIdFromFileName0`).

**Correction (real-machine verification, tasks.md 1.1)**: unlike
`FWPM_CONDITION_ALE_PACKAGE_ID`, `FWPM_CONDITION_ALE_USER_ID` does **not**
accept a raw SID (`FWP_SID`) — `FwpmFilterAdd0` fails with
`FWP_E_TYPE_MISMATCH` (0x80320027) if you try. Per Microsoft's official
"Permitting and Blocking Applications and Users" sample, it requires a
self-relative security descriptor (`FWP_SECURITY_DESCRIPTOR_TYPE`) that
grants the target SID `FWP_ACTRL_MATCH_FILTER` access (built via
`BuildExplicitAccessWithNameW`/manually-constructed `EXPLICIT_ACCESS` +
`BuildSecurityDescriptorW`); WFP evaluates the condition as an access check
against that descriptor's DACL, not a direct SID equality comparison. This
is now implemented in `WfpManager.BuildUserSidSecurityDescriptorBlob` and
confirmed working against the real WFP engine. The rest of this decision's
reasoning (why user-SID-keying over app-path-keying) is unaffected by this
correction.

A regular Windows process token is inherited by child processes by default,
the same way an AppContainer token is — so keying on the *user SID* of a
dedicated restricted account (item ②'s mechanism) reproduces the "whole
process tree covered by one filter rule" property `WfpManager` already
relies on, with no per-spawn bookkeeping. Keying on `ALE_APP_ID` (exe path)
instead would only match the top-level executable; a spawned child with a
different binary would silently bypass the filter unless the design adds
dynamic enumeration (poll `JobObjectManager.TryGetProcessIds`, resolve each
new PID's exe path, register a filter for it) — extra runtime complexity and
a race window between spawn and filter registration.

**Consequence**: this change's non-AppContainer WFP path is designed to
consume a dedicated restricted account's user SID as input, making it
dependent on item ②'s "restricted dedicated user account" change landing
first (or at least its account-provisioning primitive existing). Until then,
this change can still land with `ALE_APP_ID`-keyed filtering as a fallback
for the narrow case of a single-exe, no-child-process app, with the
`ALE_USER_ID` path as the intended long-term mechanism once ② exists.

**Alternatives considered:**
- `ALE_APP_ID` (exe path) as the sole mechanism — rejected as primary
  because it doesn't cover child processes without dynamic PID polling
  (see above); kept as a documented fallback for the no-children case.
- Enumerate `JobObjectManager` PIDs and register per-PID filters
  dynamically — rejected as primary because of the spawn-to-registration
  race window (a child could make its first connection before its filter
  is registered) and the added polling-loop complexity; the `ALE_USER_ID`
  approach avoids needing this entirely.

### Decision: `WfpManager.Install` takes a discriminated identity parameter instead of a bare `IntPtr sidAppContainer`

Replace the current `Install(NetworkPolicy network, IntPtr sidAppContainer, string appName)`
signature with an identity abstraction (e.g. a small union/enum of
`PackageSid(IntPtr)` | `UserSid(IntPtr)` | `AppPath(string)`) so
`BuildSidCondition`/`AddAllowIpRule` can select the correct
`fieldKey` (`FWPM_CONDITION_ALE_PACKAGE_ID` vs `FWPM_CONDITION_ALE_USER_ID`)
without duplicating the allow/block rule-building logic per identity type.
`RemoveForApp`/`RemoveFiltersForSubLayer` are keyed on the deterministic
`subLayerKey` derived from `appName` and are unaffected by this change.

**Alternatives considered:**
- Separate `WfpManager` subclass per identity type — rejected, the only
  difference is the condition-building helper; a full subclass would
  duplicate `AddAllowIpRule`'s IP/loopback logic for no benefit.

### Decision: policy opt-out is per-Tier2-app, not global

The proposal's new policy field (exact YAML shape TBD, likely
`tier2.app_container: false` under the existing `tier: 2` policy) opts a
single app out of AppContainer nesting. `RunOperation.RunNested` branches on
this field: when `true`/absent (default), behavior is unchanged (existing
`AppContainerLauncher` + SID-keyed `WfpManager.Install`); when `false`,
`RunNested` launches the target directly under the dedicated restricted
account (item ②) without `AppContainerLauncher`, and calls
`WfpManager.Install` with the `UserSid` identity instead.

## Risks / Trade-offs

- **[Risk]** Apps that opt out of AppContainer nesting lose Tier1-level
  fs/process confinement inside the guest (per DC-010's `alt-vm-only`
  rejection rationale) → **Mitigation**: this is an explicit, documented
  trade-off (see proposal.md's BREAKING note), not a silent regression; the
  VM boundary (Tier2's structural guarantee) still prevents host damage,
  and this change's scope is limited to restoring the network-egress pillar
  for that reduced-confinement case.
- **[Risk]** This design is blocked on item ②'s account-provisioning
  primitive for its intended long-term mechanism → **Mitigation**: land the
  `ALE_APP_ID` fallback first for the no-child-process case (covers `claude
  -p` style single-process headless invocations, which is exactly DC-016's
  reproduction case), then extend to `ALE_USER_ID` once ② exists.
- **[Risk]** `FWPM_CONDITION_ALE_USER_ID`'s condition-value construction was
  unverified against real-machine behavior → **Materialized**: the original
  `FWP_SID` assumption was wrong (`FWP_E_TYPE_MISMATCH`); fixed to
  `FWP_SECURITY_DESCRIPTOR_TYPE` (see the Decision above) and now verified
  working end-to-end (register/remove, and actual block/allow of real
  connections) via `WfpManagerTests`. **Still open**: whether it matches the
  primary token of the *process* or something session-scoped, specifically
  under a dedicated restricted account with child processes — the tests so
  far used the current session's own user SID and a single process, not a
  restricted account with an inherited-token child. → **Mitigation**: this
  narrower question is deferred to the sibling
  `restricted-account-app-isolation` change's own real-machine PoC, once a
  real dedicated account exists to test against.

## Open Questions

- Exact YAML shape for the AppContainer opt-out field — deferred to
  implementation (tasks.md), needs to fit the existing `PolicyModel`
  structure in `src/Srm.PolicyEngine`.
- Whether `ALE_USER_ID` requires the dedicated account's SID to be resolved
  before or after the nested process starts (ordering constraint similar to
  DC-010's ACL pre-grant timing concern) — needs the real-machine PoC above.
- Whether this change needs its own DC record (revising DC-010) or whether
  documenting the trade-off in proposal.md/design.md is sufficient before
  archive — follow this project's existing convention (check with
  `openspec-archive-change` flow when this change is implemented).
