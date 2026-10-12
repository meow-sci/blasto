---
type: Mod Feature
title: Staging delays per part variant and per sequence
description: Hold engines and decouplers of a staged row for configurable delays with on-screen countdowns, configured globally per part variant in TOML and per vehicle sequence from a part-window postfix
mod: ksa-advancedflightcomputer
category: vehicles-and-parts
techniques: [harmony-postfix, toml-config, imgui-window, reflection]
hooks:
  - harmony-postfix KSA.Part.DrawPartInfo
  - harmony-transpiler KSA.GameSettings.OnDrawUi
  - reflection KSA.ModLibrary.AllParts
lifecycle: [frame:apply-solvers, frame:draw-ui, event:mod-unload]
game_types:
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.Part
  - KSA.ISequenced
  - KSA.EngineController
  - KSA.Decoupler
  - KSA.TimedAlert
  - KSA.ModLibrary
tags: [staging, delays, decoupler, ignition, toml, part-window, settings]
---

Stock staging fires every module of a row at once. AFC can hold the row's engines and decouplers
for separate delays measured from the staging trigger, to imitate separation and spool-up time (the
default ignition delays per stock engine variant are 2 s for EngineA1 and 3 s for EngineA3).
Delays come from a global table per part variant and can be overridden per vehicle sequence from
the part's right-click window.

## How it works

- **Split activation.** `StagingExecution.ActivateNextSequenceSplit` activates the row like stock
  but routes each inactive `EngineController` or `Decoupler` module to a pending list when its delay
  is positive; everything else fires at once. It returns a `PendingStaging` with absolute deadlines
  in simulation time ([AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs#L13-L99)).
- **Firing.** Every `Evaluate` first calls `TickPendingStaging`, which fires the modules whose
  deadline passed (skipping modules whose part an earlier decouple carried away), then resets the
  sequence caches. Countdowns are `TimedAlert.Create("Decouple in X.Xs", ...)` and
  `"Ignition in X.Xs"` with the delay as the alert lifetime
  ([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L476-L483)).
- **Per module, not per part.** A sequence is a set of `ISequenced` modules: one part can put its
  motor and two mounts on three rows. `SequencedModules` enumerates the part and its direct sub-parts
  (the scope of `Part.ActivateSubtreeInStage`) with a ref-struct enumerator, and the row delay is the
  longest variant delay among the modules of that kind ([AdvancedFlightComputer/Features/AutoStage/SequencedModules.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/SequencedModules.cs)).
- **Config layers.** `autostage.toml` holds `[staging]` switches and `[engine_delays]` /
  `[decoupler_delays]` keyed by part template id; `autostage-vehicles/<vehicle-id>.toml` holds
  `[sequence_delays]` and `[decoupler_delays]` per sequence number, which win. Nothing goes into a
  save. A file that failed to load is never written back, and the standalone AutoStage mod's files
  are imported once on first load ([AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs#L8-L85)).
- **Part window.** A postfix on `Part.DrawPartInfo()` adds one "Ignition Delay" or "Decoupler Delay"
  block per (module kind, sequence) the part fires in, for parts of the controlled vehicle, and
  saves on `IsItemDeactivatedAfterEdit` ([AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs)).
- **Settings page.** `AutoStageSettingsPage.DrawSection` (registered with the
  [Mods settings page](mods-settings-page-sections.md)) shows the two switches and the delay tables
  over every part variant from the private `ModLibrary.AllParts` collection; the tables are skipped
  when that soft anchor is missing ([AdvancedFlightComputer/Features/AutoStage/AutoStageSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageSettingsPage.cs)).
- **Unload.** `StagingDetector.FlushPendingForUnload` runs first in `[StarMapUnload]`: a held row is
  already marked activated, so stock would never fire its modules again. Under StarMap 4dc60f8 this is
  defensive only: `StarMapUnload` is invoked from `StarMapCore.DeInit` after `Program.Main` has returned
  and after `Harmony.UnpatchAll()` has removed every patch, so the flushed activations are queued into
  an `InputEvents` buffer that no frame drains
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).

## Lifecycle and timing

| Part | Lifecycle |
|---|---|
| Split activation and pending fires | `Universe.ApplyVehicleSolvers` postfix (`frame:apply-solvers`), main thread; `module.Activate` only queues, the activation is drained and part trees rebuilt later in `PrepareFrame` |
| Part-window block | `Part.DrawPartInfo`, called from `Part.OnDrawUi` for pinned part windows and from the editor (`frame:draw-ui`) ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L3155-L3212)) |
| Settings tables | `GameSettings.OnDrawUi` (`frame:draw-ui`) |
| Flush | `[StarMapUnload]` (`event:mod-unload`), after the game loop has ended under StarMap |

Deadlines use `Universe.GetElapsedSeconds()` because the detector also runs on paused frames, where
a countdown fed by frame delta would keep draining.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs) | Split activation and `PendingStaging` |
| [AdvancedFlightComputer/Features/AutoStage/SequencedModules.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/SequencedModules.cs) | Module-level sequence membership |
| [AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs) | Two-layer TOML config, import, overrides |
| [AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs) | Part-window override UI |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageSettingsPage.cs) | Settings section with delay tables |
| [tests/AdvancedFlightComputer.HarnessTests/Features/AutoStage/StagingDelayTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoStage/StagingDelayTest.cs) | Headless test of held modules and deadlines |

## Snippets

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs L56-L80
        // The same walk stock performs through Part.ActivateSubtreeInStage.
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            foreach (ISequenced module in parts[i].InSequence(seqNumber))
            {
                if (!module.IsActive && module is EngineController && engineDelay > 0.0)
                {
                    pendingEngines ??= new List<ISequenced>();
                    pendingEngines.Add(module);
                }
                else if (!module.IsActive && module is Decoupler && decouplerDelay > 0.0)
                {
                    pendingDecouplers ??= new List<ISequenced>();
                    pendingDecouplers.Add(module);
                }
                else
                {
                    module.Activate(vehicle);
                    firedNow++;
                }
            }
        }

        GameReflection.SequenceList_updatingSequence!.SetValue(seqList, false);
        seqList.ResetCaches();
```

## Game APIs touched

| Member | Use |
|---|---|
| `SequenceList.ActivateNextSequence` ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171-L206)) | Stock behavior mirrored by the split activation |
| `Part.InSequence(int)`, `ActivateSubtreeInStage`, `GetSubtreeSequencedModules` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs)) | Module membership per row |
| `ISequenced.Activate(Vehicle)`, `IsActive` | Fire held modules |
| `Part.DrawPartInfo()` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L3291)) | Part-window postfix host |
| `TimedAlert.Create(string, Color, double)` ([current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs)) | On-screen countdowns |
| `ModLibrary.AllParts` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs)) | Part variants for the settings tables |

## Pitfalls and version notes

- Key overrides by sequence number per vehicle id, and move or drop them on rename and dispose (AFC
  removes them on dispose).
- Only module kinds AFC names get a delay; a new module kind added by the game fires immediately.
- The part window only flushes after an edit is deactivated, so pending saves are flushed on reset and
  unload.

## See also

- [Automatic staging](automatic-staging.md)
- [Spent stage drop](spent-stage-drop.md)
- [Mods settings page sections](mods-settings-page-sections.md)
- [Save-scoped sidecar registries](save-scoped-sidecar-registries.md)
- [Hold decouplers and engines back after the staging trigger with sim-time deadlines](../../../howto/vehicles-and-parts/staging-and-engines/stage-automatically/stage-automatically-3.md) - how-to article demonstrating this feature

