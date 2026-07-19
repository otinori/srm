## Why

DC-016 proved that Claude Code (Bun-compiled) cannot run under an
AppContainer token — Bun's internal named-pipe creation fails against
AppContainer's default DACL and retries forever in a kernel busy-loop, with
no fix available from srm's side (confirmed via a failed token
default-DACL workaround). Apps in this situation currently have no way to
get any filesystem confinement from srm: they either run unconfined on the
host, or they're pushed to Tier2 where DC-010 still nests the same broken
AppContainer inside the guest (see the sibling change
`tier2-guest-network-egress`, which addresses the network pillar for this
case). This change addresses the filesystem/process pillar: a lighter-weight
confinement mechanism — a dedicated restricted Windows user account plus
NTFS ACLs — that doesn't route through the AppContainer capability-token
code path DC-016 identified as the failure trigger, so it should not
reproduce the Bun busy-loop.

## What Changes

- Add a new confinement mechanism, parallel to (not replacing) Tier1's
  `AppContainerLauncher`: a dedicated, restricted local Windows user account
  used to launch a target app, with NTFS ACLs on policy-declared
  `filesystem.allow_paths` granted to that account's SID instead of an
  AppContainer package SID.
- Reuse `AclManager`'s existing traverse-chain/grant logic
  (`GrantTraverseChain`/`GrantExecuteAccess` in
  `src/Srm.Runtime/AclManager.cs`), generalized to accept either an
  AppContainer SID or a regular user SID as the grantee.
- Provide the resulting account SID as an identity option for the sibling
  `tier2-guest-network-egress` change's WFP `UserSid` filter-keying path
  (that change's design.md names this as its intended long-term mechanism).
- Define the account lifecycle: created once (or per-app, TBD in design.md),
  never used for interactive login, least-privilege (no admin group
  membership), reused across `srm run` invocations of the same policy rather
  than provisioned fresh each time (to avoid DC-010-style per-run setup
  cost).
- Document explicitly what this mechanism does *not* provide relative to
  AppContainer: no capability-based network/device restriction of its own
  (network egress is delegated to the sibling change's WFP work), no
  AppContainer's kernel-enforced object-namespace isolation — this is
  NTFS-ACL-level fs confinement plus standard Windows process-token
  least-privilege, not a capability sandbox.

## Capabilities

### New Capabilities
- `restricted-account-app-isolation`: Launching a target app under a
  dedicated, non-interactive, least-privilege Windows user account with
  NTFS ACLs scoped to policy-declared `filesystem.allow_paths`, as an
  alternative to AppContainer for apps DC-016 has ruled out of Tier1.

### Modified Capabilities
(none — `AclManager`'s grant logic is being generalized, not changing its
existing AppContainer-SID behavior; no existing `openspec/specs/` capability
covers it today)

## Impact

- `src/Srm.Runtime/AclManager.cs` — `GrantTraverseChain`/`GrantExecuteAccess`
  need a grantee-SID parameter generalized beyond AppContainer SIDs (both
  currently take `IntPtr sidAppContainer` by name).
- New native interop for local account creation/management (`NetUserAdd`/
  `LogonUser`/`CreateProcessAsUser` or equivalent) — no existing code in
  `src/Srm.Runtime/Native` covers this; account lifecycle is new surface
  area.
- Policy YAML schema — needs a field to select this confinement mode instead
  of (or alongside) AppContainer, shape TBD in design.md; likely coordinates
  with the `tier2.app_container: false` opt-out proposed in the sibling
  `tier2-guest-network-egress` change so both agree on when a restricted
  account is in play.
- Does not touch Tier2's Windows Sandbox provisioning (`SandboxLauncher`) or
  Channel C's file-transfer/quarantine mechanism — this is a Tier1-adjacent
  (and Tier2-nested-execution-adjacent) confinement primitive, not a new
  tier or a network/leak-review mechanism of its own.
- Security-relevant: a dedicated local account with broad-but-scoped NTFS
  access is a smaller blast-radius reduction than AppContainer's
  kernel-object-namespace isolation; this must be reviewed as a
  security-relevant design before implementation (design.md's Risks
  section should treat this as the primary open risk).
