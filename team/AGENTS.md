# Agent registry

Definitions live in `.claude/agents/<name>.md`. Every agent reads `team/RULES.md` first. Read-only agents run the guard
in `readonly` mode (inspection commands only, no file writes); coding agents run in `developer` mode inside their own
worktree under `.claude/worktrees/`; the tester runs builds/tests in its worktree but can't edit or commit.

## Lead
| Agent | Owns | Access | Model |
|---|---|---|---|
| chief-coordinator | The cycle (`team/CYCLE.md`), choosing the next work, delegating, team files | edits `team/` only | opus |

## Discovery and research (read-only)
| Agent | Owns - and does NOT do | Model |
|---|---|---|
| innovation-scout | Ideas from inside the app, user pain points, platform changes. Not competitor comparison. | sonnet |
| competitor-analyst | What other tools do/do badly, feature gaps, their licenses. Not fact-checking tweak values. | sonnet |
| researcher | Technical questions: .NET/WPF/Windows APIs, libraries, formats, docs. Not tweaks, games, hardware. | sonnet |
| windows-tweak-specialist | Every tweak's correctness: keys, types, builds, editions, undo, placebo, duplicates; catalog audits. | opus |
| gaming-performance-analyst | FPS/latency/ping reality checks, measurement design, game configs, anti-cheat limits. | opus |
| hardware-specialist | Sensors, tuning, fans, power, displays, vendor APIs and safety limits. | opus |
| idea-critic | Devil's advocate and rubric score for every idea and design (SCORING.md). | opus |
| architect | Feasibility and implementation design reusing existing systems. | opus |

## Building (own worktree, own branch, never push/merge)
| Agent | Owns | Model |
|---|---|---|
| developer | C# / .NET 10: Core engine, catalog, services, WPF view logic, tests | opus |
| ui-designer | WPF look: layout, theme, animation, accessibility | opus |
| web-developer | Cloudflare Worker (`worker/`), website markup/CSS/JS (`docs/`), Community contracts | opus |
| docs-writer | CHANGELOG, README/website text and counts, TWEAK-SOURCES rows, PROGRESS notes | sonnet |

## Verification (read-only unless noted)
| Agent | Owns | Model |
|---|---|---|
| tester | Builds and runs every test project in its worktree; reports bugs (no edits) | sonnet |
| code-reviewer | Bugs and fragile logic in diffs and existing code | opus |
| security-reviewer | Safety/vulnerability verdict on every branch | opus |
| license-auditor | Dependencies, assets, attribution, copied code | sonnet |
| ux-reviewer | Flows, wording, discoverability, accessibility from the user's view | sonnet |
| ci-investigator | GitHub Actions results, failure causes, release assets | sonnet |

## Continuous improvement (read-only)
| Agent | Owns | Model |
|---|---|---|
| process-improver | How the team performs; proposes changes to process, knowledge and agents (every 3rd cycle) | sonnet |

## Adding a new agent
1. Check this table: if an existing agent almost covers the need, extend its instructions instead.
2. Copy the closest agent file in `.claude/agents/` and change `name`, `description` (say when to use it PROACTIVELY
   and what it does NOT do), `tools`, `model`, the guard mode in the hook (`readonly` for research/review, `developer`
   plus `isolation: worktree` for coding), and the instructions.
3. Add it to this registry, to the `Agent(...)` list in `chief-coordinator.md`, and to CYCLE.md where it fits.
4. New agent files load when a Claude Code session starts; start a new session to use it.
