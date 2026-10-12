---
type: Mod Feature
title: Fly scripted scenarios unattended and report a pass or fail verdict
description: A developer-only state machine reads a request file beside the mod log at load, loads a save, spawns targets, flies an engagement, bomb drop, gunnery run or ballistic shot, and writes tagged SCENARIO lines that a shell harness waits on, screenshots and turns into an exit code
mod: ksarmory
category: testing-and-tooling
techniques: [file-ipc, state-machine, log-protocol, end-to-end-test]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - reflection KSA.Popup.Popups
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.GameSaves, KSA.Popup, KSA.Program, KSA.Universe]
tags: [scenarios, automation, e2e-testing, harness, saves, verdict]
---

KSA cannot run headless (Windows natives, a simulation threaded through the Vulkan renderer), but
it can run unattended. KSArmory's developer installs read a request file at load and fly one
scripted scenario end to end: a missile or cannon engagement against spawned drones, a store
dropped off a climbing craft and scored against the sight, a gun scored shell by shell, or a full
ballistic shot from pad to impact. Every line the runner writes to the mod log starts with
`SCENARIO`, so `tools/scenario.sh` (build, deploy, launch, wait) only has to grep one tag, take a
screenshot when the mod says `CAPTURE`, and exit non-zero on `FAIL` or `TIMEOUT`.[^runner][^scenario-sh]

## How it works

| Step | Mechanism |
|---|---|
| Request | `scenario.sh` writes `scenario.txt` into the mod's log folder. A file rather than an environment variable because the game is a Windows process launched from WSL, where the environment does not survive. Line 1 is the request (`head-on`, `drop:6000,30,guided`, `gunnery:6,passing,...`, `mirv:26.485S,68.148W,2`), line 2 an A/B arm spec, line 3 its phase, line 4 space-separated options (`verbose`, `trace`, `clouds`, `noshader`, `chase`, `holdkeys`, ...). Lines are trimmed individually because CRLF can arrive from WSL[^runner] |
| Take once | `ScenarioRunner.Requested()` reads and deletes the file, so a relaunch does not repeat it[^runner] |
| Override and restore | `Begin` snapshots `Config` and the log threshold; `Finish` restores both, so a game kept open afterwards is a normal session again[^runner] |
| State machine | `Idle -> LoadingSave -> WaitingForWorld -> Arming -> Engaging/Flying/Dropping -> Settling -> Done`; long shapes delegate to `BallisticScenario`, `DropScenario` and `GunneryScenario`[^runner][^ballistic][^drop][^gunnery] |
| World control | Loads a named save with `GameSaves.LoadSaveGame(name)` three seconds after start; spawns targets through the panel's test-target code; sets timewarp; designates and arms as an operator would[^runner][^ksa-gamesaves] |
| Keeping the run honest | Each frame closes KSA engine popups (found through the private static `Popup.Popups` list by reflection), because an open one holds the controlled craft's throttle; `holdkeys` instead forces `ImGui.SetNextFrameWantCaptureKeyboard(true)` to reproduce that condition deliberately[^runner][^ksaworld][^ksa-popup] |
| Budgets | A wall-clock budget for engagements and a simulated-time budget for ballistic shots; exceeding either ends the run `TIMEOUT` with a description of where it was stuck, except while settling, which has its own bound[^runner] |
| Verdict | `FAIL ...`, `PASS detonated 12.3 m from the target`, or per-flight lines then the worst of the group against a threshold given in the request[^runner] |

Supporting tools build the inputs a scenario needs:
[tools/make-rocket.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/make-rocket.py)
writes a rocket carrying the MIRV bus into the design library from stage descriptions (part
positions from the parts' own connectors) and turns it into a scenario save through the bridge;
[tools/make-scaling-save.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/make-scaling-save.py)
writes a save with N copies of a rocket for solver-scaling measurements;
[tools/sky-matrix.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/sky-matrix.sh)
flies a list of nuclear bursts at night through the bridge, keeping a capture and the GPU split per
age.[^make-rocket][^make-scaling-save][^sky-matrix]

## Lifecycle and timing

- **`startup:all-mods-loaded`.** `KSArmoryMod.OnFullyLoaded` constructs the runner and, only when
  `Build.Developer`, calls `_scenario.Begin(ScenarioRunner.Requested())`.[^entry]
- **Per frame, at the tail of `StepSimulation`.** `_scenario.Update(_roster, _icbms, _lastSimStep, dtPlayer)`
  runs after the rounds, fire control, ballistic computers and effects of the frame have been
  stepped, inside `StepOnce`. That is normally the `[StarMapAfterGui]` postfix on
  `Program.OnDrawUiViewports` (`frame:draw-ui`, Program.cs L2265-L2269) and, on frames that draw no
  UI, the `[StarMapAfterOnFrame]` fallback or the `OnFrameCelestials` prefix. `StepSimulation`
  runs only while `KsaWorld.InFlightScene` (`Program.Editor` is null and `Universe.CurrentSystem`
  is not), so the runner advances only with a scene loaded.[^entry][^ksaworld][^ksa-program]
- **Two clocks.** `playerStep` drives the wall-clock budget and the three-second load beat;
  `simStep` (zero when paused) drives world-time budgets and ages. Commands that move the world
  (load a save, change speed, stage) are therefore issued from a mod hook, after the frame's
  physics step was dispatched; they take effect from the next `PrepareFrame`.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L376-L460) | Request file, options, config snapshot |
| [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L823-L900) | Per-frame update, budgets, save load |
| [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L1116-L1129) | `Finish` and the `SCENARIO` log tag |
| [src/KSArmory/Ksa/BallisticScenario.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BallisticScenario.cs) | Pad-to-impact ballistic shot |
| [src/KSArmory/Ksa/DropScenario.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DropScenario.cs) | Store release scored against the sight |
| [src/KSArmory/Ksa/GunneryScenario.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GunneryScenario.cs) | Every shell scored where it burst |
| [tools/scenario.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/scenario.sh) | Harness: request, launch, wait, screenshot, exit code |

## Snippets

```csharp
// src/KSArmory/Ksa/ScenarioRunner.cs L376-L392
    public static string? Requested()
    {
        try
        {
            string path = Path.Combine(Log.Folder, "scenario.txt");
            if (!File.Exists(path)) return null;

            string text = File.ReadAllText(path).TrimEnd();
            File.Delete(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
```

```csharp
// src/KSArmory/Ksa/ScenarioRunner.cs L823-L842
    public void Update(WeaponSystems roster, IcbmComputers? icbms, double simStep, double playerStep)
    {
        if (_phase is Phase.Idle or Phase.Done) return;
        if (!double.IsFinite(playerStep) || playerStep <= 0.0) return;

        // The UI holding the keyboard is what an open popup does to the rocket being flown, without the popup.
        if (_holdKeys) KsaWorld.HoldTheKeyboard();

        // Nobody is there to click a popup away, and an open one holds the controlled rocket's throttle.
        IReadOnlyList<string> closed = KsaWorld.CloseEnginePopups();
        if (closed.Count > 0)
        {
            Report($"{_name}: closed KSA's {string.Join(", ", closed)}, which holds the throttle of the craft "
                   + "being flown while it is open");
        }

        double dt = double.IsFinite(simStep) && simStep > 0.0 ? simStep : 0.0;
        _elapsed += playerStep;
        _simElapsed += dt;
```

## Game APIs touched

| Member | Use |
|---|---|
| `GameSaves.LoadSaveGame(string)` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L283)) | Load the scenario's save by name |
| `Popup.Popups` (private static list, [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L32)) | Reflected to close popups that would hold the throttle |
| `Program.Editor`, `Universe.CurrentSystem` | `InFlightScene`, the gate on stepping |

## Pitfalls and version notes

- **The mod cannot put a rocket on the pad.** `LoadVehicleFromLibrary` resolves under the game
  install, so a ballistic scenario needs the craft supplied (a save, or `settings.toml`'s
  `startVehicle`); `make-rocket.py` builds one.[^scenario-sh]
- **Popups steal the throttle.** An open engine popup, such as KSA's update notice, holds the
  controlled craft's throttle while the UI owns the keyboard; the runner closes them every frame.[^runner]
- **Quote multi-target requests.** `;` separates targets in a `mirv:` request and is also the
  shell's separator.[^scenario-sh]
- **Appearance is not judged.** The runner scores numbers; pictures arrive through `CAPTURE`
  markers for a person or model to look at.[^runner]
- A save loaded mid-session is detected by the mod's own reload hook, which forgets the previous
  world before the scenario continues.

## See also

- [Drive a running game from outside through a file command bridge](./file-command-bridge-and-mcp.md)
- [Shot-night statistics](./shot-night-statistics.md)
- [Spawn and teleport test craft](./spawn-and-teleport-test-craft.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [Guided bomb tail kit and sight](./guided-bomb-tail-kit-and-sight.md)
- [Detect a save load](./detect-save-load-ongameloaded.md)
- [Load a save a few seconds after start from an unattended scenario runner](../../../howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-3.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature
- [A request file, a state machine, wall-clock and sim-time budgets and tagged SCENARIO lines](../../../howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-1.md) - how-to article demonstrating this feature
- [A seeded, interleaved overnight batch with rank-sum verdicts, and within-run variant splits](../../../howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-3.md) - how-to article demonstrating this feature

[^runner]: src/KSArmory/Ksa/ScenarioRunner.cs
[^ballistic]: src/KSArmory/Ksa/BallisticScenario.cs
[^drop]: src/KSArmory/Ksa/DropScenario.cs
[^gunnery]: src/KSArmory/Ksa/GunneryScenario.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^scenario-sh]: tools/scenario.sh
[^make-rocket]: tools/make-rocket.py
[^make-scaling-save]: tools/make-scaling-save.py
[^sky-matrix]: tools/sky-matrix.sh
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-popup]: KSA.Popup (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
