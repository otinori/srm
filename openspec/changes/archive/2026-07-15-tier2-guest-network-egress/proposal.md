## Why

DC-016 established that Claude Code (Bun-compiled) cannot run under an
AppContainer token: Bun's internal named-pipe creation fails against
AppContainer's default DACL and retries forever, with no fix available from
srm's side. DC-010 mandates that Tier2 (Windows Sandbox) always nests a
Tier1 AppContainer inside the guest (`srm.exe run --nested` reuses
`AppContainerLauncher`/`JobObjectManager` unconditionally; `alt-vm-only` was
explicitly rejected). DC-015 then built Tier2's network egress control on
top of that same nested AppContainer, registering WFP filters keyed by the
AppContainer SID.

This means Claude Code likely cannot run correctly under Tier2 either, as
currently implemented — the guest-side nested AppContainer reproduces
DC-016's busy-loop, and the network enforcement mechanism (DC-015) is
architecturally dependent on that AppContainer SID existing. To let Tier2
serve as a safe execution environment for apps that DC-016 has ruled out of
Tier1, guest network egress control must be decoupled from the requirement
that the nested process run inside an AppContainer.

## What Changes

- Add a WFP guest-side filter mode keyed by process path
  (`FWPM_CONDITION_ALE_APP_ID`) as an alternative to the existing
  AppContainer-SID-keyed condition, so `network.allow_hosts` enforcement
  does not require the nested process to hold an AppContainer token.
- Extend process-path-keyed filter registration to cover the full process
  tree spawned by the nested app (Job-Object-equivalent scope), not just the
  top-level executable, so a spawned child cannot bypass the egress filter.
- Allow `srm.exe run --nested` to launch the target app without wrapping it
  in an AppContainer when the policy opts out of AppContainer nesting (new
  policy field, exact shape defined in design.md), while still applying
  `network.allow_hosts` via the new path-keyed WFP filter. **BREAKING**: this
  is a partial revision of DC-010's "nested execution is always
  AppContainer-wrapped" decision — apps that opt out lose Tier1-level
  fs/process confinement inside the guest and rely on the VM boundary alone
  for that pillar (network egress control remains enforced either way).
- Document the reduced guarantee explicitly: policies that opt out of
  AppContainer nesting get "VM disposability (no host damage) + network
  egress control (no network exfiltration)" but not AppContainer's
  fine-grained fs/process confinement inside the guest.

## Capabilities

### New Capabilities
- `tier2-network-egress-control`: Guest-side WFP enforcement of
  `network.allow_hosts` that works whether or not the nested target process
  is wrapped in an AppContainer, including process-tree-scoped filter
  coverage for the non-AppContainer case.

### Modified Capabilities
(none — no existing `openspec/specs/` capability formally covers DC-010's
nested-execution AppContainer wrapping or DC-015's SID-keyed WFP
registration; both are currently captured only as DecisionRecords, not as
openspec spec deltas)

## Impact

- `src/Srm.Runtime/Operations/RunOperation.cs` (`RunNested` and the Tier2
  launch path) — needs a branch for "nested without AppContainer".
- `WfpManager` (Tier1/Tier2 shared, per DC-015) — needs a process-path-keyed
  filter registration mode alongside the existing AppContainer-SID-keyed
  one, plus process-tree coverage (likely via `JobObjectManager` PID
  enumeration rather than a static exe path list).
- Policy YAML schema — needs a field to opt a Tier2 app out of AppContainer
  nesting (e.g. under `application:` or a new `tier2:` section); exact shape
  is a design.md decision.
- DC-010 needs a follow-up DecisionRecord (not written yet) formally
  revising "nested execution is always AppContainer-wrapped" to "AppContainer
  wrapping is the default, with an explicit opt-out for apps incompatible
  with it per DC-016."
- Does not touch Channel C (file-transfer) or DC-013's evidence/quarantine
  gate — those remain the separate non-network leak-prevention layer per the
  prior architecture discussion.
