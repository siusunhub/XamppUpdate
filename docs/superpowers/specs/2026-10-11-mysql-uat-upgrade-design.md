# MySQL "Real Test Upgrade" (UAT copy) - Design

Date: 2026-10-11
Status: revised draft after code review (2026-10-12); ready for implementation planning, not an implementation approval

## 1. Purpose

Rehearse a real MariaDB/MySQL upgrade on a **full copy** of the production installation, running **in parallel** with
production on a different port, so problems (dirty InnoDB redo log, incompatible `my.ini`, failed `mariadb-upgrade`)
are found before production binaries or configuration are replaced. The default copy mode briefly stops production.

Success = a per-stage report that shows, with evidence, whether the exact new version starts on a copy of the real
data and completes the supported upgrade procedure, with production restored to its original running/stopped state
and launch mode. This is an upgrade rehearsal, not a backup or proof of application compatibility.

## 2. Scope

In scope: the MySQL/MariaDB wizard only. Out of scope: Apache/PHP/phpMyAdmin wizards, automatic promotion of the copy
to production, data masking. Initial support is MariaDB-to-MariaDB on a documented supported upgrade path.
Reject cross-family MySQL/MariaDB migrations, downgrades, and unsupported version jumps before stopping production;
Oracle MySQL needs its own version-specific upgrade design rather than assuming `mariadb-upgrade` applies.

## 3. User-facing behaviour

### 3.1 Entry point
On wizard step 1 a **"Real Test Upgrade"** button is placed on the left of the existing Next button. It:
1. validates that a source is selected (same validation as Next),
2. prepares the incoming package (same `PrepareIncomingSourceAsync` + version detection as Next),
3. opens the **UAT window** modally. It does **not** advance the wizard.

### 3.2 UAT window options

| Option | Default | Notes |
|---|---|---|
| Target folder | `<parent of MySql.InstallationPath>\mysql_copy_upgrade` | Editable |
| Port | `13306` | Editable; must be free and differ from production's |
| Stop production MySQL before copy | on | Off = experimental live file copy; potentially inconsistent, explicit warning acknowledgement required |
| Test as Windows service | on | Service name `mysql_uat`; off = standalone process only |
| Admin user / password | `root` / empty | Same fields as step 3; used for flush, upgrade, checks |

Controls: **Run**, **Cancel** (graceful, runs cleanup), **Open folder**, **Delete UAT copy** (confirm dialog).
A stage list shows Pending / Running / OK / FAILED / Cancelled / Skipped with duration; a log pane shows redacted
command lines and exit codes. Credentials and secret config values must also be redacted from diffs and saved reports.
Disable editable options, Run and Delete while running or cleaning up. Closing the window requests cancellation and
waits for cleanup; it must not abandon the task. Use one operation lock across UAT and production MySQL operations.
Source preparation must not stop production, change wizard progression, or persist UAT settings into app settings.

## 4. Stages

Every stage has an explicit success criterion; any failure aborts the run, runs cleanup (section 5) and marks the
remaining stages Skipped.

| # | Stage | Action | Success criterion |
|---|---|---|---|
| 1 | Preflight | Validate paths, effective config, versions, state, privileges, service name, port and disk space (4.2). If production is running and "stop first", validate admin login and privileges | all checks pass without changing production or creating target/service |
| 2 | Copy | If "stop first" and production was running: require verified safe shutdown, copy the whole folder, then **restart production in its prior mode** immediately. If already stopped, leave it stopped. Else perform acknowledged experimental live copy | cold copy matches source manifest; no unreadable/skipped files; production original state verified before proceeding; live copy labelled potentially inconsistent |
| 3 | Rewrite config in copy | See 4.1 | effective data/write paths confined to copy; unique TCP port; external side effects disabled |
| 4 | Old binaries on copy | Start copy's old daemon standalone with its explicit ini; wait for admin login (<= 180 s); verify server identity; request full flush and graceful shutdown | login, flush read-back, process exit and fresh shutdown evidence verified; no kill; otherwise abort before replacement |
| 5 | Deploy new binaries | Preserve the old UAT tree by renaming it to an owned sibling; deploy into a fresh target using the same file-deploy helper as production, then migrate copied data and apply rewritten retained configs | expected manifest; no stale old binaries/DLLs; retained config content unchanged from stage 3; both trees remain for inspection |
| 6 | Start new version | Revalidate isolation and port; install/start owned manual-start `mysql_uat` if selected, otherwise launch standalone | login works; PID, executable, effective basedir/datadir/port belong to UAT; parsed server version/family matches incoming daemon version |
| 7 | Full upgrade | Run the incoming copy's supported upgrade tool using explicit UAT connection options, then sanity checks and one clean restart/login check | actual upgrade performed successfully (not silently skipped); exit 0 and no fatal upgrade errors; InnoDB available; version-specific system-table checks pass; restart/login works |
| 8 | Cleanup and report | Stop owned copy gracefully, remove owned `mysql_uat`, verify production state, persist redacted report | no owned UAT server/service remains and production original state verified; otherwise overall FAILED with recovery instructions |

### 4.1 Config rewrite rules
- Files: every `*.ini` and `*.cnf` under the copy (including `bin\my.ini`, `my.ini`, `data\*.cnf`).
- Replace the old install root with the new root in these forms, case-insensitive: `C:\xampp\mysql`,
  `C:/xampp/mysql`, `C:\\xampp\\mysql`. A match must end at a **path-segment boundary** (end of string, `\`, `/`,
  quote or whitespace), so `C:\xampp\mysql_old` and the new root itself are never rewritten.
- Parse the effective config and include graph, not just text in `[mysqld]`. Handle applicable server/version/service
  groups, duplicate keys, `!include` and `!includedir`. Reject unresolved or external includes before startup.
- Set explicit copy-local `basedir`, `datadir`, temporary directories, error/general/slow/bin/relay logs and PID paths.
  Resolve relative paths against the actual launch context. Reject external writable paths, external tablespaces and
  links that cannot be safely copied; root-string replacement alone does not establish isolation.
- Force the selected port in all effective server/client groups and `bind-address=127.0.0.1`. Disable named pipes and
  shared memory for this TCP-only rehearsal; these options are not all path-valued strings.
- Disable the event scheduler and automatic replication startup using options supported by both tested versions.
  Reject Galera/other external integrations unless their side effects can be safely disabled. Verify isolation again
  before each old/new daemon launch so copied jobs cannot run against production or external systems.
- Preserve encoding and line endings where possible; never rewrite unrelated data files. Log redacted diffs.
- Verify no boundary-matched production root or external writable path remains in effective options. Inspect actual
  server variables after login as well as the static config. Comments and sibling names such as `mysql_old` do not
  fail this check merely because they contain a similar string.

### 4.2 Preflight and copy validity

- Canonicalize absolute paths. Reject equal or overlapping production/target/incoming paths in either direction,
  filesystem roots, existing files/directories at target, and ambiguous junction/symlink/reparse-point layouts.
  Reject linked source content rather than silently skipping it. Validate the selected production ini and all data
  locations; external data is unsupported in this first version. Reserve the target with race-safe creation after
  successful preflight; never overwrite a folder created by another process between checking and copying.
- Capture original state: running/stopped, service or standalone, actual executable/ini/arguments, configured port
  and service identity. An installed service does not prove that it owns a running standalone daemon. Ambiguous
  ownership or unreadable process identity is a hard stop. Confirm port/config from the intended production server.
- Port must be 1..65535 and differ from production. Probe availability by exclusive TCP bind on relevant IPv4/IPv6
  listeners, not by connecting to `0.0.0.0`. Release probes before launch and handle bind races as stage failures.
- In service mode, reject any existing `mysql_uat` (including production using that name) without stopping/removing it;
  require service-management privileges. Register the temporary service for manual start, never automatic startup.
- Budget destination-volume space for the full old copy, fresh deployed package, migrated data if copied again,
  retained trees and a safety margin, using unpacked sizes. Fail enumeration on inaccessible files. Include each
  affected volume rather than assuming source and destination share a drive.
- A normal file-by-file copy of a running database is **not guaranteed crash-consistent**. Live mode is diagnostic
  only; a successful run must retain that qualification. A trustworthy non-stop backup/snapshot workflow is deferred.
- Cold-copy verification uses a manifest of relative paths, lengths and content hashes while production remains
  stopped. Copy failures or verification cancellation still trigger immediate production restoration. Live mode
  reports changed/unreadable files and cannot claim equivalence from file counts/total bytes.

## 5. Safety guarantees

1. The app never replaces, rewrites, renames or deletes production files/config. Production itself writes database
   and log files during flush, shutdown and restart, so a literal "no production file writes" promise is incorrect.
2. If this run attempts to stop running production, record restoration responsibility **before** requesting shutdown.
   A `finally` restores its prior mode on partial shutdown failure, exception or cancel, using a separate bounded
   cleanup budget unaffected by the cancelled run token. Verify readiness against the original endpoint; do not
   advance to stage 3 if restoration fails. Originally stopped production stays stopped. Restoration is an attempted
   and verified recovery, not an unconditional guarantee; failure is prominent with exact manual recovery steps.
3. The copy's processes are identified by **path-segment-safe** matching. (Prerequisite fix: existing
   `TerminateProcessesByName` lacks a trailing separator and can match `mysql_copy_upgrade` when targeting `mysql`.
   In the reviewed working tree, `EnsureNoLingeringDatabaseProcessesAsync` already adds the separator, but treats
   an unreadable process path as belonging to this installation. Fix that unsafe fallback too. Centralize canonical
   path-boundary matching and regression-test sibling names and unreadable identities. UAT must never invoke broad
   force-kill helpers; inability to prove process ownership blocks the operation.)
4. No UAT tree is deleted automatically. Delete requires confirmation listing canonical owned paths and verification
   that all owned daemons are stopped and the owned service is removed. Recheck paths and reject links/production
   overlap at deletion time. Delete only trees recorded by this run, including its retained old tree; an arbitrary
   existing folder that caused preflight to fail must never become eligible for this button.
5. Remove `mysql_uat` only if this run created it and its registered executable/ini identity still matches. Never
   remove a pre-existing/replaced service. Cleanup must run after partial registration failure too. If stopping or
   removal fails, report residual PID/service and verified recovery commands (PowerShell: `sc.exe`, not `sc`).
6. A failed run shows fresh, redacted copy error-log output. Track log offsets/time for each start/shutdown so a
   pre-existing production "Shutdown complete" line in the copied log cannot make a failed shutdown look verified.
7. Cleanup attempts all independent obligations even if one fails. Overall OK requires every stage and cleanup to
   succeed; preserve the original failure alongside cleanup failures. Save the stage report outside the data tree.
   Cancellation has bounded waits and process ownership tracking; it cannot leave an unobserved upgrade child running.
8. A process crash or machine reboot cannot execute `finally`. Persist a non-secret ownership/restoration journal
   outside database data; on the next app start detect unfinished runs and show verified recovery actions. Do not
   automatically kill processes, delete trees/services, or start an originally stopped production instance.

## 6. Architecture

| Unit | Responsibility | Depends on |
|---|---|---|
| `IUatUpgradeService` / `UatUpgradeService` | Runs stages 1-8 against immutable production/UAT target snapshots; tracks ownership and cleanup | target-aware server/tool helpers, deploy helper, read-only settings snapshot |
| `UatIniRewriter` (static, pure) | Path/port rewriting and verification (section 4.1) | none |
| `UatUpgradeViewModel` | Options, stage list, log, commands | `IUatUpgradeService` |
| `UatUpgradeWindow` | UI | view model |
| `MySqlWizardViewModel` + step-1 XAML | New "Real Test Upgrade" button/command that opens the window | existing |

Shared code: extract a destination-explicit deploy helper from `ExecuteUpdatePipelineAsync`, retaining its production
behaviour. Production renames the old tree, deploys into a fresh tree, applies resolved configs and migrates data;
an overlay onto old UAT binaries is not equivalent. Stage 5 follows that layout with the retained config policy.
The helper must define exclusions, data migration and config application, with manifest-based parity tests. This
step-1 UAT tests retained configs plus isolation overrides; it does not test later custom merge choices in the wizard.

Existing helpers are reusable building blocks, **not all reusable as-is**:
- `RunMySqlUpgradeAsync` reads production settings and can start production, even with a UAT executable path.
  Introduce an explicit target (root, ini, service, host, port, expected identity) and tool runner; production callers
  retain their existing defaults. Never temporarily change global settings to redirect an operation.
- Every UAT client/admin/upgrade call must use the copy's executable, explicit TCP `127.0.0.1:<UAT port>` and controlled
  option-file loading. Prevent ambient defaults from selecting production; verify identity before mutation.
- `SafeShutdownMySqlAsync` currently returns true even when login/flush was skipped or log verification was absent.
  Add structured phase results/strict UAT validation; true alone does not satisfy stages 2/4. Keep existing production
  semantics unless an intentional separate change is specified. Restore the original runtime flush setting if a
  failed shutdown changed it but production remained running.
- Service installation currently considers any service with the requested name a success. Add identity/ownership
  verification and manual-start registration for UAT. Server/client runners need bounded cancellation and cleanup.
- Use structured process arguments and preserve password text exactly, including whitespace. Prefer protected
  temporary credential options or the existing password runner; never persist credentials in the recovery journal.

## 7. Error handling

- Folder exists: message "Target folder already exists: <path>. Nothing was changed." and stop.
- Port busy: message names the port; user edits it.
- Login refused: same guidance as the real pipeline (enter credentials, Test Login).
- Any stage failure: stage marked FAILED with the failing command, exit code and relevant log lines; cleanup runs.
- Cleanup/restart failure: overall FAILED even if upgrade passed; show the original production mode and manual
  recovery command. Login/start timeouts and tool waits are bounded and include the last observed identity/state.
- Existing service, unsupported upgrade path, unsafe external paths or inaccessible source: fail preflight clearly.

## 8. Testing

- Keep a focused `XamppUpdate.Tests` project (xUnit). `XamppUpdate.sln` already references this missing project;
  create it at that existing path and use a compatible Windows target. Confirm solution build and test execution.
- Pure tests: root forms/boundaries/idempotence, config groups/includes/duplicate keys, secret redaction, external
  writable paths, effective isolation overrides, ownership path checks and per-volume disk-space calculations.
- Runner/orchestration tests with fake server/tool/filesystem dependencies, not just a fake service manager:
  originally stopped/service/standalone production; partial stop failure; failed restart; cancel/failure at each stage;
  cleanup token independence; pre-existing service untouched; partial service registration cleaned; unreadable
  process identity never killed; folder race; original and cleanup errors retained; live-copy result qualified.
- Deploy parity tests: same incoming manifest/exclusions as production, no stale DLLs, copied data retained, rewritten
  configs retained, no writes to production paths/settings. Assert every mutating SQL/admin/upgrade command targets
  the verified UAT endpoint. Test stale shutdown-log evidence and version-dependent sanity checks.
- Small bind integration checks on IPv4/IPv6; do not fake actual port occupancy as a pure unit test.
- Manual checklist on a disposable representative installation: cold copy with production 3306 and UAT 13306;
  initially stopped production; both production launch modes; failed/cancelled stages; retained/deleted trees;
  service collision; restart and crash recovery report. Confirm effective data/log paths and that copied events and
  replication do not start. Test a production query after recovery and repeat a UAT restart after upgrade.
- Record source/target family and version, package identity, config policy, copy consistency, stage evidence and
  cleanup results in the report. A fixed `mysql.event.definer` type is not a universal criterion: check the documented
  system-table schema for the specific supported target version, marking inapplicable checks explicitly.

## 9. Assumptions

- "Start copying the new mysql file to new dir" means a fresh deployment matching production's rules, preserving
  copied data and rewritten retained ini files, rather than leaving obsolete binaries in place.
- A temporary Windows service on the server is acceptable (confirmed by the user), controlled by a checkbox.
- Single UAT copy at a time; re-running requires deleting the previous copy (stage 1 enforces it).

## 10a. Delivery phases (approved 2026-10-12)

The design above is the design of record. It is delivered in two phases; Phase 1 must be complete and verified
before Phase 2 starts.

**Phase 1 - safe, usable UAT run**
- Prerequisite fixes: path-segment-safe process matching (`TerminateProcessesByName`); remove the "unreadable path
  means ours" fallback in `EnsureNoLingeringDatabaseProcessesAsync`; a structured/strict result from
  `SafeShutdownMySqlAsync` (login, flush read-back, exit, fresh-log evidence); a target-explicit upgrade/tool runner
  so `RunMySqlUpgradeAsync` cannot touch production; service-ownership verification and manual-start registration.
- Create the `XamppUpdate.Tests` project the solution already references; solution builds and tests run.
- Stages 1-8 on a **cold copy** only ("stop production first"), MariaDB-to-MariaDB on a supported path.
- Config rewrite with effective-config isolation; **any `!include`/`!includedir` is rejected** in this phase.
- Stage 5 fresh deploy via the shared deploy helper; ownership-safe cleanup; redacted stage report.
- Copy verification by relative path + length manifest (content hashing is optional/off by default).
- Probe the chosen port on IPv4 loopback with an exclusive bind.

**Phase 2 - hardening and extras**
- Crash/reboot recovery journal (section 5.8) and unfinished-run detection at app start.
- Optional content-hash manifest for cold-copy verification.
- Include-graph resolution (`!include`, `!includedir`) instead of rejecting.
- Experimental live-copy mode with acknowledgement (section 3.2) and its qualified reporting.
- IPv6 listener probing.

## 10. Review references and planning notes

Reviewed against the current working tree on 2026-10-12, which contains uncommitted implementation changes.
Only this specification was changed by the review. Recheck helper contracts when writing the implementation plan.
Prioritize target isolation, restoration/ownership and deploy parity before wiring the UI.

- [MariaDB Windows upgrades](https://mariadb.com/docs/server/server-management/install-and-upgrade-mariadb/upgrading/platform-specific-upgrade-guides/upgrading-mariadb-on-windows)
- [Supported MariaDB upgrade paths](https://mariadb.com/docs/server/server-management/install-and-upgrade-mariadb/upgrading/mariadb-community-server-upgrade-paths)
- [mariadb-upgrade behaviour](https://mariadb.com/docs/server/clients-and-utilities/deployment-tools/mariadb-upgrade)
- [Option-file loading and groups](https://mariadb.com/docs/server/server-management/install-and-upgrade-mariadb/configuring-mariadb/configuring-mariadb-with-option-files)
- [Server options, including replication startup](https://mariadb.com/docs/server/server-management/starting-and-stopping-mariadb/mariadbd-options)
