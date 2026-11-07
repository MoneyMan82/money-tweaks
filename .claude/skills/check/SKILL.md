---
name: check
description: Have the code-reviewer agent inspect part of Money Tweaks for bugs and improvements, read-only. Use when the owner types /check, optionally naming an area (e.g. /check Fix My Game, /check updater).
argument-hint: "[area or feature, e.g. Autopilot, updater, Potato Graphics]"
disable-model-invocation: true
---
Run the `code-reviewer` agent on Money Tweaks. Area from the owner (may be empty): $ARGUMENTS

1. If no area was given, pick the area changed most recently (`git log --name-only -15 --format=` on main) and say
   which one you picked.
2. Find the relevant files (Grep/Glob) and give the reviewer a self-contained brief: the files, the related tests,
   and what to focus on (correctness, undo/restore, concurrency, error handling, safety). Read-only: no edits, builds
   or tests.
3. Before reporting, check the top findings yourself by reading the cited lines, and drop any that don't hold up.
4. Reply with: areas reviewed (one line each), then confirmed findings ranked by severity
   (file:line, what goes wrong, how likely), then improvement suggestions. Say plainly if nothing was found.
   Change no files. End with: "Say `fix <finding>` to have the developer fix it on a branch."
