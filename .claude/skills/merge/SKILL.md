---
name: merge
description: Owner-approved merge of an agent branch into main, followed by the normal Money Tweaks release flow. Only when the owner types /merge <branch> themselves - this command is the owner's approval for that one branch.
argument-hint: "<branch name, e.g. claude/cycle-20261107-0856>"
disable-model-invocation: true
---
The owner approved merging this branch: $ARGUMENTS
(If empty, list unmerged agent branches - `claude/cycle-*` on origin and local worktree branches - and ask which one.)

This approval covers only this branch. Do this in the main session (not through an agent):
1. Show what will be merged: `git log --oneline main..<branch>` and `git diff --stat main...<branch>`. If the
   branch has no tester and security-reviewer results in `team/LOG.md` or its PR, run both agents on it first and
   stop if tests fail or security blocks.
2. Run the game check (team/RULES.md). Merge without rewriting history (`git merge --no-ff <branch>`); resolve
   conflicts carefully, keeping both sides' features. Never force-push.
3. Build and run the full test suite. If anything fails, fix it on main or stop and report; don't push a red main.
4. Follow the normal release flow: bump the version past the newest remote tag, update CHANGELOG/README/website
   counts, push main, wait for green CI, tag, and verify the release assets and the website.
5. Mark the task "merged" in `team/TASKS.md`, the idea "done" in `team/IDEAS.md`, append to `team/LOG.md`, and commit.
Report the version, release link and what changed for users.
