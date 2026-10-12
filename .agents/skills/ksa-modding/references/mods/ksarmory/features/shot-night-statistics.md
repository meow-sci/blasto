---
type: Mod Feature
title: Compare mod variants in flight with interleaved batches and rank statistics
description: A batch harness flies unattended ballistic shots of every prebuilt variant in seeded interleaved order with the deployed DLL hashed per shot, and a report judges arms with an exact Wilcoxon rank-sum test, Hodges-Lehmann effects, a stopping rule and within-run splits
mod: ksarmory
category: testing-and-tooling
techniques: [interleaved-batches, rank-statistics, reproducible-builds, within-run-split, reflection]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: []
tags: [statistics, a-b-testing, experiment-design, harness, wilcoxon, reproducibility]
---

A mod whose behaviour is only measurable in flight has a noise problem: in KSArmory's ballistic
shots the miss on an identical setup scatters by a factor of 1.7 either side of a 0.79 km median,
so one shot each way settles nothing and a 300 m difference needs about twenty-five shots an arm.
The repository treats this as an experiment-design problem and automates it: `shot-batch.sh` flies
a night of unattended shots under a strict protocol, `shot-report.py` decides what the night
settled, and two within-run designs (split the rockets, or split the tubes of one bus) remove the
between-run noise when the question allows.[^shot-batch][^protocol]

## How it works

### The batch (`tools/shot-batch.sh`)

| Rule | Mechanism and reason |
|---|---|
| Build every arm once, up front | Each arm (`base=HEAD`, `x=arm/x`, ...) is built and stashed before the first flight; nothing reads the working tree during the night, so an edit at 3 a.m. cannot reach a shot in flight[^shot-batch] |
| Prove which arm flew | The build is byte-reproducible; the deployed DLL's SHA-256 is recorded per shot and checked against the arm's stash before launch[^shot-batch] |
| Interleave | Arms run in a seeded random order inside each block, so drift over the night (thermal throttling, background services, a fragmenting session) becomes noise shared by all arms[^shot-batch] |
| Keep every log | `scenario.sh` truncates the mod log at launch, so each shot's log is copied out before the next starts[^shot-batch] |
| One batch at a time | A lock, because two batches sharing one install and mods folder produce shots belonging to neither[^shot-batch] |
| Resume, plan only | `--resume <dir>` continues after an interruption; `--plan-only` prints the order and stops[^shot-batch] |

### The verdict (`tools/shot-report.py`)

- The endpoint is the group's **mean miss on a log scale** (not the worst warhead, not the
  arithmetic mean: the distribution has a heavy right tail).
- Arms are compared with **Wilcoxon rank-sum under an exact permutation null**, and the effect is
  the **Hodges-Lehmann** median pairwise log-ratio with a distribution-free interval, so
  "unresolved" is a result with a width.
- Only the **baseline flown the same night** is compared against; older nights are printed for
  drift only.
- `--instrument` after the first shot checks the trace is recording (fatal if not); `--gate`
  mid-batch names arms to drop (removal only); the morning report gives the verdict per arm.
- Further endpoints (`signed-walk`, `signed-cross`, `spread`, `landing`, `centre`, `dispersion`,
  `--per-seat`) exist for terms a ratio cannot represent.[^shot-report][^protocol]

### Within-run splits

- **`--paired` (split the rockets).** One build, a save with several rockets, and each craft gets
  its own variant through `Sim/ShotArms.cs`, which parses a one-line spec and applies each
  setting to that craft's `IcbmConfig` by name with `typeof(IcbmConfig).GetField(..., IgnoreCase)`.
  Frame pacing, warp history, solver load and weather are shared by construction. It was adopted
  after the same baseline read 14.49 km and 5.43 km three hours apart on identical code.[^shot-arms][^shot-batch]
- **Split the tubes (`tools/ab-shot.py`).** Odd tubes fly the shipped code and even tubes the
  change; six warheads from one bus land within about 20 m of each other, so a per-round term
  separates from one flight. It cannot see anything upstream of the release and is a screen for
  what deserves a night, not a verdict.[^ab-shot]

## Lifecycle and timing

The harness and report run outside the game. In game, each shot is an ordinary scenario run by
the scenario runner from the tail of `StepSimulation` (normally the `[StarMapAfterGui]` postfix,
`frame:draw-ui`), and the per-craft arm is applied to that craft's ballistic computer
configuration before it flies; nothing else about the frame changes between arms, which is the
point of the within-run design.[^runner][^shot-arms]

## Key code

| Path | Role |
|---|---|
| [tools/shot-batch.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/shot-batch.sh) | Builds arms, plans the order, flies, copies logs |
| [tools/shot-report.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/shot-report.py) | Endpoints, rank test, gate, verdict |
| [src/KSArmory/Sim/ShotArms.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ShotArms.cs) | Per-craft variants applied by field name |
| [tools/ab-shot.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ab-shot.py) | Odd/even tube split scored from one log |
| [docs/SHOT-PROTOCOL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/SHOT-PROTOCOL.md) | The protocol and every constant's reason |

## Snippets

```bash
# tools/shot-batch.sh L14-L17
# docs/SHOT-PROTOCOL.md is why this exists and how to read what it writes. The short version:
# run-to-run scatter on an identical pick-up is a factor of 1.7 either side of a 0.79 km median,
# so a difference of 300 m needs about twenty-five shots an arm and a single shot each way settles
# nothing at all. Everything here is in service of spending fifty shots so that they add up.
```

```csharp
// src/KSArmory/Sim/ShotArms.cs L151-L152
            FieldInfo? field = typeof(IcbmConfig).GetField(
                setting.Field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
```

## Game APIs touched

None directly. The shots run through the scenario runner and the mod's ballistic computer; the
harness drives the game only by writing request files and reading logs.

## Pitfalls and version notes

- **Between-run comparisons drift.** Identical code read 2.7x apart three hours later; prefer the
  within-run split whenever the question is about something the rockets do not share.[^shot-batch]
- **A split sees only what it splits.** The tube split is blind upstream of release; the rocket
  split is blind to anything the world shares (build, system, terrain under the target).[^shot-arms][^ab-shot]
- **Freeze the tree.** The protocol forbids touching the build during a night; the stash and the
  per-shot hash make that checkable.[^protocol]
- **The ground matters.** The protocol requires a flat target and checks whether slope under the
  target shaped the misses.[^protocol]

## See also

- [Fly scripted scenarios unattended](./unattended-scenario-runner.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md)
- [Drive a running game from outside through a file command bridge](./file-command-bridge-and-mcp.md)
- [A seeded, interleaved overnight batch with rank-sum verdicts, and within-run variant splits](../../../howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-3.md) - how-to article demonstrating this feature

[^shot-batch]: tools/shot-batch.sh
[^shot-report]: tools/shot-report.py
[^ab-shot]: tools/ab-shot.py
[^shot-arms]: src/KSArmory/Sim/ShotArms.cs
[^protocol]: docs/SHOT-PROTOCOL.md
[^runner]: src/KSArmory/Ksa/ScenarioRunner.cs
