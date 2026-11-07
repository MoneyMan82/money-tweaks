# Idea scoring rubric

Used by `idea-critic` for every idea and by the coordinator to pick the next build. Scores are 1-5.

## Gates (any "no" = REJECT, score not needed)
- **Safe:** no weakening of Windows security, no anti-cheat interaction, no hardware-damage risk, nothing irreversible
  without saying so, no downloaded scripts, no personal data leaving the PC.
- **Free and honest:** no paid service, account or telemetry needed; no placebo; results can be shown truthfully.
- **New:** not a duplicate of an existing page, tool, tweak or setting (if it overlaps, rewrite it as "improve X").

## Criteria
| Criterion | Weight | 5 means | 1 means |
|---|---:|---|---|
| User value | 3 | Most users (gamers, low-end PCs, laptops) notice a real benefit | Niche or cosmetic |
| Evidence | 2 | Primary sources and real user demand (issues, forums, competitor gaps) | Assumption only |
| Measurability | 2 | The app can show the effect on the user's own PC | Effect can't be shown |
| Feasibility | 2 | Fits existing systems cleanly, known APIs, testable | New subsystem, unknowns, hard to test |
| Effort | 1 | S (one cycle) | L (several cycles; must be split) |

Total = sum(score x weight), maximum 50.
- **BUILD:** total >= 34 and no criterion below 2. Build the smallest scope the critic named.
- **REWORK:** 26-33, or one criterion at 1 - say what would raise it (more evidence, smaller scope).
- **REJECT:** below 26 or a failed gate. Record the reason so it isn't proposed again.

Ties: prefer bug fixes over features, improving an existing feature over a new page, S over M, and anything that
makes existing tweaks safer or better verified.

## The "next idea" questions (asked by the coordinator every cycle - see CYCLE.md Step 2)
Beyond the decision order, when choosing between candidates also ask:
- What do users of Money Tweaks complain about or struggle with most (logs, FINDINGS, issues)?
- What do the best competitors do that we still don't, and can we do it more honestly?
- What changed recently in Windows, GPU drivers or popular games that affects our users?
- Which existing feature is the weakest or least verified, and what would make it trustworthy?
- What would make the app easier for a first-time, non-technical gamer?
