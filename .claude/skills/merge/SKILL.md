---
name: merge
description: Owner-approved merge of an agent branch into main, followed by the normal Money Tweaks release flow. Only when the owner types /merge <branch> themselves - this command is the owner's approval for that one branch.
argument-hint: "<branch name from team/INBOX.md>"
disable-model-invocation: true
---
The owner approved merging this branch: $ARGUMENTS
(If empty, show the "Ready for approval" items in `team/INBOX.md` and ask which one.)

This approval covers only this branch, and is done in the owner's main session (not through an agent).
0. If `.claude/autonomous-run` exists, check no cycle is running right now (its time is older than 4 hours, or the
   scheduled task isn't running); if one is running, wait for it or ask. Then delete the marker so the guard is off.
1. Show what will be merged: `git log --oneline main..<branch>`, `git diff --stat main...<branch>`, and the branch's
   INBOX entry. If tester and security-reviewer results are missing, run both first; stop on failures or BLOCK.
2. Game check (team/RULES.md). `git merge --no-ff <branch>`; resolve conflicts keeping both sides' features. Never
   force-push or rewrite history.
3. Build and run all test suites. If anything fails, fix it or stop and report; don't push a red main.
4. Normal release flow: move the CHANGELOG "Unreleased" entry under the new version, bump the version past the newest
   remote tag, check README/website counts, push main, wait for green CI, tag, verify the release assets and website.
5. Update `team/INBOX.md` (remove the item), `team/TASKS.md` (merged), `team/IDEAS.md` (done), `team/STATE.md`, and
   append to `team/LOG.md`; commit and push.
Report the version, release link and what changed for users.
