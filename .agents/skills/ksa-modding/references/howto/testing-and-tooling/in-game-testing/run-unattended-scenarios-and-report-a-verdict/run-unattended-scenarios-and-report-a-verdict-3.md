---
type: How-To
title: A seeded, interleaved overnight batch with rank-sum verdicts, and within-run variant splits
description: Fly many unattended runs of competing mod variants in randomised order with every log kept, then decide with an exact rank test and a Hodges-Lehmann interval, or split variants inside one run
category: testing-and-tooling
group: in-game-testing
topic: run-unattended-scenarios-and-report-a-verdict
approach: 3
difficulty: advanced
prerequisites: [starmap, python, bash]
demonstrated_by: [mods/ksarmory/features/shot-night-statistics, mods/ksarmory/features/unattended-scenario-runner]
techniques: [statistics, reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Universe]
tags: [testing, ab-test, statistics, wilcoxon, interleaving, batch, unattended]
---

Outcome: you can tell whether a code change to a noisy in-game quantity made a difference. The unit of work is one unattended run ([article 1](run-unattended-scenarios-and-report-a-verdict-1.md)); this article adds the experiment design around it:
build every variant ("arm") once up front, interleave them in seeded random order, copy each run's log out, and let a Python report compare arms with an exact permutation rank test and a distribution-free effect interval. Where the question allows, split variants inside one run (per craft, or per tube of one salvo) to cancel the between-run noise.
Demonstrated by KSArmory ([shot-night statistics](../../../../mods/ksarmory/features/shot-night-statistics.md)). The quantity under test in KSArmory is the miss distance of a ballistic round (a gameplay mod's accuracy), on a log scale; replace it with any per-run number you can log.
Prerequisites: a scenario runner, Bash, `flock`, Python 3 and `sha256sum`.

## Background: how the game does it

Nothing in the engine is involved except what makes runs noisy: frame pacing, timewarp history, solver load and weather are not repeatable between launches, so the same code flown three hours apart read 14.49 km and 5.43 km in KSArmory's tests ([src/KSArmory/Sim/ShotArms.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ShotArms.cs#L11-L21)).
The consequences for a design: compare variants that were flown in the same session or on interleaved launches, never across nights; and keep the build identical to the byte for a given arm, which a hash proves.

## Lifecycle and timing

Harness and report run outside the game. In the game each run is an ordinary scenario from `ScenarioRunner.Update`, called at the tail of the mod's per-frame step (normally `StarMapAfterGui`, `frame:draw-ui`); the per-craft arm is applied to that craft's computer config just before it is armed ([src/KSArmory/Ksa/BallisticScenario.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BallisticScenario.cs#L540-L556)). Nothing else about the frame differs between arms, which is the point of the within-run design. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **State the design in the script's header** (what it protects against), as `shot-batch.sh` does: run-to-run scatter is a factor of 1.7 either side of a 0.79 km median, so a 300 m difference needs about twenty-five runs an arm and one run each way settles nothing:

   ```bash
   # tools/shot-batch.sh L14-L45
   # docs/SHOT-PROTOCOL.md is why this exists and how to read what it writes. The short version:
   # run-to-run scatter on an identical pick-up is a factor of 1.7 either side of a 0.79 km median,
   # so a difference of 300 m needs about twenty-five shots an arm and a single shot each way settles
   # nothing at all. Everything here is in service of spending fifty shots so that they add up.
   #
   # --paired is the OTHER kind of night, and it is the one to prefer. Instead of a build per arm and
   # an arm per shot, it builds once and gives every rocket in the world its own variant -- so the two
   # arms share a frame trace, a warp history, a solver load and a target. The between-run instrument
   # stopped being able to see anything smaller than 3x when the same baseline read 14.49 km and
   # 5.43 km on identical code three hours apart; the within-run one has no such term. Sim/ShotArms.cs
   # has the whole argument. It needs a save carrying several rockets, and what it cannot compare is
   # anything the rockets share -- the build, the system, the ground under the target.
   #
   # Four things it does that a bare `for` loop around scenario.sh does not:
   #
   #   * Every arm is BUILT ONCE, UP FRONT, and stashed. Nothing during the night reads the working
   #     tree, so an edit at 3 a.m. cannot reach a shot in flight. The build is byte-reproducible, so
   #     the deployed DLL's SHA-256 is proof of which arm actually flew -- recorded per shot, and
   #     checked against the arm's stash before the game is launched.
   #   * ARMS ARE INTERLEAVED, in a seeded random order within each block. Anything that changes with
   #     time or machine state -- thermal throttling, a service waking, a long session fragmenting --
   #     becomes noise shared by every arm instead of a difference between the ones flown early and
   #     the ones flown late. It costs one file copy a shot against eight minutes of flight.
   #   * THE LOG IS COPIED OUT between runs. scenario.sh truncates KSArmory.log at every launch, so
   #     the release probes, the warhead traces and the frame pacing of shot N are gone the instant
   #     shot N+1 starts.
   #   * ONE BATCH AT A TIME, enforced with a lock. Two batches sharing one game install and one mods
   #     folder produce shots that belong to neither.
   #
   set -euo pipefail

   REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
   ```

2. **One batch at a time.** A lock held for the life of the script, because two batches sharing an install produce runs that belong to neither:

   ```bash
   # tools/shot-batch.sh L88-L95
   LOCKFILE="$HOME/.ksarmory-shot-batch.lock"
   exec 9>"$LOCKFILE"
   if ! flock -n 9; then
       echo "error: another shot batch holds $LOCKFILE." >&2
       echo "       Wait for it, or kill it -- two batches produce shots belonging to neither." >&2
       exit 1
   fi
   echo "$$ $(date -Is) $OUT" >&9
   ```

3. **Build each arm once, up front, and record its DLL hash.** The build is byte-reproducible; the hash is the proof of which arm flew (`arms.tsv` columns `arm`, `ref`, `sha`, `dll_sha256`):

   ```bash
   # tools/shot-batch.sh L241-L247
           rm -rf "${OUT:?}/arms/$name"
           cp -a "$REPO_ROOT/src/KSArmory/bin/Release/net10.0" "$OUT/arms/$name"

           printf '%s\t%s\t%s\t%s\n' \
               "$name" "$ref" "$pin" \
               "$(sha256sum "$OUT/arms/$name/KSArmory.dll" | cut -d' ' -f1)" >> "$ARMS_TSV"
       done
   ```

4. **Interleave in seeded random order inside each block**, not a fixed rotation, so the arm is not confounded with its position in the block (a game launched after eight idle minutes is not in the state the fourth is):

   ```bash
   # tools/shot-batch.sh L272-L288
       # Randomised within each block rather than a fixed rotation. A fixed order confounds the arm
       # with its position in the block, and position is not nothing: the first game launched after
       # the machine has been idle for eight minutes is not in the state the fourth is.
       mapfile -t NAMES < <(tail -n +2 "$ARMS_TSV" | cut -f1)
       : > "$PLAN"
       python3 - "$SEED" "$BLOCKS" "${NAMES[@]}" >> "$PLAN" <<'PY'
   import random, sys
   seed, blocks, *names = sys.argv[1:]
   rng = random.Random(int(seed))
   n = 0
   for b in range(1, int(blocks) + 1):
       order = names[:]
       rng.shuffle(order)
       for arm in order:
           n += 1
           print(f"{n:03d}\t{b}\t{arm}")
   PY
   ```

5. **Before every run, copy the arm's build into the mods folder and verify the hash.** Refuse to fly if something else wrote there; mark the install as a developer install so the runner starts ([article 1](run-unattended-scenarios-and-report-a-verdict-1.md)):

   ```bash
   # tools/shot-batch.sh L397-L406
       printf '\n=== shot %s  block %s  arm %s  (%s)\n' "$n" "$block" "$arm" "$(date +%H:%M:%S)"

       cp -a "$OUT/arms/$arm/." "$MODS/"
       : > "$MODS/developer"   # the scenario runner starts only on a developer's install
       got="$(sha256sum "$MODS/KSArmory.dll" | cut -d' ' -f1)"
       if [[ "$got" != "$want" ]]; then
           echo "error: the deployed DLL is not arm '$arm' ($got, wanted $want)." >&2
           echo "       Something else is writing to $MODS. Stopping rather than flying it." >&2
           exit 1
       fi
   ```

6. **Copy each run's log out** before the next launch (the scenario harness truncates the mod log), and keep `--resume <dir>` and `--plan-only` for interrupted nights and dry runs.

7. **Decide with an exact test.** The report compares each arm with the baseline flown the same night using a Wilcoxon rank-sum with an exact permutation null (the recurrence is exact where a normal approximation is not, at the n of 12 to 30 a night produces), and reports the Hodges-Lehmann median pairwise log-ratio with a distribution-free interval, so "unresolved" is a result with a width:

   ```python
   # tools/shot-report.py L793-L847
   def mannwhitney_null(m, n, _memo={}):
       """Exact null distribution of U for sizes m and n, as counts indexed by U.

       count(m, n, u) = count(m-1, n, u-n) + count(m, n-1, u) -- the standard recurrence, which is
       exact where a normal approximation is not. At the sizes a night produces (n <= 30) the whole
       table costs milliseconds, and an approximate p-value at n=12 is the sort of thing that turns
       an unresolved arm into a reported win.
       """
       key = (m, n)
       if key in _memo:
           return _memo[key]
       if m == 0 or n == 0:
           _memo[key] = [1]
           return _memo[key]

       left = mannwhitney_null(m - 1, n)
       right = mannwhitney_null(m, n - 1)
       counts = [0] * (m * n + 1)
       for u, c in enumerate(left):
           counts[u + n] += c
       for u, c in enumerate(right):
           counts[u] += c
       _memo[key] = counts
       return counts


   def mannwhitney_p(a, b):
       """Two-sided exact p for 'b is drawn from the same distribution as a'."""
       m, n = len(a), len(b)
       if m == 0 or n == 0:
           return 1.0
       u = sum(1 for x in a for y in b if y < x) + 0.5 * sum(1 for x in a for y in b if y == x)
       counts = mannwhitney_null(m, n)
       total = sum(counts)
       centre = m * n / 2.0
       tail = sum(c for k, c in enumerate(counts) if abs(k - centre) >= abs(u - centre) - 1e-9)
       return min(1.0, tail / total)


   def hodges_lehmann(a, b):
       """Median pairwise (b - a), with a distribution-free interval at ALPHA."""
       diffs = sorted(y - x for x in a for y in b)
       if not diffs:
           return 0.0, 0.0, 0.0
       point = statistics.median(diffs)
       counts = mannwhitney_null(len(a), len(b))
       total = sum(counts)
       cum, k = 0, 0
       for u, c in enumerate(counts):
           if (cum + c) / total > ALPHA / 2:
               k = u
               break
           cum += c
       k = min(k, (len(diffs) - 1) // 2)
       return point, diffs[k], diffs[len(diffs) - 1 - k]
   ```

   The endpoint is the group's mean miss on a log scale, chosen because the distribution has a heavy right tail ([tools/shot-report.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/shot-report.py#L10-L22)).

8. **Prefer a within-run split when the question allows.** `--paired 'base|trim:TrimCeilingFromBudget=true'` builds once and gives each rocket in one save its own variant, alternating by roster index plus a per-shot phase so each arm draws each position across the night; the settings are written by field name through reflection, and an unresolvable name is a `FAIL`, because flying the wrong arm would report a dead heat:

   ```csharp
   // src/KSArmory/Sim/ShotArms.cs L145-L172
       public static bool TryApply(in Arm arm, IcbmConfig config, out string fault)
       {
           fault = "";

           foreach (Setting setting in arm.Settings)
           {
               FieldInfo? field = typeof(IcbmConfig).GetField(
                   setting.Field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

               if (field is null)
               {
                   fault = $"arm '{arm.Name}' sets '{setting.Field}', which is not a setting on IcbmConfig";
                   return false;
               }

               if (!TryConvert(setting.Value, field.FieldType, out object? value))
               {
                   fault = $"arm '{arm.Name}' sets {field.Name} to '{setting.Value}', "
                           + $"which is not a {field.FieldType.Name}";
                   return false;
               }

               field.SetValue(config, value);
           }

           return true;
       }

   ```

   It is applied last, so an arm has the final word over the harness's own forced settings ([src/KSArmory/Ksa/BallisticScenario.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BallisticScenario.cs#L544-L556)).

9. **Or split inside one salvo** (`tools/ab-shot.py`): odd tubes fly the shipped code and even tubes the change, scored from one log. It is a screen that tells you what deserves a night, not a verdict, and it cannot see anything upstream of the release ([tools/ab-shot.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ab-shot.py#L1-L22)).

10. **Read the morning report.** Run `./tools/shot-report.py <dir>`; `--instrument` after the first run checks the trace is recording; `--gate` mid-batch names arms to drop.

## Complete example

| File | Role |
|---|---|
| [tools/shot-batch.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/shot-batch.sh) | Builds arms, plans order, flies, copies logs, lock, resume |
| [tools/shot-report.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/shot-report.py) | Endpoints, exact rank test, Hodges-Lehmann, gate |
| [src/KSArmory/Sim/ShotArms.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ShotArms.cs) | Per-craft variants applied by field name |
| [tools/ab-shot.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ab-shot.py) | Odd/even tube split scored from one log |
| [docs/SHOT-PROTOCOL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/SHOT-PROTOCOL.md) | The protocol and the reasons behind every constant |

## Variations and gotchas

- **Between-run comparisons drift.** Identical code read 2.7x apart three hours later; prefer within-run splits.
- **A split sees only what it splits.** A per-craft split is blind to anything the world shares (build, system, terrain); a per-tube split is blind upstream of the release.
- **Position is not nothing.** A rocket's place in the roster was worth a large factor in miss, so arms must alternate by position and rotate between runs, never be blocked.
- **Freeze the tree.** Building up front and hashing is what makes an edit at 3 a.m. unable to reach a run in flight.
- **Do not pool seats within an arm** and divide seat level out before forming the ratio, as the protocol's "A seat is a hillside" section explains; on a four-arm night read the un-levelled line.
- **Cost.** A launch per run is minutes; this design spends fifty launches so that they add up. Use it for a question that is worth that.

## See also

- [Overview](overview.md), [article 1](run-unattended-scenarios-and-report-a-verdict-1.md)
- [Measure what a mod costs per frame](../measure-and-budget-mod-performance/overview.md)
- [KSArmory shot-night statistics](../../../../mods/ksarmory/features/shot-night-statistics.md), [KSArmory unattended scenario runner](../../../../mods/ksarmory/features/unattended-scenario-runner.md)
