# Task queue

Statuses: queued · in progress · in review (tester + security) · needs fixes · awaiting owner approval · merged · cancelled.
Agents never merge; "merged" is set after the owner merges.

| Id | Title | Idea | Owner agent | Status | Branch | Acceptance criteria |
|---|---|---|---|---|---|---|
| T-001 | First scouting run: rank 5-10 new ideas against the current app | – | innovation-scout | queued | – | Each idea has evidence links, the files/systems it reuses, effort, risk, and is not a duplicate; added to IDEAS.md |
| T-002 | Research I-003 (GPU preference per exe): key, values, Windows builds, undo | I-003 | researcher | queued | – | Microsoft source linked; notes how it interacts with the existing Fortnite dGPU tweak |
| T-003 | Autopilot: one async gate for Start/End/EndNow/Shutdown; StartAsync re-checks its session and Enabled after awaits (review findings 1, 2, 4, 7) | – | developer | queued | – | End during Start can't leave settings applied; no duplicate history; watcher can't stop mid-start; tests for the re-entrancy |
| T-004 | Gaming Profiles: keep the session record when rollback fails; ProfileStore.Save via AtomicFile (findings 5, 6) | – | developer | queued | – | Failed rollback stays recoverable at next start; profiles.json written atomically; tests |
| T-005 | Autopilot recovery checks process identity (start time + path), not just the PID (finding 3) | – | developer | queued | – | A reused PID after reboot ends the session instead of resuming it; test with mismatched identity |
