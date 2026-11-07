# Design T-006 + T-007: backup identity, merge on op-list change, roll back the failing step, safe DynamicRegOp undo

Author: architect (cycle 1, 2026-11-07). Status: awaiting idea-critic review.

## Verdict
Fixable entirely in Core; no new UI, admin or restart needs; only result messages change. Deliberately NOT done:
- No guessing on old backups: a pre-1.80 backup (no op identity) that can't be aligned is never applied; Undo refuses and
  keeps the file byte-identical; it is archived only when nothing from the tweak is active.
- Never write registry locations taken from a backup file except inside a scope declared in code (backup JSON is
  user-writable and the app may run elevated - writing file-supplied locations would be a privilege escalation, e.g.
  IFEO or service ImagePath). This also rules out "restoring" orphaned entries from their stored Id.

More positional readers exist than in the findings: TweakVerifier.RestoreMismatches (:317), TweakVerifier.Verify (:94),
TweakXRay.Inspect (:70/75), EmergencyRescue.Scan/Values (:83/100/116). All move to the shared matcher.

## 1. Op identity
- `TweakOp`: `public virtual string BackupKind => GetType().Name;` and `public virtual string OpKey => $"{BackupKind}|{Describe()}";`
  (virtual so test ops FailingOp/RevertedOp still compile).
- Overrides (Capture uses BackupKind; registry paths trimmed of `\`, compared OrdinalIgnoreCase):
  RegValueOp `reg|{Hive}\{Path}\{Name}` · RegStringFlagOp `regflag|{Hive}\{Path}\{Name}|{Flag}` · ServiceStartOp
  `service|{Service}` · PowerCfgOp `powercfg|{SettingKey}` · ScheduledTaskOp `task|{TaskPath}` · AppxRemoveOp
  `appx|{PackageName}` · CommandOp `command|{ApplyCommand}` · DynamicRegOp `dynreg|{Id ?? label}` (new optional init `Id`).
  The target value is not part of the key (0 -> 1 in an update keeps the same original).
- Storage: `OpBackup.Id` (string?, JSON "Id") in `%LOCALAPPDATA%\MoneyTweaks\backups\<id>.json`. The engine stamps
  `ob.Id = op.OpKey` after every Capture. Old files deserialize with Id = null; older app versions ignore the property.
  The saved list is in current op order.
- New `TweakEngine.CaptureAll(Tweak)`: used in Apply, TweakVerifier.RoundTrip (:363), EmergencyRescue.RestoreOne (:227).

## 2. Matching (new `src/MoneyTweaks.Core/Engine/BackupMatch.cs`)
```csharp
public sealed record BackupMatch(OpBackup?[] ForOp, IReadOnlyList<OpBackup> Orphans, bool Ambiguous, bool Legacy)
{ public bool Complete => !Ambiguous && Orphans.Count == 0 && ForOp.All(o => o is not null); }
public static class BackupMatcher { public static BackupMatch Match(Tweak t, TweakBackup? b); }
```
- No backup: all null, not ambiguous.
- All entries have Id: k-th op with key X pairs with the k-th entry with Id X (handles duplicates). An unmatched
  DynamicRegOp receives the merged Data of all unmatched `dynreg` orphans (survives a label change; undo only acts on
  keys discovery or scope allows). Remaining entries are Orphans.
- Any entry without Id (legacy): positional only if counts equal AND every `b.Ops[i].Kind == t.Ops[i].BackupKind`
  (Legacy=true); otherwise Ambiguous=true, all null.

## 3. Apply (replaces TweakEngine.cs:110-169)
1. TryLoad as today (damaged still refuses). `existed = backup is not null`; `m = Match(t, backup)`.
2. Ambiguous: if any op Check() is Applied/Unknown -> refuse, change nothing, new `AmbiguousBackupMessage` ("This tweak's
   steps changed in an app update and its older backup can't be matched to them safely, so nothing was changed. Your
   original values are kept in <id>.json..."). Else `BackupStore.Archive(t.Id)` -> `backups\orphaned\<id>.<yyyyMMdd-HHmmss>.json`
   (AllIds lists only the top level) and continue with existed = false.
3. Pre-click snapshot `now = CaptureAll(t)` (rollback only). Capture throws -> "Could not back up current settings".
4. Stored backup = for each op `m.ForOp[i] ?? now[i]` (Id stamped) + `m.Orphans`; keep old AppliedAt; save atomically
   before any change. Matched entries are never re-captured (fixes T-006b). X-Ray "before" log only for new entries.
5. `preApplied[i]` only for CommandOp with HasCheck.
6. `reached = i` before each Apply(); on exception `Rollback(t, now, last: reached, failed: reached)`.
7. Rollback: reverse from `last` to 0, undo each to its `now` snapshot. The failing step is undone only when safe:
   exact-restore ops (reg, regflag, service, powercfg, task, dynreg) always; CommandOp only if HasCheck and after
   InvalidateCache() Check()==Applied (its undo writes defaults, not originals); AppxRemoveOp only if the snapshot said
   installed and now it isn't. CommandOp with preApplied[j] is skipped. Errors collected.
8. Delete rule (both failure paths): `BackupStore.Delete` only when `!existed && rollbackErrors.Count == 0`. A pre-existing
   backup is never deleted; a fresh one is kept if rollback failed ("Rolling back step N failed; your original values
   are kept, use Undo.").
9. Read-back failure (:159): `Rollback(t, now, last: Count-1, failed: null)`, same delete rule.

## 4. Undo (TweakEngine.cs:186-212)
- Ambiguous: all ops NotApplied/NotApplicable -> Archive, success ("Nothing from this tweak is active; the older backup
  was moved to backups\orphaned"). Otherwise refuse, write nothing, file byte-identical, message also says "delete the
  file to fall back to Windows defaults" (like DamagedBackupMessage).
- Per op, reverse: no backup at all -> Undo(null) (Windows defaults, as today); backup exists but op unmatched -> skip
  (step added after Apply; never changed by Money Tweaks); else Undo(m.ForOp[i]).
- Guard in RegValueOp.Undo and RegStringFlagOp.Undo: `backup?.Id` set and != OpKey -> throw TweakOpException.
- Success and identity-aware RestoreMismatches clean: orphans -> AtomicFile to `backups\orphaned\<id>.<ts>.json`, logged,
  message adds "N steps removed in an app update were not undone; their saved values are in backups\orphaned"; then
  delete the backup.

## 5. DynamicRegOp (Ops/SystemOps.cs)
- `public sealed record RegScope(Hive Hive, string PathPrefix, string? ValueName, string? PathSuffix = null)` with
  `Contains(hive, path, name)`; `public RegScope[] Scope { get; init; } = [];`. Set in code on all 9 catalog DynamicRegOps:
  TcpAckFrequency/TCPNoDelay (Tcpip Interfaces prefix + value name), MSISupported (`SYSTEM\CurrentControlSet\Enum\` +
  suffix `MessageSignaledInterruptProperties`), SvcHostSplitThresholdInKB, *EEE (NicClass prefix), NetbiosOptions (NetBT
  Interfaces prefix), NVIDIA (exact path+name), Fortnite (HKCU GpuPrefs, any name), launchers (HKCU ApprovedRun, any name).
- Apply: resolve targets once; capture each in memory before writing; on any exception at target k undo k..0 from those
  captures (catch Exception: a bad DWord throws FormatException), rethrow TweakOpException with the cause (mention inner
  undo failures). Engine rollback is the second layer.
- `internal RestorePlan Plan(OpBackup? b)` shared by Undo and the verifier: discovery records DiscoveryError (no
  swallowing). Discovered target paired with its backup entry by `Hive\Path\Name`; discovered target without an entry
  included only if it reads Applied (Undo(null) as today), else skipped. Backup keys not discovered: parse Hive (before
  first `\`) and Name (after last `\`); key gone -> drop; already original -> drop; inside Scope -> restore via a rebuilt
  RegValueOp; else -> Unresolved.
- Undo: DiscoveryError and Unresolved non-empty -> throw before any write (message lists values; engine keeps the
  backup). Else restore the plan.

## 6. Verifier and other readers
- RestoreMismatches: BackupMatcher; Ambiguous -> one difference ("backup can't be matched to this tweak's steps"); skip
  unmatched ops when a backup exists; DynamicRegOp branch: for each Plan item read the value directly and compare kind
  and value with the backup ("absent" must read absent); DiscoveryError + Unresolved count as differences; drop the
  discovery-based Capture() diff for dynreg (fixes the absent-original blind spot :333-342).
- Verify (:94): `!m.Complete` -> warning "Backup from an older version doesn't match every step".
- TweakXRay.Inspect (:70-89): use m.ForOp[i]; applied = backup != null && !m.Ambiguous; ambiguous text "...Undo refuses to guess".
- EmergencyRescue: Scan -> OlderVersion when !m.Complete; Values -> m.ForOp[i]; StatusText (:28) "backup from an older
  version doesn't match every step". Rescue exe gets this via Core.

## Files
New `Engine/BackupMatch.cs`. Changed: `Ops/TweakOp.cs`, `Ops/RegValueOp.cs`, `Ops/RegStringFlagOp.cs`,
`Ops/AppxRemoveOp.cs`, `Ops/SystemOps.cs`, `Engine/TweakEngine.cs` (BackupStore.Archive/ArchiveOrphans, CaptureAll,
Apply, Undo, Rollback, messages), `Engine/TweakVerifier.cs`, `Engine/TweakXRay.cs`, `Engine/EmergencyRescue.cs`, catalog
scopes in `Catalog/TweakCatalog.NetAdv.cs`, `.Extra.cs`, `.Apps.cs`, `.Expanded.cs`.

## Tests - new `tests/MoneyTweaks.Tests/BackupIdentityTests.cs`, `[Collection("registry")]`, EngineTests pattern
AppPaths.OverrideRoot(temp); HKCU `Software\MoneyTweaksTests\BackupIdentity` deleted in ctor and Dispose; inline test
tweaks (two Tweak records with the same Id and different Ops simulate an update); legacy backup = raw JSON without "Id"
written to BackupStore.PathOf(id); failing target = `new RegValueOp(HKCU, key, "Bad", DWord, "not-a-number")`; broken
discovery = captured `bool broken` in the discover lambda; `HalfOp` test op wrapping a RegValueOp (Apply writes then
throws; variant whose Undo throws). Each fails before the fix (except 4):
1. Undo_after_reorder_restores_each_original_to_its_own_value (A=1,B=2; v1 [A=10,B=20]; v2 [B,A]; Undo -> A=1,B=2)
2. Undo_after_inserted_step_leaves_the_new_step_untouched (C=5 pre-exists; v2 [C=50,A,B]; Undo -> C=5,A=1,B=2)
3. Legacy_backup_with_changed_steps_is_refused_and_kept_byte_for_byte (+ kinds-swap variant)
4. Legacy_backup_with_matching_steps_still_undoes (compat guard)
5. Legacy_unmatchable_backup_with_nothing_active_is_archived_on_apply
6. Reapply_after_a_step_was_added_keeps_true_originals (v1 [A=10]; v2 [A,B]; Apply v2 then Undo -> A=1, B=2)
7. Failed_reapply_keeps_the_existing_backup_and_pre_click_state (v2 [A, FailingOp])
8. Failing_step_itself_is_rolled_back (HalfOp)
9. Failed_rollback_keeps_the_fresh_backup
10. Multi_target_dynamic_op_failing_on_last_target_changes_nothing (direct op.Apply and TweakEngine.Apply)
11. Dynamic_undo_restores_from_backup_when_discovery_fails (scoped)
12. Dynamic_undo_without_scope_fails_loudly_when_discovery_fails
13. RestoreMismatches_sees_dynamic_values_when_discovery_fails
14. Removed_step_backup_is_archived_on_undo
15. Failed_command_without_check_is_not_reversed (marker value absent afterwards)
CatalogTests additions: op keys unique per tweak; OpKey starts with BackupKind + "|"; every catalog DynamicRegOp has a
non-empty Scope and every target it discovers on this PC (read-only Resolve()) is inside it; BackupMatcher pure unit
tests (duplicate keys, mixed/legacy entries, dynreg orphan merge).
Must stay green: EngineTests.Engine_rolls_back_when_a_step_fails, CompatibilityTests.Failed_step_rolls_back...,
VerificationTests.Apply_rolls_back_when_read_back_does_not_match, EmergencyRescue and UninstallRestore backup tests.

## Risks
Legacy same-kind reorders still match positionally (no data to detect) · ambiguous legacy backups with active steps now
refuse Undo (way out: documented file delete; Rescue reports it) · orphaned steps keep their tweaked value (restoring
would trust file locations) · discovery race (existing; now visible) · value names containing `\` (none in catalog) ·
a CommandOp already active before the click is skipped in rollback.

## Security review points
Only code-declared Scope locations are written from backup data; Id only used for matching; Enum.Parse of a tampered kind
throws into the existing error path; archive uses File.Move without overwrite + timestamp; nothing leaves the PC; no
elevation added.

## Docs
CHANGELOG "Unreleased -> Fixed"; PROGRESS gotchas (backup format v2, matching rules, backups\orphaned, Scope required for
new DynamicRegOps); ARCHITECTURE.md known-weakness pointer (coordinator). No README/website/count changes.

## Steps (tests first in each)
1. OpBackup.Id, BackupKind/OpKey on all ops, CaptureAll, CatalogTests uniqueness/prefix.
2. BackupMatch.cs + pure tests; BackupStore.Archive/ArchiveOrphans.
3. Apply: merge, snapshot, Rollback incl. failing step, delete rule (tests 5-9, 15).
4. Undo: matcher, skip unmatched, ambiguous refuse/archive, orphans; Id guard (tests 1-4, 14).
5. DynamicRegOp: RegScope, atomic Apply, Plan, Undo, catalog scopes (tests 10-12 + catalog scope test).
6. Verifier/XRay/EmergencyRescue + CaptureAll in RoundTrip and Rescue (test 13).
7. Full build and all three test projects, exact counts.

## Acceptance criteria
Tests 1-15 pass and 1-3, 5-14 fail on current main; existing suite green; reorder/insert/remove never writes one op's
original to another setting; a pre-existing backup is never deleted or replaced on any failure path; a failed Apply
leaves the pre-click state including the failing step and every dynamic target; Undo never reports success when a
dynamic value wasn't restored; old backups either match by count and kinds or are kept untouched; no registry location
outside code-declared ops or scopes is ever written.

---
## Amendments after idea-critic review (BUILD, 38/50) - these OVERRIDE the sections above
Status: approved for implementation, split into two branches.

### Required changes
1. **Backslashes in value names (Fortnite).** The Fortnite DynamicRegOp (TweakCatalog.Extra.cs:58-61) uses the full exe
   path as the value name under HKCU UserGpuPreferences. Never split backup keys at the last `\`. Find the Scope whose
   `Hive\Path` is the prefix; for exact-path scopes the name is everything after `Hive\Path\`; for fixed-ValueName scopes
   the name is that value. Add an `Exact` (exact path, no subkeys) flag to RegScope; use it for UserGpuPreferences,
   ApprovedRun and NVIDIA. Optional: new backups store path and name explicitly. Test: variant of test 11 with a `\` name.
2. **Capture must not swallow discovery failures.** DynamicRegOp.Capture (SystemOps.cs:338/:351) throws when discovery
   fails, so Apply refuses with "Could not back up current settings" (EmergencyRescue per-tweak snapshot then skips that
   tweak with its existing message - acceptable). Test: Apply with broken discovery leaves no backup and changes nothing.
3. **Re-apply records originals of newly discovered dynreg targets.** Matched dynreg entry = old Data + keys from the new
   capture not already in old Data (old values always win). Test: Apply v1 (target A), add target B, Apply, Undo -> B
   gets its own original back.
4. **No Undo-side archive for unmatchable legacy backups.** Undo with an ambiguous backup always refuses and keeps the
   file (message names the file and the way out, like DamagedBackupMessage; Rescue status says the same). The Apply-side
   archive stays, but "inactive" means: every op NotApplied; an AppxRemoveOp reading NotApplicable counts as ACTIVE;
   Unknown counts as ACTIVE.
5. **Command steps.** Unmatched command steps pair with unmatched `command` orphans in order (the undo command comes from
   code, not the file, so nothing can be written wrongly). Optional key `command|{Label ?? ApplyCommand}`.
6. **Failing-step rollback false errors.** If undoing the failing step throws, compare Capture() with its snapshot; equal
   = not an error. Replace the type switch for exact-restore ops with `public virtual bool UndoRestoresSnapshot => false;`
   on TweakOp, overridden true by the reg, regflag, service, power-setting, task and dynreg ops.
- Pin BackupKind overrides to the exact strings existing backups use (reg, regflag, service, the power-setting kind,
  command, task, appx, dynreg - read the current Capture() code for each) with a pure test.
- Changelog/notes: legacy backups whose same-kind steps were reordered still match positionally (accepted).
- Security reviewer note: a tampered backup inside the MSISupported scope could turn MSI on for any device (black-screen
  risk, not privilege escalation).

### Scope cuts
- Cut the dynreg orphan merge. Instead `Id` is required on all 9 catalog DynamicRegOps (CatalogTest: non-empty Id).
- Cut the Undo-side archive (see 4). Keep the Apply-side archive and the orphan archive after a successful Undo.
- Catalog scope test: add pure tests that build representative paths from the catalog constants and check them against
  each Scope (discovery finds little on CI runners).

### Split
- **Branch A (cycle 1): T-006 + T-007a.** Steps 1-4, and step 6 except the DynamicRegOp branch; amendments 4, 5, 6 and the
  BackupKind pin; DynamicRegOp gets `Id` (required, catalog test) and `UndoRestoresSnapshot`, but not yet Scope/Plan.
  Tests 1-9, 14, 15 + new tests for amendments 4-6 and the BackupKind strings.
- **Branch B (next cycle): T-007b.** Step 5 (RegScope with Exact, atomic DynamicRegOp Apply, Plan, Undo, catalog scopes),
  the RestoreMismatches dynreg branch, amendments 1-3. Tests 10-13 + new tests for amendments 1-3.
