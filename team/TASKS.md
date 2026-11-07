# Task queue

Types: bug (severity critical/high/medium/low) · feature · research · quality · task. Statuses: queued · in progress · in review (tester + security) · needs fixes · awaiting owner approval · merged · cancelled.
Agents never merge; "merged" is set after the owner merges.

| Id | Type / severity | Title | Idea | Owner agent | Status | Branch | Acceptance criteria |
|---|---|---|---|---|---|---|---|
| T-001 | task | First scouting run: rank 5-10 new ideas against the current app | – | innovation-scout | done (2026-11-07: I-006..I-010) | – | Each idea has evidence links, the files/systems it reuses, effort, risk, and is not a duplicate; added to IDEAS.md |
| T-002 | research | Research I-003 (GPU preference per exe): key, values, Windows builds, undo | I-003 | researcher | queued | – | Microsoft source linked; notes how it interacts with the existing Fortnite dGPU tweak |
| T-003 | bug / high | Autopilot: one async gate for Start/End/EndNow/Shutdown; StartAsync re-checks its session and Enabled after awaits (review findings 1, 2, 4, 7) | – | developer | queued | – | End during Start can't leave settings applied; no duplicate history; watcher can't stop mid-start; tests for the re-entrancy |
| T-004 | bug / medium | Gaming Profiles: keep the session record when rollback fails; ProfileStore.Save via AtomicFile (findings 5, 6) | – | developer | queued | – | Failed rollback stays recoverable at next start; profiles.json written atomically; tests |
| T-005 | bug / medium | Autopilot recovery checks process identity (start time + path), not just the PID (finding 3) | – | developer | queued | – | A reused PID after reboot ends the session instead of resuming it; test with mismatched identity |
| T-006 | bug / high | Tweak engine: match backups to ops by identity, merge (not replace) when the op list changed, never delete a pre-existing backup on rollback (engine review findings 1, 2) | – | developer | queued | – | Undo after a catalog change restores true originals; failed re-apply keeps the old backup; tests with reordered/added ops |
| T-007 | bug / high | Tweak engine: roll back the failing step too (partial DynamicRegOp/CommandOp); DynamicRegOp.Undo fails loudly when discovery finds nothing (findings 3, 6) | – | developer | queued | – | Multi-target op failing on its last target leaves nothing changed; test |
| T-008 | bug / medium | RegStringFlagOp undo restores only its own flag; BatchAsync try/finally; RegValueOp.Check compares value kind (findings 7, 4, 8) | – | developer | queued | – | Settings › Graphics changes survive undo; a throwing item doesn't stall a batch; REG_SZ "1" isn't "applied" for a DWORD tweak; tests |
