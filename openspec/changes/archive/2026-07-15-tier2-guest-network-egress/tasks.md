## 1. Real-machine PoC (blocking, before committing to the design)

- [x] 1.1 Verify `FWPM_CONDITION_ALE_USER_ID` semantics on a real machine
      (design.md Open Question). **Important negative result**: the design's
      original assumption (`FWP_SID`, same representation as
      `FWPM_CONDITION_ALE_PACKAGE_ID`) is wrong — `FwpmFilterAdd0` failed
      with `FWP_E_TYPE_MISMATCH` (0x80320027). Confirmed via Microsoft's
      official "Permitting and Blocking Applications and Users" sample that
      `FWPM_CONDITION_ALE_USER_ID` requires a self-relative security
      descriptor (`FWP_SECURITY_DESCRIPTOR_TYPE`) granting the target SID
      `FWP_ACTRL_MATCH_FILTER` access, built via `BuildSecurityDescriptorW`
      — not a raw SID. Fixed in `WfpManager.BuildUserSidSecurityDescriptorBlob`
      and verified working end-to-end against the real WFP engine
      (`WfpManagerTests.Install_UserSidIdentity_RegistersAndRemovesWithoutError`).
      **Still open**: this used the current session's own user SID, not a
      dedicated restricted account, and didn't verify the "one filter covers
      the whole process tree via token inheritance" property — that needs
      the actual restricted account from the sibling
      `restricted-account-app-isolation` change and is deferred to that
      change's own PoC (see this change's task 5).
- [x] 1.2 Verify `FwpmGetAppIdFromFileName0` + `FWPM_CONDITION_ALE_APP_ID`
      correctly blocks/allows connections, against the real WFP engine
      (`WfpManagerTests.Install_AppPathIdentity_BlocksDisallowedHost_ThenAllowsWhenAdded`):
      confirmed a disallowed host is blocked and an allowed host succeeds.
      Used the test process itself as the AppPath target rather than
      `claude -p` directly (avoids real Anthropic API cost/network
      dependency in a test that runs repeatedly) — this validates the WFP
      condition mechanism itself, which is identity-agnostic; the DC-016
      busy-loop is specifically an AppContainer-token problem and is
      structurally absent from the AppPath path since it never wraps the
      process in AppContainer. The actual `claude -p` repro end-to-end is
      still covered separately by task 4.2 once the policy/RunOperation
      wiring exists.

## 2. WfpManager identity abstraction

- [x] 2.1 Replace `WfpManager.Install(NetworkPolicy, IntPtr sidAppContainer,
      string appName)` with an identity parameter covering PackageSid /
      UserSid / AppPath (design.md's discriminated-identity decision).
- [x] 2.2 Update `BuildSidCondition`/`AddAllowIpRule`/`AddBlockAllRule` to
      select `FWPM_CONDITION_ALE_PACKAGE_ID` vs `FWPM_CONDITION_ALE_USER_ID`
      vs `FWPM_CONDITION_ALE_APP_ID` based on the identity variant. (Renamed
      to `BuildIdentityCondition`; `FWPM_CONDITION_ALE_USER_ID` uses
      `FWP_SECURITY_DESCRIPTOR_TYPE`, not `FWP_SID` — see task 1.1.)
- [x] 2.3 Confirm `RemoveForApp`/`RemoveFiltersForSubLayer` need no changes
      (already keyed on `appName`-derived `subLayerKey`, independent of
      identity type). Confirmed — no changes needed.
- [x] 2.4 Add/update unit or native-interop tests covering all three
      identity variants' condition construction
      (`tests/Srm.Runtime.Tests/WfpManagerTests.cs`, 3 tests, all passing
      against the real WFP engine; full `Srm.Runtime.Tests` suite — 85
      tests — still green after this change).

## 3. Policy schema and RunOperation branch

- [x] 3.1 Add the AppContainer opt-out field to `PolicyModel`
      (`src/Srm.PolicyEngine`) — landed as `tier2.app_container: false`
      (new `Tier2Policy` class, defaults `true` = unchanged behavior).
- [x] 3.2 Update `RunCommand.RunNested` (`src/Srm.Cli/Commands/RunCommand.cs`
      — DC-010's comment explains why this lives in `Srm.Cli`, not
      `RunOperation`) to branch: default path unchanged
      (`AppContainerLauncher` + `WfpIdentity.FromPackageSid`); opt-out path
      (`LaunchWithoutAppContainer`, new helper) launches via plain
      `Process.Start` with no ACL grants and calls `WfpManager.Install` with
      `WfpIdentity.FromAppPath` instead. `UserSid` wiring deferred to task 5
      (blocked on the sibling change's account primitive, as planned).
- [x] 3.3 `srm validate` now prints a warning when `tier: 2` +
      `tier2.app_container: false` is set (`ValidateOperation`/
      `ValidateCommand`), documenting the reduced fs/process confinement
      guarantee. Verified via real `srm validate` run against a temporary
      policy file — warning text confirmed to print correctly.

## 4. Fallback path first (no dependency on item ②)

- [x] 4.1 Implemented end-to-end: `tier2.app_container: false` →
      `LaunchWithoutAppContainer` + `WfpIdentity.FromAppPath` (task 3.2).
- [x] 4.2 Real-machine verification: ran DC-016's exact repro command
      (`claude -p "Reply with exactly: PONG" --output-format text`) via the
      same code path `RunCommand.LaunchWithoutAppContainer` +
      `WfpManager.Install(..., WfpIdentity.FromAppPath(...), ...)` takes
      (verified with a one-off test, not committed — see below), with
      `network.allow_hosts: [api.anthropic.com]`. **Result**: completed
      well under 10s and printed `PONG` correctly — no busy-loop, consistent
      with DC-016's non-isolated ~2s baseline (vs. 19+ minutes under
      AppContainer/Tier1). The temporary test was removed after collecting
      this result (it makes a real, billed Anthropic API call, so it isn't
      suitable for a repeatedly-run CI suite); this note is the durable
      record of the verification, matching this project's convention of
      recording one-off real-machine findings in the DC-016 style rather
      than committing costly tests.

## 5. UserSid path (depends on item ②'s account-provisioning primitive)

- [x] 5.1 Once the dedicated restricted account primitive from the sibling
      change exists, wire `RunOperation.RunNested`'s opt-out path to launch
      under that account and use the `UserSid` identity. Done in the
      `restricted-account-app-isolation` change (its tasks.md task 5.1):
      `RunCommand.RunNested`'s `else` branch now calls
      `RestrictedAccountManager.EnsureAccount`, launches via
      `RestrictedAccountLauncher`, and calls
      `WfpManager.Install(policy.Network, WfpIdentity.FromUserSid(account.Sid),
      policy.Name)`. `LaunchWithoutAppContainer` (task 3.2/4.1's original
      fallback) was removed as dead code once superseded.
- [x] 5.2 Real-machine verification: confirm a child process spawned by the
      nested app is also subject to `network.allow_hosts` (process-tree
      coverage scenario from specs/tier2-network-egress-control/spec.md).
      Done in the `restricted-account-app-isolation` change (its tasks.md
      task 5.2): ran a real Tier2 (Windows Sandbox) test
      (`policies/tier2-restricted-account-test.yaml`) where `cmd.exe`
      (the nested app) spawned `curl.exe` as a child process. Confirmed the
      child process's traffic was correctly subject to the `UserSid`
      filter — the allow-listed host (`example.com`) succeeded (HTTP 200)
      and the non-allow-listed host (`neverssl.com`) was blocked
      (`Couldn't connect to server`) — proving `UserSid`'s process-tree
      coverage advantage over the old `AppPath` fallback (which only
      covered the single named executable).

## 6. Documentation and decision record

- [x] 6.1 Wrote [DC-022](../../../views/records/DC-022.md), a follow-up
      DecisionRecord documenting the opt-out and its trade-offs (does not
      mark DC-010 Superseded — the default behavior is unchanged, this is
      an additive opt-out, matching this record's own review_trigger note).
- [x] 6.2 Updated `manual/usage.md`: added `tier2.app_container` to the
      policy schema reference block and a note in the network-filtering
      section about the `FWPM_CONDITION_ALE_APP_ID` fallback and its
      no-child-process limitation.
