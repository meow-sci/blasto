---
type: How-To
title: Load a save from a command file and wait until a craft is flown again
description: Call GameSaves.LoadSaveGame from a polled command bridge and finish the command only when Program.ControlledVehicle is a live vehicle, because the load itself gives no completion signal
category: persistence
group: saves
topic: save-and-load-the-game-from-code
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/file-command-bridge
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksacars/features/file-drop-command-bridge
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, event:load]
game_types: [KSA.GameSaves, KSA.UncompressedSave, KSA.Program, KSA.Universe]
tags: [saves, load, bridge, harness, controlled-vehicle, developer-tools]
---

Outcome: an external tool (or a debug console in the mod) asks the running game to load a named save, and the mod answers only once the new world is up and a craft is under control. The same `BeginLoad` appears in four sibling mods by the same author: KSAGolf, KSAStructures, KSACars and KSArmory. Prerequisite: StarMap with a `[StarMapAfterOnFrame]` hook. Demonstrated by [File command bridge and MCP server](../../../../mods/ksagolf/features/file-command-bridge.md), [Developer file bridge and MCP server for driving a running game](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md) and [File-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md).

## Background: how the game does it

`GameSaves.LoadSaveGame(string name)` is the console `load` command:

```csharp
// current/decomp/KSA/GameSaves.cs L282-L289
	[TerminalAction("load", "load the named game", ArgParseMode.Default)]
	public static void LoadSaveGame(string name)
	{
		if (Saves.TryGet(name, out GameSave output))
		{
			output.Load();
			return;
		}
```

The name is the save's `Id`, which is its folder name under `saves/` (`UncompressedSave` builds its directory from `GameSaves.SaveFolderPath` and `Id`: [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L12-L20)). Behaviour a caller has to know:

- **Synchronous in the stock game.** `GameSave.Load` -> `UncompressedSave.Load` -> `Universe.DeserializeSave` -> `Program.OnGameLoaded` all run before `LoadSaveGame` returns. If another mod installs deferring hooks (unscience does: [Hook native save and load with five patches, eight callbacks and a deferred world change](../save-mod-state-with-the-scene/save-mod-state-with-the-scene-3.md)), the call returns first and the world changes at the next `PrepareFrame` handoff - one more reason the bridge below polls instead of trusting the return.
- **No failure result.** An unknown name logs `save '<name>' not found` and returns. An open editor makes `Load` return silently after `RefusedInEditor`; an unreadable `universe.xml` logs and returns before `DeserializeSave` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83)). Nothing throws.
- **The scene survives, vehicles do not.** `DeserializeSave` keeps the `CelestialSystem` and replaces every vehicle (`DestroyAllVehicles`, then rebuilds from the save: [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L122-L132), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L663-L720)). `Program.ControlledVehicle` is set to `null` by the viewport `Unfollow()` calls and reassigned only if the saved camera followed a vehicle ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L832-L849)).

## Lifecycle and timing

The bridge runs in `[StarMapAfterOnFrame]`, a postfix on `Program.OnFrame` (`frame:after-on-frame`), described in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). That is the one hook KSA fires every frame, including with the HUD hidden and in menus, which is why a load command works from a menu state. KSAGolf calls the bridge from the end of that hook, after its per-frame step, so a command sees this frame's world:

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L74-L96
[StarMapAfterOnFrame]
public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
{
    try
    {
        if (_disabled || _ui is null) return;

        if (KsaWorld.InFlight)
        {
            double speed = KsaWorld.SimulationSpeed;
            if (Math.Abs(speed - _lastSimSpeed) > 1e-9)
            {
                Log.Info($"simulation speed {_lastSimSpeed:F2}x -> {speed:F2}x"
                         + (KsaWorld.IsPaused ? " (paused)" : ""));
                _lastSimSpeed = speed;
            }
        }

        // A no-op on every frame the GUI pass ran. See StepOnce.
        StepOnce(dtPlayer);

        // After the step, so a command sees this frame's world and its simulated step.
        _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? _lastSimStep : 0.0);
```

The load runs on the main thread after the frame's render has been submitted. The physics workers for the next step were already dispatched in `PrepareFrame` and are still running; `DeserializeSave` joins them with `JobSystems.WaitAll` before it destroys vehicles and bubbles ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478)), which is why a load is safe from this hook even though a general vehicle mutation is not (see [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)). The new world is built synchronously, but it is first seen by the next frame's `PrepareFrame`, which is why the mods poll rather than assume it is ready.

## Steps

1. **Receive commands outside the game's UI.** The bridge polls `bridge/in/*.json` every 0.1 s of player time and takes the oldest file; a writer creates `<id>.json.part` and renames it. See the full bridge in [File command bridge and MCP server](../../../../mods/ksagolf/features/file-command-bridge.md). Only the load step is relevant here.

2. **Call the game, converting a thrown exception into a failed reply.** The name comes from the command's `save` argument:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L620-L635
   private Reply? BeginLoad(BridgeCommand command)
   {
       string save = command.String("save");
       if (save.Length == 0) return Failed("load needs a save");

       try
       {
           GameSaves.LoadSaveGame(save);
       }
       catch (Exception e)
       {
           return Failed($"could not load '{save}': {e.Message}");
       }

       _scene = null;
       double waited = 0.0;
   ```

   Here `_scene = null` drops state that referred to the old world (a camera set from a craft that is about to be disposed). Reset your own per-world state at this point or in a load detector, see [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md).

3. **Install a per-frame completion check instead of returning a reply.** The command table keeps a `_running` delegate `(dtPlayer, dtSim) -> Reply?` that `Update` calls every frame until it returns a reply; the queue waits meanwhile:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L637-L651
       _running = (dtPlayer, _) =>
       {
           waited += dtPlayer;

           // A beat past the craft appearing, for the world to settle round it.
           if (waited > 3.0 && KsaWorld.InFlight)
           {
               return Done(new() { ["craft"] = KsaWorld.DisplayName(KsaWorld.ControlledVehicle!) });
           }

           return waited > 60.0 ? Failed("no craft after 60 s") : null;
       };

       return null;
   }
   ```

   Three seconds of player time after the craft is flown "for the world to settle", and a 60-second ceiling so a failed load answers instead of hanging.

4. **Define "a craft is flown" as a live vehicle.** The helper is a one-liner. Its doc comment in the mod warns that "a craft is flown" is not the same as "the flight scene is up" (`Universe.DestroyVehicle` clears `ControlledVehicle` when the flown craft is destroyed and the scene carries on):

   ```csharp
   // src/KSAGolf/Ksa/KsaWorld.cs L32
   public static bool InFlight => Program.ControlledVehicle is { IsDisposed: false };
   ```

   In the stock game a synchronous load leaves `ControlledVehicle` either `null` or the new vehicle: `Universe.DeserializeSave` calls `Camera.Unfollow()` on every game viewport (whose default `changeControl: true` sets `Program.ControlledVehicle = null`) after `DestroyAllVehicles`, and `Camera.DeserializeSave` then assigns `astronomical as Vehicle` when the saved camera followed something. The `IsDisposed: false` test is a cheap guard for the window where a mod defers the load (unscience does) and the property still names a vehicle the engine has disposed. `Vehicle.IsDisposed` is the engine's own flag (`Universe.DestroyVehicle` returns early when it is set).

5. **Reply from the same hook.** `Answer` writes `out/<id>.json` through a `.part` file and `File.Move`, so the client never reads half a reply. KSAStructures, KSACars and KSArmory contain the same command body: [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L380), [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L424) and [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1204); they differ only in the state each one resets (KSAStructures and KSArmory clear a held camera or pose).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L620-L651) | `BeginLoad` and its completion delegate |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L32) | `InFlight` |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L74-L96) | The frame hook that drives the bridge |
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L380) | The same command in KSAStructures |
| [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L424) | The same command in KSACars |
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1204) | The same command in KSArmory |

## Variations and gotchas

- **False success.** As written, the wait only tests that some craft is flown after three seconds. If `LoadSaveGame` was refused (unknown name, editor open, unreadable file), the old craft is still flown and the command reports success for the world that was never replaced. Check the name against `saves/` first, or compare `Program.ControlledVehicle` with the vehicle you had before the call and require a different, non-disposed instance.
- **A save with no controlled craft** (camera following a body) never satisfies `InFlight`; the command fails after 60 seconds although the load worked.
- **Do not call from a patch on the load path itself.** Calling `LoadSaveGame` from inside `Universe.DeserializeSave` or `OnGameLoaded` re-enters the load; do it from a frame hook as shown.
- **Developer gating.** All four mods create the bridge only on a developer install (a `developer` marker file beside the DLL), because a command file that loads saves is an arbitrary-action channel.
- **Pair with the writer.** The matching write side is [Write timed native saves with SaveName.TryAccept and UncompressedSave.Make](save-and-load-the-game-from-code-1.md).

## See also

- [Write timed native saves with SaveName.TryAccept and UncompressedSave.Make](save-and-load-the-game-from-code-1.md), [Load a save a few seconds after start from an unattended scenario runner](save-and-load-the-game-from-code-3.md), [overview](./overview.md)
- [File command bridge and MCP server](../../../../mods/ksagolf/features/file-command-bridge.md), [Developer file bridge and MCP server for driving a running game](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md), [File-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md)
- [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md), [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
