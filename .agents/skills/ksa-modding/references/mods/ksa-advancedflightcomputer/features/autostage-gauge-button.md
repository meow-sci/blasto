---
type: Mod Feature
title: A custom gauge button action backed by a mod enum
description: Register a mod-defined enum in GaugeButtonFlightComputer.EnumTypes at immediate load, add a Button to the stock EngineControl gauge with a KittenExtensions XML patch, and give the enum toggle, lit and disabled state through Vehicle patches
mod: ksa-advancedflightcomputer
category: ui
techniques: [starmap-lifecycle, xml-patch, harmony-prefix, reflection]
hooks:
  - starmap StarMapImmediateLoad
  - reflection KSA.GaugeButtonFlightComputer.EnumTypes
  - harmony-prefix KSA.Vehicle.ToggleEnum
  - harmony-prefix KSA.Vehicle.IsSet
  - harmony-prefix KSA.Vehicle.IsFlightComputerDisabled
lifecycle: [startup:immediate-load, startup:asset-load, frame:draw-ui, frame:render, frame:input]
game_types: [KSA.GaugeButtonFlightComputer, KSA.EnumTypeOption, KSA.Vehicle, KSA.InputEvents, KSA.ModLibrary]
tags: [gauges, cockpit, xml-patch, enum, kittenextensions, immediate-load]
---

AFC adds an **AUTOSTAGE** toggle to the stock EngineControl gauge panel, in the free lower-left slot
under RCS, that looks and sounds exactly like the stock buttons and lights green while the vehicle is
armed. Stock gauge buttons bind to a flight-computer enum by type name; AFC registers its own enum
type in that list early enough for the gauge XML to bind it, then answers for the enum in the three
`Vehicle` methods the button calls.

## How it works

1. **The marker enum.** `public enum AfcAutoStageToggle { Enabled }`. Its value is never read; only
   the type name matters. The name differs from the standalone AutoStage mod's `AutoStageToggle`
   so the two buttons cannot bind to each other's type
   ([AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs)).
2. **Register the type.** `GaugeButtonFlightComputer` keeps a private static
   `List<EnumTypeOption> EnumTypes` filled in its static constructor; `OnDataLoad` resolves the XML
   `Action` attribute by name with `FindTypeOption` and logs "unknown enum type" otherwise
   ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L15-L18), [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L55-L99)).[^ksa-gaugebuttonflightcomputer]
   AFC appends `new EnumTypeOption(typeof(AfcAutoStageToggle))` to that list by reflection,
   idempotently, and removes it on unload ([AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs#L17-L46)).
3. **Timing.** `ModLibrary.PrepareAll` calls every mod's `PrepareSystems` (StarMap's
   `[StarMapImmediateLoad]` prefix) before `ModLibrary.LoadAll` loads the assets, including
   `Core/Gauges.xml`, and `[StarMapAllModsLoaded]` is a postfix on `LoadAll`, which is too late
   ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L521)).[^ksa-modlibrary] So the injection runs in
   `Mod.OnImmediateLoad` ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L26-L28)). The binding cannot be undone
   later, so the injection is skipped (with a warning) when the AutoStage reflection keys do not
   resolve, rather than leaving a live-looking dead button.
4. **The button.** `Patches/AutoStageButton.xml` is a KittenExtensions `<Copy Path=... Pos="Append">`
   that appends a `<Button>` with `<FlightComputer Action="AfcAutoStageToggle" Value="Enabled" Toggle="true"/>`
   to `//GaugeCanvas[@Id='EngineControl']/Component[@Name='Buttons']`, at `X="0.006" Y="0.5"`, the
   empty quarter of the stock 2x2 grid (RCS at 0.006/0, IGNITE and SHUTDN on the right)
   ([AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml), [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L864-L913)).
   EngineControl was chosen over BurnControl because BurnControl only draws while a burn is planned.
5. **State.** Three prefixes answer for the marker type and pass everything else through:
   `Vehicle.ToggleEnum(Enum)` toggles `StagingDetector.Arm`; `Vehicle.IsSet<Enum>` returns armed (lit);
   `Vehicle.IsFlightComputerDisabled<Enum>` returns disabled only when unarmed and there is no next
   engine sequence or pending pure-jettison row. The generic methods are closed over `System.Enum`
   with `MakeGenericMethod(typeof(Enum))`, because `GaugeButtonFlightComputer.PackData` calls them with
   its boxed `Enum` field ([AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs),
   [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L246-L253)).
6. **Fallback.** Without KittenExtensions the XML patch never runs; the same switch is on the Mods
   settings page (see [optional KittenExtensions patches](optional-kittenextensions-patches.md)).

## Lifecycle and timing

| Step | Lifecycle |
|---|---|
| Enum injection | `startup:immediate-load` (prefix on AFC's `Mod.PrepareSystems`) |
| XML patch applied to the game data document | `startup:asset-load` (KittenExtensions, prefix on `ModLibrary.PrepareAll`) |
| Button bound by name | `startup:asset-load` (`GaugeButtonFlightComputer.OnDataLoad` during `ModLibrary.LoadAll`) |
| `IsSet` / `IsFlightComputerDisabled` from `PackData` | `frame:render`: `Program.RenderGame` -> `GaugeCanvas.CanvasesRender` -> `CanvasRenderer.Render` -> `GaugeBundle.UpdateInstanceBuffer` -> `GaugeButtonReference.PackData1` -> `PackData`, the gauge pass of the frame ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940), [current/decomp/KSA/CanvasRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CanvasRenderer.cs#L49-L59)) |
| `IsFlightComputerDisabled` from `IsDisabled` | `frame:draw-ui`: hover and click handling in `GaugeCanvas.OnDrawUi`, which asks `IsDisabled` before playing the click sound or queueing the click |
| Click to `Vehicle.ToggleEnum` | queued in `InputEvents.FlightComputerInputBuffer`, applied in `InputEvents.ApplyInputEvents` (`frame:input`) ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L592-L605)) |

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs) | Marker enum |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs) | Injection, removal, standalone detection by enum name |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs) | Toggle, lit and disabled answers |
| [AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml) | KittenExtensions patch adding the button |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) | `[StarMapImmediateLoad]` entry |

## Snippets

```xml
<!-- AdvancedFlightComputer/Patches/AutoStageButton.xml L8-L20 -->
  <Copy Path="//GaugeCanvas[@Id='EngineControl']/Component[@Name='Buttons']" Pos="Append">
    <Button X="0.006" Y="0.5" Width="0.4854" Height="0.4994"
            Label="AUTOSTAGE" TextScale="0.28">
      <TextColor Name="Black" />
      <BackColor Name="AlmostBlack" A="0" />
      <DisabledColor R="0.1" G="0.1" B="0.1" />
      <IdleColor R="0.175" G="0.18" B="0.19" />
      <ActiveColor Name="Green" />
      <SoundEvent SoundId="LedButtonDown" Action="Pressed" />
      <FlightComputer Action="AfcAutoStageToggle" Value="Enabled" Toggle="true"/>
      <Tooltip Value="Automatically stage when engines run out of propellant, and drop spent boosters while the rest of the stage keeps firing."/>
    </Button>
  </Copy>
```

```csharp
// AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs L10-L35
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.ToggleEnum), new[] { typeof(Enum) })]
    internal static class TogglePatch
    {
        static bool Prefix(Vehicle __instance, Enum? enumValue)
        {
            if (enumValue is not AfcAutoStageToggle)
                return true;
            StagingDetector.Arm(__instance, !StagingDetector.IsArmed(__instance));
            return false;
        }
    }

    // KittenEva's override forwards anything that is not a KittenEvaAction to base, so this still answers for an EVA kitten.
    [HarmonyPatch]
    internal static class IsSetPatch
    {
        static MethodBase TargetMethod() => GameReflection.Vehicle_IsSet_Enum!;

        static bool Prefix(Vehicle __instance, Enum value, ref bool __result)
        {
            if (value is not AfcAutoStageToggle)
                return true;
            __result = StagingDetector.IsArmed(__instance);
            return false;
        }
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `GaugeButtonFlightComputer.EnumTypes` (private static), `OnDataLoad`, `PackData`, `OnReleased` ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs)) | Type registry, binding, render bits, click queueing |
| `EnumTypeOption` ([current/decomp/KSA/EnumTypeOption.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EnumTypeOption.cs)) | Registry entry wrapping a `Type` |
| `Vehicle.ToggleEnum(Enum)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6266)), `IsSet<T>(T, bool)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6431)), `IsFlightComputerDisabled<T>(T)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6860)) | State hooks |
| `ModLibrary.PrepareAll` / `LoadAll` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs)) | Load order that forces immediate-load injection |

## Pitfalls and version notes

- The enum's type name is the binding key and the first match wins; pick a name no other mod uses.
- A disabled mod still applies its KittenExtensions XML patches, so a removed-but-not-deleted
  standalone AutoStage would leave its own button in the gauge.
- If AFC's AutoStage block fails at all-mods-loaded, the button was already bound and still draws but
  does nothing; AFC logs exactly that.
- `KittenEva` overrides `IsSet`/`IsFlightComputerDisabled`; AFC documents why the prefixes still behave.

## See also

- [Automatic staging](automatic-staging.md)
- [Optional KittenExtensions patches](optional-kittenextensions-patches.md)
- [RCS burn editor and gauge UI](rcs-burn-editor-and-gauge-ui.md)
- [Append a button to a stock gauge canvas with Copy and inline XML](../../../howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-1.md) - how-to article demonstrating this feature
- [Add a stock-looking gauge button: register an enum at immediate load and append the Button with an XML patch](../../../howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-1.md) - how-to article demonstrating this feature

[^ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
