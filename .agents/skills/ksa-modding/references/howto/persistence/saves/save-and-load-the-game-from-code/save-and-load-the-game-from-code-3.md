---
type: How-To
title: Load a save a few seconds after start from an unattended scenario runner
description: Read a one-shot request file at load, wait three seconds of player time, call GameSaves.LoadSaveGame, then run the scripted scenario once a craft is flown
category: persistence
group: saves
topic: save-and-load-the-game-from-code
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/unattended-scenario-runner]
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:load]
game_types: [KSA.GameSaves, KSA.Program, KSA.Universe]
tags: [saves, load, scenario, harness, developer-tools, startup]
---

Outcome: the game starts, loads a named save by itself and then runs a scripted test, with no click. KSArmory uses this for an unattended harness: a developer script writes a one-shot request file, launches the game, and watches the mod log for a verdict. The technique is narrow (a developer install only), but the load part is reusable wherever a mod must get from the main menu into a flight scene without the player. Demonstrated by [Fly scripted scenarios unattended and report a pass or fail verdict](../../../../mods/ksarmory/features/unattended-scenario-runner.md).

## Background: how the game does it

At startup the game sits at a menu or configuration dialog; nothing is in flight. KSArmory's comment records the reason it loads a save at all: "settings.toml's startVehicle is only ever read *by* that dialog, so without one the game sits at a menu and nothing is ever in flight. Loading a save is the only way in that does not need a click, and `GameSaves.LoadSaveGame` is public" ([src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L625-L630)). `LoadSaveGame` is described in [Create and load native KSA saves from mod code](overview.md); the relevant facts are that it is synchronous, silent on failure, and that `UncompressedSave.Load` ends with `Program.OnGameLoaded()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2606)).

## Lifecycle and timing

| Moment | Code | Lifecycle |
|---|---|---|
| Read the request | `KSArmoryMod.OnFullyLoaded` calls `ScenarioRunner.Begin(ScenarioRunner.Requested())` when `Build.Developer` | `startup:all-mods-loaded` |
| Load the save | `ScenarioRunner.Update`, phase `LoadingSave`, once 3.0 s of player time have accumulated | `frame:draw-ui` (the step runs in the `[StarMapAfterGui]` path, falling back to the frame postfix) |
| Wait for the world | phase `WaitingForWorld`: `KsaWorld.InFlight` and a crewed system | `event:load` plus later frames |

`StarMapAllModsLoaded` is early enough to read a file but too early for a save: the default system is not built until the constructor goes on to `LoadDefaultSystem` (see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). That is why the load is deferred by a timer measured in `playerStep` seconds, ticked from `Update` once frames are running, instead of being issued in the startup hook.

## Steps

1. **Take the request once.** The harness writes `scenario.txt` into the mod's log folder; the mod reads and deletes it, so a relaunch does not repeat the run. A file is used instead of an environment variable because the game is a Windows process launched from WSL:

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

2. **Parse "name|save".** The text after the `|` is the save to load; everything before it names the scenario:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L630-L631
   string[] parts = request.Split('|', 2);
   _save = parts.Length > 1 ? parts[1].Trim() : string.Empty;
   ```

3. **Start the state machine in `LoadingSave`.** `Begin` ends each scenario branch with `_phase = Phase.LoadingSave` (for example [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L665)), and `Update` adds `playerStep` to `_elapsed` every frame ([src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L841)).

4. **Wait three seconds, then load.** The comment is the design note: the first scene is still being built, and the runner does not try to swap scenes while it is. Then it chooses the next phase:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L870-L893
   case Phase.LoadingSave:
       // A beat after load: asking the game to swap scenes while it is still building
       // the first one is not a case anything here can recover from.
       if (_elapsed < 3.0) return;

       if (_save.Length > 0)
       {
           _drop?.NoteTheWorldBeforeTheLoad();

           try
           {
               GameSaves.LoadSaveGame(_save);
               Report($"{_name}: asked for save '{_save}'");
           }
           catch (Exception e)
           {
               Finish($"FAIL could not load '{_save}': {e.Message}");
               return;
           }
       }

       _phase = _isBallistic ? Phase.Flying
                : _drop is not null ? Phase.Dropping
                : Phase.WaitingForWorld;
   ```

   A failed call is reported as `FAIL could not load '<save>'` and ends the run; as with the command bridge, an unknown save name does not throw and is only visible in the game log.

5. **Wait for the world, with a diagnostic.** `Phase.WaitingForWorld` does nothing until `KsaWorld.InFlight` is true and a weapon system is crewed, and logs which of the two is missing every ten seconds:

   ```csharp
   // src/KSArmory/Ksa/ScenarioRunner.cs L910-L930
   case Phase.WaitingForWorld:
       if (!KsaWorld.InFlight || entry is null)
       {
           // Every few seconds, because "TIMEOUT in WaitingForWorld" says which state it
           // died in and nothing about which of the two things it was missing.
           if (_elapsed - _lastComplaint > 10.0)
           {
               _lastComplaint = _elapsed;
               Report($"{_name}: waiting -- "
                      + (KsaWorld.InFlight ? "in flight" : "NO CRAFT IN FLIGHT")
                      + ", " + (entry is null ? "NO SYSTEM CREWED" : "system crewed"));
           }
           return;
       }

       if (_elapsed < SettleSeconds) return;

       Report($"{_name}: crewed {KsaWorld.DisplayName(entry.Weapon.Platform!)} "
              + $"with {entry.Weapon.Profile.DisplayName}");
       _phase = Phase.Arming;
       return;
   ```

   `KsaWorld.InFlight` is `Program.ControlledVehicle is { IsDisposed: false }`:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L33
   public static bool InFlight => Program.ControlledVehicle is { IsDisposed: false };
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L376-L460) | Request file and option parsing |
| [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L823-L933) | Per-frame update, the three-second load beat, waiting for a craft |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L217-L225) | Construction and `Begin(Requested())` behind `Build.Developer` |

## Variations and gotchas

- **A fixed delay is a guess.** Three seconds worked on the author's machine; a slower load or a very large save is not detected. Prefer the readiness check of [Load a save from a command file and wait until a craft is flown again](save-and-load-the-game-from-code-2.md) (poll until a live craft is flown) when you can, and treat the delay only as a floor.
- **Developer only.** The request file is read only when `Build.Developer` is true, so a player install never loads a save by itself.
- **Keep your own state out of the way.** The runner calls `_drop?.NoteTheWorldBeforeTheLoad()` first: a scenario that holds references to the old world must record what to forget before the load disposes it (see [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)).
- **Deferred loads.** With unscience's native save hooks installed, `LoadSaveGame` only queues the load; the "asked for save" log line is then true but the world changes a frame phase later ([Hook native save and load with five patches, eight callbacks and a deferred world change](../save-mod-state-with-the-scene/save-mod-state-with-the-scene-3.md)). A wait that only tests for a flown craft (step 5) is satisfied by the old craft until the deferred load replays, so compare with the vehicle that was flown before the call.
- **A save is an input, not a result.** Nothing in this article writes a save; for the write side see [Write timed native saves with SaveName.TryAccept and UncompressedSave.Make](save-and-load-the-game-from-code-1.md).

## See also

- [Write timed native saves with SaveName.TryAccept and UncompressedSave.Make](save-and-load-the-game-from-code-1.md), [Load a save from a command file and wait until a craft is flown again](save-and-load-the-game-from-code-2.md), [overview](./overview.md)
- [Fly scripted scenarios unattended and report a pass or fail verdict](../../../../mods/ksarmory/features/unattended-scenario-runner.md)
- [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)
