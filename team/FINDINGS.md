# Shared findings board

Durable findings that more than one agent needs. The coordinator adds entries from agent reports and passes the
relevant ones in briefs. Newest at the bottom. Format: date · agent · area · finding · evidence · status.

- 2026-11-07 · code-reviewer · tweak engine · Undo matches backups to ops by position; op-list changes replace (and on
  failed re-apply delete) the original backup; failing step not rolled back; RegStringFlagOp.Undo restores the whole
  string; BatchAsync lacks try/finally · TweakEngine.cs:113/131-147/191, RegStringFlagOp.cs:68, TweakItem.cs:399-408 ·
  confirmed by owner's session, tasks T-006..T-008.
- 2026-11-07 · code-reviewer (stand-in) · Game Autopilot / Profiles · End doesn't wait for an in-flight Start; failed
  rollback drops the session record; ProfileStore.Save not atomic; recovery trusts the PID alone ·
  AutopilotService.cs:117-235, ProfileService.cs:59-60, GamingProfiles.cs:115-121, GameAutopilot.cs:147-150 ·
  confirmed, tasks T-003..T-005.
- 2026-11-07 · innovation-scout · ideas · Five ideas I-006..I-010; registry keys and event IDs cited from memory, not
  verified · needs specialist verification before building.
- 2026-11-07 · environment · cloud · Cloud routine runs fail: GitHub App lacks repo access, devblogs/learn.microsoft.com
  blocked by the network allowlist, no .NET SDK. Local cycles don't have these problems.
