---
name: ideas
description: Ask the innovation-scout agent for new Money Tweaks feature ideas and add them to the ideas backlog. Use when the owner types /ideas (optionally with a topic, e.g. /ideas networking).
argument-hint: "[optional topic, e.g. networking, FPS, laptops]"
disable-model-invocation: true
---
Run the `innovation-scout` agent to find 5 original, evidence-backed Money Tweaks ideas.
Topic from the owner (may be empty): $ARGUMENTS

1. Brief the scout: read `team/RULES.md`, `team/IDEAS.md` (don't repeat existing ideas), `PROGRESS.md` and
   `src/MoneyTweaks.Core/Model/AppNavigation.cs`; grep the code to confirm each idea is new; focus on the topic if one
   was given; for each idea give problem + evidence, reused systems, effort S/M/L, risk, safety notes; read-only.
2. Add the ideas to `team/IDEAS.md` with the next free ids (I-0xx), status "new", and append a short dated entry to
   `team/LOG.md`. Change no other files.
3. Reply with a small table: id, idea, effort, one-line why. Mark anything the scout said it could not verify.
   End with: "Say `/build <id>` to have the team build one."
