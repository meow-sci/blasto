---
type: How-To
title: "Add a stock-looking gauge button: register an enum at immediate load and append the Button with an XML patch"
description: Add an AUTOSTAGE toggle to the EngineControl gauge panel by appending a Button element with a KittenExtensions XML patch, registering its marker enum in GaugeButtonFlightComputer.EnumTypes at StarMapImmediateLoad and answering ToggleEnum, IsSet and IsFlightComputerDisabled in prefixes
category: ui
group: menus
topic: work-with-stock-cockpit-gauges
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony, kittenextensions]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/autostage-gauge-button]
techniques: [xml-patch, harmony-prefix, reflection, starmap-lifecycle]
hooks:
  - starmap StarMapImmediateLoad
  - reflection KSA.GaugeButtonFlightComputer.EnumTypes
  - harmony-prefix KSA.Vehicle.ToggleEnum
  - harmony-prefix KSA.Vehicle.IsSet<Enum>
  - harmony-prefix KSA.Vehicle.IsFlightComputerDisabled<Enum>
lifecycle: [startup:immediate-load, startup:asset-load, frame:draw-ui, frame:input, frame:render]
game_types: [KSA.GaugeButtonFlightComputer, KSA.EnumTypeOption, KSA.Vehicle, KSA.ModLibrary, KSA.GaugeCanvas]
tags:
  - gauge-button
  - enum-binding
  - xml-patch
  - engine-control
  - immediate-load
  - kittenextensions
  - flight-computer
---
KSA-AdvancedFlightComputer (AFC) adds an **AUTOSTAGE** toggle to the stock EngineControl gauge panel, in the free lower-left slot under RCS. It looks, sounds and lights like the stock buttons. The recipe has two halves that must line up: **data** (a `<Button>` appended to the gauge XML by a KittenExtensions patch) and **code** (an enum type whose *name* the button binds to, registered before the XML is read, plus three Harmony prefixes that answer for that enum). Prerequisites: StarMap, Harmony, and KittenExtensions as an optional dependency for the XML patch. Demonstrated by [AUTOSTAGE gauge button](../../../../mods/ksa-advancedflightcomputer/features/autostage-gauge-button.md).

## Background: how the game does it

A stock flight-computer button is `<FlightComputer Action="FlightComputerRCSMode" Value="Enabled" Toggle="true" />` inside a `<Button>` ([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L864-L913)[^ksa-current-content-core-gauges-xml]). `Action` is the enum's **type name**. `GaugeButtonFlightComputer` resolves it in `OnDataLoad` against a private static list filled once in the static constructor (11 entries, `FlightComputerManualThrustMode` ... `KittenEvaAction`); the first entry whose `EnumTypeOption.GetName()` (= `Type.Name`) matches wins and an unknown name logs `unknown enum type '...'` ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L15-L18), [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L55-L99), [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L175-L194),[^ksa-ksa-gaugebuttonflightcomputer] [current/decomp/KSA/EnumTypeOption.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EnumTypeOption.cs)).[^ksa-ksa-enumtypeoption]

At run time the button asks the controlled vehicle three things: `IsFlightComputerDisabled(_enumValue)` and `IsSet(_enumValue, clicked)` (both `public virtual` generics on `Vehicle`, read by `IsDisabled()` and `PackData()`), and on release it queues a `FlightComputerInputData`; `InputEvents` applies it with `Vehicle.ToggleEnum(EnumValue)` when `Toggle` is true ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L135-L173), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L592-L605)[^ksa-ksa-inputevents]). `Vehicle.ToggleEnum(Enum?)` is an if-chain of known enum types ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6266)[^ksa-ksa-vehicle]); a custom enum falls through it, which is where the prefix comes in.

Load order: `Program` calls `ModLibrary.PrepareAll()` (registers each enabled mod and calls its `PrepareSystems`, where StarMap's `[StarMapImmediateLoad]` hook runs), then `PreloadAssetBundles()`, later `LoadAll()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)[^ksa-ksa-program], [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L446)[^ksa-ksa-modlibrary]). `[StarMapAllModsLoaded]` is a postfix on `LoadAll`, which is too late for this.

## Lifecycle and timing

| Step | Lifecycle |
|---|---|
| Add the enum type to `GaugeButtonFlightComputer.EnumTypes` | `startup:immediate-load`, a method with exactly one `KSA.Mod` parameter |
| Apply the XML patch to the game data document | `startup:asset-load`, KittenExtensions' prefix on `ModLibrary.PrepareAll` |
| The button binds its enum by name | `startup:asset-load`, while the gauge XML is deserialized |
| Click is applied (`Vehicle.ToggleEnum`) | `frame:input` |
| `IsFlightComputerDisabled` during hover and click handling | `frame:draw-ui` |
| `IsSet` and `IsFlightComputerDisabled` read by `PackData` | `frame:render` (gauge pass of `Program.RenderGame`, via `CanvasRenderer.Render`) |

The binding cannot be undone after the XML is read, so a mod that cannot answer for the enum should skip the injection (with a warning) rather than leave a live-looking dead button. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare a marker enum.** Only its type name matters; the value is never read. Pick a name no other mod uses, because lookup is first match by `Type.Name`:

```csharp
// AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs L3-L6
// Type marker for the AUTOSTAGE gauge button.
// GaugeButtonFlightComputer resolves it by Type.Name with the first hit, so the name is distinct from the standalone AutoStage mod's marker and the two buttons cannot bind to each other's type.
// The value is never read.
public enum AfcAutoStageToggle { Enabled }
```

2. **Register the type at immediate load.** Use `[StarMapImmediateLoad]` with one `KSA.Mod` parameter (a zero-argument method is never called). It reflects into the private list and appends an `EnumTypeOption`, idempotently:

```csharp
// AdvancedFlightComputer/Mod.cs L26-L28
    // The AUTOSTAGE gauge button resolves its enum by name while the game reads Gauges.xml, which happens before any AllModsLoaded hook.
    [StarMapImmediateLoad]
    public void OnImmediateLoad(KSA.Mod mod) => AutoStageFeature.InjectGaugeEnumAtLoad();
```

```csharp
// AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs L18-L47
    // Runs at immediate load, before the game reads Gauges.xml and binds the AUTOSTAGE button's Action="AfcAutoStageToggle" once.
    // The binding cannot be undone later, so a build whose staging keys do not resolve gets no entry and one warning instead of a live-looking button.
    internal static void InjectGaugeEnumAtLoad()
    {
        if (!GameReflection.ValidateAutoStage())
        {
            DefaultCategory.Log.Warning("[AFC] AutoStage reflection keys did not resolve; the AUTOSTAGE gauge button is not registered.");
            return;
        }
        if (!InjectGaugeEnum())
            DefaultCategory.Log.Warning("[AFC] GaugeButtonFlightComputer.EnumTypes is not a List<EnumTypeOption>; the AUTOSTAGE gauge button is not registered.");
    }

    // Idempotent, because a reload would otherwise add a second entry.
    internal static bool InjectGaugeEnum()
    {
        if (!TryGetEnumTypes(out List<EnumTypeOption> list))
            return false;
        if (!list.Any(o => o.Type == typeof(AfcAutoStageToggle)))
            list.Add(new EnumTypeOption(typeof(AfcAutoStageToggle)));
        _enumInjected = true;
        return true;
    }

    internal static void RemoveGaugeEnum()
    {
        if (_enumInjected && TryGetEnumTypes(out List<EnumTypeOption> list))
            list.RemoveAll(o => o.Type == typeof(AfcAutoStageToggle));
        _enumInjected = false;
    }
```

   The reflection handles (`AccessTools.Field(typeof(GaugeButtonFlightComputer), "EnumTypes")` plus the two generics closed over `System.Enum`, used in step 5) are validated at load:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L241-L253
    // The gauge button resolves its bound enum by Type.Name from this list, so the AUTOSTAGE button's own enum type is appended to it at immediate load.
    [UsedBy(Feature.AutoStage)]
    public static readonly FieldInfo? GaugeButtonFlightComputer_EnumTypes =
        AccessTools.Field(typeof(GaugeButtonFlightComputer), "EnumTypes");

    // Closed over System.Enum, because GaugeButtonFlightComputer.PackData calls them on its boxed value.
    [UsedBy(Feature.AutoStage)]
    public static readonly MethodInfo? Vehicle_IsSet_Enum =
        GenericVehicleMethod(nameof(Vehicle.IsSet), parameterCount: 2)?.MakeGenericMethod(typeof(Enum));

    [UsedBy(Feature.AutoStage)]
    public static readonly MethodInfo? Vehicle_IsFlightComputerDisabled_Enum =
        GenericVehicleMethod(nameof(Vehicle.IsFlightComputerDisabled), parameterCount: 1)?.MakeGenericMethod(typeof(Enum));
```

3. **Append the Button with an XML patch.** KittenExtensions' `<Copy Path="..." Pos="Append">` adds the element to the `Buttons` component of the `EngineControl` canvas. The `FlightComputer` child binds the enum by type name:

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

   The patch file is declared in `mod.toml`, with KittenExtensions as an optional StarMap dependency:

```toml
# AdvancedFlightComputer/mod.toml L1-L8
name = "AdvancedFlightComputer"
patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]

[StarMap]
EntryAssembly = "AdvancedFlightComputer"

[[StarMap.ModDependencies]]
ModId = "KittenExtensions"
```

   EngineControl was chosen over the burn canvas because the burn canvas only draws while a burn is planned (`VisibleInContext`); EngineControl draws whenever the vehicle carries an engine. See [XPath XML patch recipes](../../../assets/xml-patching/xpath-xml-patch-recipes/overview.md).

4. **Answer the click.** A prefix on `Vehicle.ToggleEnum(Enum)` handles your marker and skips the original (return `false`) only for it:

```csharp
// AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs L10-L20
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
```

5. **Answer "lit" and "disabled".** `IsSet<T>` and `IsFlightComputerDisabled<T>` are generic. The button calls them with its boxed `Enum` field, so the instantiation to patch is the one **closed over `System.Enum`**; AFC resolves it with `MakeGenericMethod(typeof(Enum))` and gives Harmony a `TargetMethod()`:

```csharp
// AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs L22-L55
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

    // KittenEva's override answers "disabled" for everything but its own actions without calling base, which is the wanted answer: an EVA kitten draws no engine panel.
    [HarmonyPatch]
    internal static class IsDisabledPatch
    {
        static MethodBase TargetMethod() => GameReflection.Vehicle_IsFlightComputerDisabled_Enum!;

        // Stays enabled while armed, so the player can always switch it off, and while a pure jettison row is pending, which is what the spent-stage drop stages.
        // The jettison rebuild runs from the draw pass while solvers are queued; that is safe because the worker path snapshots PartTree.Parts itself and never reads the lazy Sequence.Parts cache.
        static bool Prefix(Vehicle __instance, Enum value, ref bool __result)
        {
            if (value is not AfcAutoStageToggle)
                return true;
            __result = !StagingDetector.IsArmed(__instance)
                       && !StagingHelpers.HasNextEngineSequence(__instance)
                       && !(StagingConfig.DropSpentStages
                            && JettisonAnalysis.GetPendingJettison(__instance, StagingDetector.StateOf(__instance)) != null);
            return false;
        }
    }
```

6. **Apply the patches at all-mods-loaded**, since they can be applied late (they only matter once the vehicle is controlled). Run the isolation pattern from [Isolate patch failures and fault-proof patch bodies](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md). Unregister the enum on unload (`RemoveGaugeEnum`).

7. **Check in game.** Fly a craft with an engine: EngineControl shows the new button in the empty quarter of the 2x2 grid. Click it: the button lights green (`ActiveColor`), the sound plays, and `ToggleEnum` runs on the next input application. If the log says `unknown enum type '...'`, the enum was registered too late or its name does not match.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs) | Marker enum |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs#L18-L47) | Injection and removal of the enum entry |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageGaugePatches.cs#L10-L55) | Toggle, lit and disabled answers |
| [AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml) | KittenExtensions patch adding the button |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L26-L28) | `[StarMapImmediateLoad]` entry |
| [AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml) | `patches` list and optional dependency |

## Variations and gotchas

- **Name collisions.** Two mods registering enums with the same `Type.Name` bind to the first; AFC's name differs from the standalone AutoStage mod's `AutoStageToggle`, and `StandaloneModAbsent()` checks for that marker so the built-in stager stands down.
- **Disabled mods still patch.** KittenExtensions builds its patch domain without checking each mod's `Enabled` flag, so a disabled standalone mod keeps its button. Delete the folder instead.
- **No KittenExtensions, no button.** The data patch never runs; keep the feature reachable elsewhere (AFC puts the same switch on the Mods settings page; see [Inject UI into the game's own windows](../inject-ui-into-a-stock-window/overview.md)).
- **Late failure leaves a dead button.** If the patch block fails at all-mods-loaded after the button was bound, the button still draws and does nothing; AFC logs exactly that.
- **`KittenEva` overrides both generics** ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L240-L297)): its `IsSet` forwards anything that is not a `KittenEvaAction` to `base`, so your `IsSet` prefix still answers for an EVA kitten, while its `IsFlightComputerDisabled` returns `true` for every other enum without calling `base`, so your disabled prefix never runs for a kitten and the button reads disabled there, which is the wanted outcome (an EVA kitten draws no engine panel).[^ksa-ksa-kitteneva]
- **Generics and Harmony.** Patch the `Enum` instantiation the button actually calls; AFC's RCS code patches the concrete `GaugeButtonFlightComputer` instead, see [approach 2](work-with-stock-cockpit-gauges-2.md).

## See also

- [Overview](overview.md)
- [Approach 2: rewrite disabled bits and tooltip in postfixes](work-with-stock-cockpit-gauges-2.md)
- [XPath XML patch recipes](../../../assets/xml-patching/xpath-xml-patch-recipes/overview.md)
- [Choose a StarMap startup hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- [Declare a mod dependency](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md)
- Mod feature: [AUTOSTAGE gauge button](../../../../mods/ksa-advancedflightcomputer/features/autostage-gauge-button.md)

[^ksa-current-content-core-gauges-xml]: Game content: current/Content/Core/Gauges.xml
[^ksa-ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-ksa-enumtypeoption]: KSA.EnumTypeOption (decompiled, build 2026.10.10.5554)
[^ksa-ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
