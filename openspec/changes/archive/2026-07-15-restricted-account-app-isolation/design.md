## Context

`AclManager` (`src/Srm.Runtime/AclManager.cs`) is built around AppContainer's
**default-deny** posture: an AppContainer token does not inherit
`BUILTIN\Users`/`Authenticated Users`, so every ancestor directory up to the
allowed path needs an explicit Traverse grant (`GrantTraverseChain`), and
`filesystem.allow_paths` need an explicit grant to the AppContainer's own
SID. A plain Windows user account has the **opposite** default posture: it
normally inherits `BUILTIN\Users`/`Authenticated Users` and can read most of
the filesystem by default. Simply creating "a restricted account" and
launching a process under it, with no further token surgery, would not
reproduce AppContainer's confinement — it would just be a differently-named
standard user with broad default read access.

DC-016 also already surfaced a concrete cost constraint relevant here: the
first-time NTFS ACL propagation for a previously-ungranted ancestor
directory (e.g. `%USERPROFILE%\AppData`) took 100-180 seconds in the
AppContainer case, mitigated by writing ancestor traverse grants to the
**shared** well-known `S-1-15-2-1` (ALL APPLICATION PACKAGES) SID once
rather than per-policy SIDs, so later policies hit `HasTraverseAccess` and
skip the write. Any restricted-account design needs an equivalent
shared-grant strategy or it reintroduces that cost per new policy.

## Goals / Non-Goals

**Goals:**
- Define a token/account construction that gives a dedicated account
  AppContainer-equivalent default-deny filesystem behavior, without routing
  through the AppContainer capability-token code path DC-016 identified.
- Preserve `AclManager`'s "shared coarse grant for ancestor traverse, precise
  per-policy grant for `allow_paths`" pattern, so this mechanism doesn't
  reintroduce DC-016's NTFS-propagation-delay cost per new policy.
- Keep per-policy isolation: policy A's `allow_paths` grants must not become
  readable by policy B's process just because both use this mechanism.

**Non-Goals:**
- Replacing AppContainer for apps that already work under Tier1 — this is
  strictly an alternative for apps DC-016 rules out.
- Network confinement — delegated to the sibling `tier2-guest-network-egress`
  change, which names this change's account SID as its intended
  `FWPM_CONDITION_ALE_USER_ID` filter key.
- Kernel-object-namespace isolation (AppContainer's registry/named-object
  separation) — proposal.md already documents this as a known gap relative
  to AppContainer; out of scope to replicate here.

## Decisions

### Decision (SUPERSEDED — see below): strip default group SIDs with `CreateRestrictedToken`

~~Launch the target process with a token produced by `CreateRestrictedToken`
(disabling `BUILTIN\Users`, `Authenticated Users`, and `Everyone` SIDs on the
dedicated account's token) rather than the account's unmodified logon token.~~

**Real-machine result (DC-023)**: this decision does not work. Every
standard Windows process-launch API tried against a `CreateRestrictedToken`
-derived token failed, each in a different way: `CreateProcessAsUserW`
requires `SeAssignPrimaryTokenPrivilege` (not held by Administrators by
default); `ImpersonateLoggedOnUser` + plain `CreateProcess` fails with
`ERROR_ACCESS_DENIED` even with correct file ACLs in place;
`CreateProcessWithTokenW` (the API actually designed for this scenario)
fails with the misleadingly-named `ERROR_BLOCKED_BY_PARENTAL_CONTROLS`
(346) specifically when `Everyone` (S-1-1-0) is in the disabled-SID list —
confirmed via single-SID isolation testing — and even removing `Everyone`
from the list still hits a different, unexplained `ACCESS_DENIED`. Three
independent failure modes across three APIs is strong evidence the
`CreateRestrictedToken` + standard-launch-API combination is fragile in a
way not worth continuing to chase by trial and error. See DC-023
(`views/records/DC-023.md`) for the full investigation.

### Decision: Low Integrity Level (mandatory label) + a plain (non-restricted-token) dedicated account, replacing `CreateRestrictedToken`

Launch the target process under the dedicated account's **unmodified**
logon token (no `CreateRestrictedToken` at all — this is the change from
the superseded decision above), but lower the token's **integrity level**
to Low (`SetTokenInformation(TokenIntegrityLevel)`, well-known SID
`S-1-16-4096`) before launch. Windows Mandatory Integrity Control (MIC)
then enforces a structural **write barrier**: a Low-IL process cannot write
to any object at the default Medium IL or above, regardless of what the
object's DACL allows — this is the same mechanism Internet
Explorer/Chrome's historical "Protected Mode" sandboxes used, a mature,
widely-deployed Windows feature (unlike the `CreateRestrictedToken` +
`CreateProcessWithTokenW` combination this design is moving away from,
which has no comparable track record and turned out to be fragile in
practice on this machine).

For `filesystem.allow_paths` to actually be *writable* by the Low-IL
process, each `allow_paths` folder needs its own mandatory label explicitly
lowered to Low too (`AddMandatoryAce` with `SYSTEM_MANDATORY_LABEL_NO_WRITE_UP`
on the folder's SACL — the same operation `icacls <path> /setintegritylevel
(OI)(CI)Low` performs) — an unlabeled object defaults to Medium and blocks
Low-IL writers by the system's default policy.

This changes what pillar of the charter this mechanism can honestly claim:
**write confinement** (don't destroy things outside `allow_paths`) is a
structural OS guarantee under this design, matching the "don't break the
environment" pillar well. **Read confinement** is *not* structurally
guaranteed by Low IL alone (Low IL does not block reads of Medium+ objects
by default) — this design relies on the dedicated account being a genuinely
separate, low-privilege Windows account (which, absent Low IL entirely,
already can't read *other users'* profiles thanks to ordinary per-user NTFS
ACLs Windows sets up itself) plus the sibling `tier2-guest-network-egress`
change's network egress control as the actual leak-prevention layer for
whatever the process *can* read. This is a narrower, more honest guarantee
than the original `CreateRestrictedToken` decision aimed for, but is one
that a real, well-tested Windows mechanism can actually deliver.

**Alternatives considered:**
- Keep pursuing `CreateRestrictedToken` via yet another launch API
  (`CreateProcessWithLogonW`, LSA-permanent `SeAssignPrimaryTokenPrivilege`
  grant + `CreateProcessAsUserW`, `runas.exe`/Task Scheduler indirection) —
  rejected for now: three independent failure modes already found suggest
  diminishing returns from continued API-level trial and error; DC-023's
  review_trigger keeps this option open if a future session wants to
  revisit it, but it is not this design's primary path anymore.
- Plain standard user account, no token/IL modification at all — still
  rejected for the same reason as before: no structural write-confinement,
  would require unbounded DENY-ACE enumeration to claw back any guarantee.
- Explicit DENY ACEs on sensitive well-known locations instead of Low IL —
  rejected: DENY ACEs require enumerating what to deny (open-ended,
  easy to miss something) rather than Low IL's default-deny-for-writes
  structural guarantee that requires no enumeration of what to protect.

### Decision: one shared local group for ancestor-traverse grants, one dedicated account per policy for `allow_paths` grants

Mirror `AclManager`'s existing two-tier pattern (`AllAppPackagesSid` shared
+ per-policy AppContainer SID precise) instead of inventing a new one:
- Create one shared local group (e.g. `SRM-RestrictedApps`) that every
  policy's dedicated restricted account belongs to. `GrantTraverseChain`'s
  generalized form grants ancestor Traverse/ReadAttributes to this group's
  SID once, exactly as it does today for `S-1-15-2-1` — later policies hit
  the same `HasDirectoryAccess` check and skip the write, avoiding DC-016's
  100-180s propagation cost per new policy.
- Create one dedicated restricted account **per policy name** (not one
  shared account for all policies) for the actual `filesystem.allow_paths`
  GRANT, so policy A's account has no ACE granting it policy B's paths —
  same isolation property the per-policy-derived AppContainer SID already
  gives today. Account naming/derivation should mirror
  `DeriveAppContainerSidFromAppContainerName`'s determinism (derive from
  policy name) so `srm stop`/cleanup can resolve the same account without
  persisting extra state.

**Alternatives considered:**
- One shared account for all policies — rejected: grants accumulate on the
  shared account across policies and are never revoked per-policy (no clean
  point to remove just policy A's grant without affecting policy B), which
  leaks each policy's `allow_paths` to every other policy using this
  mechanism. Rejected specifically because it breaks the proposal's Impact
  section's isolation goal.
- Per-policy account AND per-policy traverse grants (no shared group) —
  rejected: reintroduces DC-016's 100-180s ancestor-propagation cost on
  every new policy name, which is the exact problem `AllAppPackagesSid`
  exists to avoid; no reason to regress here.

### Decision: account lifecycle is provisioned lazily on first `srm run`, reused (not deleted) across runs of the same policy

Consistent with the "no per-run setup cost" goal in proposal.md: check for
an existing account matching the policy's derived name before creating one.
`srm stop` does not delete the account (mirrors: AppContainer profiles are
also not torn down between runs). A separate cleanup path (`srm policy
remove`-equivalent, not yet designed) would delete the account together with
removing the policy.

## Risks / Trade-offs

- **[Risk, materialized]** `CreateRestrictedToken` + standard launch APIs
  turned out to be fragile in practice (DC-023) — this is exactly the class
  of bug DC-016 spent weeks chasing for AppContainer, recurring here for a
  different mechanism → **Mitigation applied**: pivoted away from
  `CreateRestrictedToken` entirely to Low Integrity Level, a mature,
  long-deployed Windows mechanism with a much better real-world track
  record, rather than continuing to chase the fragile combination.
- **[Risk]** Low IL's write barrier does not restrict reads — a Low-IL
  process can still read most Medium-IL content it would normally have
  access to as that Windows account → **Mitigation**: this is now an
  explicit, documented scope boundary (see the Low IL decision above) —
  read-side leak prevention is delegated to the dedicated account's own
  natural lack of access to other users' data plus
  `tier2-guest-network-egress`'s network control, not claimed as a Low-IL
  guarantee.
- **[Risk, resolved]** `AddMandatoryAce`/mandatory-label SACL construction
  on `allow_paths` folders was new native surface, unverified on a real
  machine → **Verified**: `RestrictedAccountTests.
  Launch_LowIntegrityProcess_CanWriteLabeledPath_CannotWriteUnlabeledPath`
  confirms both halves on real hardware — a Low-IL process launched via
  `RestrictedAccountLauncher` successfully writes inside a Low-IL-labeled
  `allow_paths` folder, and fails to write to an arbitrary unlabeled
  (Medium IL) folder in the same run. Two unrelated real-machine blockers
  surfaced and were fixed along the way (see DC-023's update): a
  window-station/desktop access gap that hung console-app launches under a
  different user's token indefinitely, and an undersized native buffer
  (`SECURITY_DESCRIPTOR_MIN_LENGTH`) that caused intermittent heap
  corruption only visible when running the full test suite.
- **[Risk]** Local account creation (`NetUserAdd` or equivalent) may require
  elevated privileges or interact with domain-joined machine policies in
  ways AppContainer's process-local capability SIDs never did → **Mitigation**:
  scope initial validation to a non-domain-joined workstation (matches
  DC-016's investigation environment); flag domain-joined support as a
  follow-up, not a launch requirement.
- **[Trade-off]** This mechanism provides weaker isolation than AppContainer
  overall (no kernel object-namespace separation, no capability model) —
  already called out in proposal.md's Impact section; design.md does not
  attempt to close that gap, only to make the filesystem-confinement subset
  work without triggering DC-016.

## Open Questions

- ~~Exact native API surface for the Low IL pivot~~ — **resolved**:
  `SetTokenInformation` + `TokenIntegrityLevel` (token side) and
  `InitializeAcl` + `AddMandatoryAce` (folder-labeling side) both work as
  expected and are verified real-machine. Two additional pieces of native
  surface turned out to be necessary and are now implemented too:
  granting the account SID access to the interactive window
  station/desktop (`RestrictedAccountLauncher.
  GrantWindowStationAndDesktopAccess`, via `OpenWindowStationW`/
  `OpenDesktopW`/`GetUserObjectSecurity`/`SetUserObjectSecurity`) — without
  it, launching a console app under the dedicated account's token hangs
  indefinitely before `conhost.exe` is even created — and `CREATE_NEW_CONSOLE`
  plus an explicit `STARTUPINFO.lpDesktop = "WinSta0\Default"` on the
  `CreateProcessWithTokenW` call itself.
- Whether the shared `SRM-RestrictedApps` group's own ancestor-traverse
  grants (design.md's earlier two-tier decision) still make sense under
  Low IL, or whether Low IL's write barrier makes some of that traverse
  scaffolding unnecessary — resolve during implementation.
- Coordination with the sibling `tier2-guest-network-egress` change on
  exactly which policy YAML field selects this mechanism, and whether one
  field controls both the AppContainer opt-out and this account-based
  fs-confinement mode, or two independent fields.
