## 1. Real-machine PoC (blocking, before committing to the design)

- [x] 1.1 (Superseded scope — see design.md's Low IL pivot) Originally:
      apply `CreateRestrictedToken` and confirm launch. That path is
      abandoned (DC-023). **New scope, done**: launch the dedicated
      account's plain logon token lowered to Low Integrity Level via
      `SetTokenInformation(TokenIntegrityLevel)`, then `CreateProcessWithTokenW`.
      Verified real-machine, green: `RestrictedAccountTests.
      Launch_LowIntegrityProcess_CanWriteLabeledPath_CannotWriteUnlabeledPath`.
      Two additional real-machine blockers were found and fixed along the
      way (see DC-023's update): (a) launching a console app under a
      different user's token hangs indefinitely before `conhost.exe` is
      even created, because the target account has no default access to
      the interactive window station (`WinSta0`)/desktop (`Default`) —
      fixed by granting the account SID explicit DACL entries on both
      objects (`RestrictedAccountLauncher.GrantWindowStationAndDesktopAccess`,
      the same technique tools like PsExec's `-i` use); (b) a hardcoded
      `SECURITY_DESCRIPTOR_MIN_LENGTH = 20` (the 32-bit-era WinNT.h
      literal) undersized the native buffer on x64, causing an
      intermittent heap-corruption crash that only reproduced when running
      the full test suite (not in isolation) — fixed by allocating 64
      bytes instead.
- [x] 1.2 Confirm the launched process actually completes instead of
      hanging or being silently blocked (this change's version of DC-016's
      "does it complete or busy-loop" check): the PoC test's
      `cmd.exe` writes to the Low-IL-labeled `allow_paths` folder,
      completes, and exits within the 15s wait — no hang, matching a
      working (non-isolated-equivalent-latency) launch, not DC-016's
      busy-loop failure mode.
- [x] 1.3 If 1.2 fails, treat as its own investigation (do not assume this
      approach is equivalent to "no isolation" — see design.md's Risks
      section). Done: DC-023 (both the original `CreateRestrictedToken`
      dead end and the two Low IL implementation blockers above are
      recorded there).

## 2. Account and group provisioning

- [x] 2.1 Implement deterministic per-policy account name derivation
      (mirror `DeriveAppContainerSidFromAppContainerName`'s determinism).
      Landed as `RestrictedAccountManager.DeriveAccountName` (MD5-hash-based,
      respects the 20-char SAM account name limit — not mentioned in
      design.md, discovered during implementation).
- [x] 2.2 Implement lazy account creation (`NetUserAdd`): check-if-exists
      before create, reset password (not full record) on reuse — see
      `RestrictedAccountManager.EnsureAccount`. Verified via
      `RestrictedAccountTests.EnsureAccount_IsIdempotentAndReturnsSid`
      (passing, real machine).
- [x] 2.3 Implement the shared `SRM-RestrictedApps` local group: create once,
      add each new policy's dedicated account as a member. Landed and
      verified (real machine, group SID resolution works).
- [x] 2.4 Decide and implement account/group cleanup path (not deleted on
      `srm stop`; needs its own removal command, per design.md's lifecycle
      decision). This project has no general "policy remove" command
      (policies are just YAML files with no installed/registered state), so
      rather than inventing one, added a narrow, purpose-built
      `srm cleanup-account <policy>` command
      (`Srm.Runtime.Operations.CleanupAccountOperation` +
      `Srm.Cli.Commands.CleanupAccountCommand`) that deletes just the
      dedicated account for a policy name. Verified real-machine both ways:
      an automated test (`CleanupAccountOperationTests.
      Execute_DeletesAccountCreatedByRestrictedAccountManager` — confirms
      a `LogonUserW` attempt against the deleted account's former
      credentials now fails) and a manual CLI round-trip (created an
      account, confirmed it existed via `net user`, ran
      `srm cleanup-account`, confirmed `net user` now reports
      "ユーザーが見つかりません").

## 3. AclManager generalization

- [x] 3.1 Generalize `GrantTraverseChain`/`GrantExecuteAccess`
      (`src/Srm.Runtime/AclManager.cs`) to accept a grantee-SID parameter
      not hardcoded to AppContainer's `AllAppPackagesSid` — parameterize the
      "shared coarse grant" target (today `S-1-15-2-1`, this mechanism's
      `SRM-RestrictedApps` group SID). Landed as overloads taking
      `(sidGrantee, sharedTraverseNativeSid, sharedTraverseSid)`; existing
      `(allowPaths, sidAppContainer)` callers unchanged.
- [x] 3.2 Verify `HasDirectoryAccess`'s SID-equality check still works
      correctly when the "our SID" is a restricted-token account SID rather
      than an AppContainer SID. Verified on real machine via `Get-Acl`
      inspection — grants to the per-policy account SID and the shared
      group SID both appear correctly.
- [x] 3.3 Add tests covering: ancestor traverse grant goes to the shared
      group, `allow_paths` grant goes to the per-policy account, and a
      second policy's process cannot read a first policy's `allow_paths`.
      Landed as `tests/Srm.Runtime.Tests/AclManagerRestrictedAccountTests.cs`
      (2 tests, real machine, `DirectorySecurity`-based inspection):
      `GrantAccess_AllowPathGoesToAccountSid_AncestorTraverseGoesToSharedGroupSidOnly`
      and `GrantAccess_SecondPolicysAccount_HasNoDirectAccessToFirstPolicysAllowPath`.
      **Important lesson from the earlier manual 3.2 check, preserved
      here**: don't target TrustedInstaller-protected files (e.g. anything
      directly under `C:\Windows\System32`) in these tests — granting an
      inheritable ACE to the parent directory does *not* retroactively
      apply to existing protected files, and attempting the fallback
      recursive directory-level grant took ~9 minutes and required manual
      cleanup (see DC-023). The new tests target a fresh temp-directory
      subtree instead.

## 4. Process launch integration

- [x] 4.1 Add a `RunOperation`/`AppContainerLauncher`-parallel launcher
      (`RestrictedAccountLauncher`). **Unblocked**: rewritten for the Low
      IL pivot (no `CreateRestrictedToken`), verified real-machine (see
      task 1.1). `Launch` now successfully starts a process under the
      dedicated account at Low IL.
- [x] 4.2 Wire the policy YAML opt-in field — reuse `tier2.app_container:
      false` (from the `tier2-guest-network-egress` change, DC-022) as the
      single trigger. `RunCommand.RunNested`'s `else` branch now calls
      `RestrictedAccountManager.EnsureAccount`, grants `allow_paths` +
      the executable via the generalized `AclManager` overloads, labels
      each `allow_paths` folder Low IL, and launches via
      `RestrictedAccountLauncher` instead of the old unconfined
      `LaunchWithoutAppContainer` (removed as dead code). MCP bridging
      (`mcp.allow_servers`) is wired the same way as the AppContainer
      branch, including a new `RestrictedAccountLauncher.
      BuildAccountEnvironmentBlock` (uses `CreateEnvironmentBlock` on the
      *target* token, not the caller's own environment, to avoid leaking
      the host Administrator's `USERPROFILE`/`TEMP` into the restricted
      account's process — factored the env-block-merging logic into a
      shared `EnvironmentBlockBuilder` used by both launchers).
- [x] 4.3 Confirm `JobObjectManager` assignment (process-tree tracking for
      `srm stop`) works unchanged for processes launched this way. The
      `job.AssignProcess(result.ProcessHandle)` call in `RunNested` is
      common to both branches (outside the if/else), so no launcher-specific
      change was needed — Job Objects operate on process handles
      independent of the process's token/account. Verified real-machine
      (task 5.2): the sandboxed app ran to completion under the Job
      Object-tracked flow with no errors.

## 5. Cross-change integration

- [x] 5.1 Coordinate with `tier2-guest-network-egress`: expose this
      mechanism's per-policy account SID so that change's `UserSid`-keyed
      WFP filter path can consume it. `RunNested`'s `else` branch now calls
      `WfpManager.Install(policy.Network, WfpIdentity.FromUserSid(account.Sid),
      policy.Name)` instead of the previous `FromAppPath` identity —
      resolves DC-022's own task 5 at the same time.
- [x] 5.2 Real-machine verification: ran a real Tier2 (Windows Sandbox) test
      with `tier2.app_container: false` (new policy
      `policies/tier2-restricted-account-test.yaml`) exercising both this
      change and `tier2-guest-network-egress`'s `UserSid` path together.
      Confirmed simultaneously: (a) fs write barrier — `cmd.exe` running
      under the Low-IL restricted account successfully wrote
      `write-test.txt` into the Low-IL-labeled `allow_paths` folder
      (`C:\srm\outbox`, the Tier2 MappedFolder); (b) network egress
      control — `curl.exe` to the allow-listed `example.com` returned
      HTTP 200, while `curl.exe` to the non-allow-listed `neverssl.com`
      failed to connect (`Couldn't connect to server`), keyed on the
      account's `UserSid` rather than an `AppContainer` `PackageSid`;
      (c) the guest-side `nested.log` confirmed the app process launched
      and ran to completion (no DC-016-style hang); (d) `srm stop` tore
      down the Windows Sandbox VM cleanly with no lingering
      `WindowsSandbox*` processes afterward.

## 6. Documentation and decision record

- [x] 6.1 Write a DecisionRecord documenting this as a new confinement
      primitive alongside Tier1/Tier2. Landed as
      [DC-023](../../../views/records/DC-023.md) — an InvestigationRecord
      (not a DecisionRecord) since the launch-mechanism blocker means no
      settled decision exists yet to document; DC-023 records the
      investigation and open next steps instead. Now updated with the
      Low IL pivot's resolution (see DC-023's 2026-07-15 addendum).
- [x] 6.2 Update `manual/usage.md` with the new policy field and its
      documented reduced-guarantee trade-off. Rewrote both the
      `tier2.app_container: false` field description (now Low IL write
      barrier + dedicated account, not "unconfined") and the network
      section's identity description (now `FWPM_CONDITION_ALE_USER_ID`
      keyed on the account SID, covering child-process traffic too,
      instead of the old single-process `FWPM_CONDITION_ALE_APP_ID`
      fallback).
