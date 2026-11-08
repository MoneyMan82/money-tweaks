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
- 2026-11-07 · ci-investigator (cycle 1) · guard · The readonly guard blocked the agent's GitHub API curl, so CI could not be
  checked. Likely cause (unconfirmed): READONLY_BASH only accepts `curl [-sSLf] https://...` without quotes or headers;
  the agent's exact command wasn't reported. Fix needs a `.claude/hooks` change -> owner approval (INBOX).
- 2026-11-07 · coordinator (cycle 1) · guard · False positives: the guard matches command names anywhere in a Bash command,
  so a heredoc that only WRITES text mentioning "powercfg" (or "curl ... | python" in a test script) is blocked.
  Workaround: write team files with Edit/Write, not shell heredocs. Proper fix (owner approval): ignore heredoc bodies.
- 2026-11-07 · tester (cycle 1) · environment · The RULES.md PowerShell game check was blocked by the worktree sandbox; the
  tester used `tasklist` with the same process names. RULES.md could list `tasklist` as the fallback.
- 2026-11-07 · idea-critic (cycle 1) · tweak catalog · The Fortnite GPU-preference DynamicRegOp uses the full exe path
  (with backslashes) as the registry value name - code that splits registry keys at the last `\` breaks on it.
- 2026-11-07 · security-reviewer (cycle 1) · backups · Backup JSON in %LOCALAPPDATA%\MoneyTweaks\backups is user-writable
  and the app may run elevated: never build file paths or registry locations from file contents (TweakId, Id, keys);
  use catalog ids/ops, check containment, refuse reparse points. Tampered VALUES at declared locations remain possible
  (inherent to user-writable backups). App-data root junction not yet checked (T-011).
- 2026-11-08 · owner's session · guard · Gap: during a cycle the session guard blocks Edit/Write outside team/, but a
  shell command that writes files (e.g. a python script) in the main checkout is not blocked. The owner's session did
  this by accident while cycle 2 ran and restored the files with `git stash` (nothing committed). Fix idea (owner
  approval, .claude/hooks): in session mode, block Bash commands that write into the main checkout outside team/.
- 2026-11-08 · owner's session · Command Center · "Tests pass / fail" summed every test run of the day (1486 / 2 =
  16 runs; the 2 failures were fixed minutes later). Change to "latest result per suite" is stashed
  (stash "tests-now stat") and goes in after cycle 2.
- 2026-11-08 · windows-tweak-specialist + idea-critic (cycle 2) · tweak catalog · `Tweak.Editions` is only a display
  string (Tweak.cs:48, set on 2 of 401 tweaks). Enterprise/Education-only Experience CSP policies make `dbl.spotlight`
  (Debloat.cs:126) and `dbl.settings-365-ads` (Expanded.cs:177) read "Applied" on Home/Pro while doing nothing; HKLM
  parts of `dbl.consumer-features`/`dbl.tips` likewise. CSP edition tables have no Home column - "Pro: yes" proves
  nothing for Home · MS Experience/WindowsAI CSP pages · confirmed by critic, task T-015.
- 2026-11-08 · windows-tweak-specialist + idea-critic (cycle 2) · tweak catalog · MMCSS: 3 of `adv.games-task`'s 4
  values do nothing (GPU/SFIO Priority unused; Priority=2 under High). `net.throttling-index` is Recommended + reboot
  although TweakAudit already marks it ineffective · MS MMCSS page, TweakAudit.cs:38 · task T-016.
- 2026-11-08 · idea-critic (cycle 2) · network · Since Windows 11 24H2 `netsh wlan show interfaces` is Access denied
  unless Location services are on - our own location-disabling tweak (Debloat.cs:54) triggers it. Any Wi-Fi feature
  must handle that · https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes.
- 2026-11-08 · ci-investigator (cycle 2) · CI · Repo slug is `MoneyMan82/money-tweaks` (not MoneyTweaks). curl is still
  blocked for read-only agents; WebFetch on api.github.com works. Read-only agents' progress notes are refused when
  chained with other commands (`&&`) - publish them as a standalone command.
