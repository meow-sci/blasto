---
type: How-To
title: Re-enable a stock gauge button and rewrite its tooltip with Harmony postfixes on GaugeButtonFlightComputer
description: Postfix IsDisabled and PackData on the concrete gauge button and prefix the private Vehicle.Hovered so the stock Auto burn button works, lights and explains itself for a burn executed with RCS on an engineless vehicle
category: ui
group: menus
topic: work-with-stock-cockpit-gauges
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui]
techniques: [harmony-postfix, harmony-prefix, reflection]
hooks:
  - harmony-postfix KSA.GaugeButtonFlightComputer.IsDisabled
  - harmony-postfix KSA.GaugeButtonFlightComputer.PackData
  - harmony-prefix KSA.Vehicle.Hovered(FlightComputerBurnMode)
  - reflection KSA.GaugeButtonFlightComputer._enumValue
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types: [KSA.GaugeButtonFlightComputer, KSA.Vehicle, KSA.GaugeCanvas, KSA.ImGuiHelper]
tags: [gauge-button, packdata, isdisabled, tooltip, rcs, burn-mode, postfix]
---
The stock Auto burn button on the gauge panel is the single trigger for burns, but stock disables it on a vehicle with no engine: it requires an engine-derived burn duration above zero, and its hover tooltip says there is no engine. AFC executes burns with RCS thrusters, so it re-enables the button, lights it while an RCS burn runs and replaces the tooltip. It does this without touching the XML, by patching the **button class itself** with two postfixes and one prefix. Prerequisites: StarMap and Harmony. Demonstrated by [RCS burn editor and gauge UI](../../../../mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui.md).

## Background: how the game does it

`GaugeButtonFlightComputer` is bound to one enum value (private field `_enumValue`). Two of its public overrides decide what the player sees:

```csharp
// current/decomp/KSA/GaugeButtonFlightComputer.cs L151-L173
	public override bool IsDisabled()
	{
		return Program.ControlledVehicle == null || Program.ControlledVehicle.IsFlightComputerDisabled(_enumValue);
	}

	public override uint2 PackData()
	{
		if (Program.ControlledVehicle == null)
		{
			return new uint2(0u, 0u);
		}
		bool flag = Program.ControlledVehicle.IsSet(_enumValue, _clicked);
		bool flag2 = Program.ControlledVehicle.IsFlightComputerDisabled(_enumValue);
		bool flag3 = _clicked && !flag2;
		bool flag4 = flag;
		byte b = ((!flag2) ? ((byte)1) : ((byte)0));
		uint num = 0u;
		num |= (uint)(flag3 ? 1 : 0);
		num |= (uint)((flag4 ? 1 : 0) << 1);
		num |= (uint)((flag2 ? 1 : 0) << 2);
		num |= (uint)(b << 3);
		return new uint2(num, 0u);
	}
```

([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L135-L173)[^ksa-ksa-gaugebuttonflightcomputer]). `PackData` returns the render state as bits in a `uint2`: bit0 clicked, bit1 set (lit), bit2 disabled, bit3 enabled.

For the Auto burn mode, `Vehicle.IsFlightComputerDisabled<T>` returns `!IsControllable || FlightComputer.Burn == null || BurnDuration <= 0` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6860-L6900)),[^ksa-ksa-vehicle] and the private `Vehicle.Hovered(FlightComputerBurnMode)` draws a red `FlightComputerTooltipNoEngine` message through `ImGuiHelper.DrawTooltip` when `IsAnyEngineActive()` is false ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6832-L6858)). `OnHovered` calls `HoveredEnum`, which dispatches to the private `Hovered` overloads ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6735)).

## Lifecycle and timing

- **Patch at `startup:all-mods-loaded`**, as part of an isolated patch block (`PatchRcsTranslation`).
- **`IsDisabled` and `Hovered` run at `frame:draw-ui`**, from the gauge hover and click handling (`GaugeCanvas.OnDrawUi`), on the main thread; ImGui calls are legal there.
- **`PackData` runs later**, in the gauge pass of `Program.RenderGame` (`GaugeBundle.UpdateInstanceBuffer` through `PackData`), `frame:render` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940)[^ksa-ksa-program], [current/decomp/KSA/CanvasRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CanvasRenderer.cs#L49-L59)).[^ksa-ksa-canvasrenderer] A `PackData` postfix must not call ImGui.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Recognise the one button you care about.** The bound enum is private, so resolve a validated `FieldInfo` once and cache the boxed value per button in a `ConditionalWeakTable`; re-read it if the editor changes the button's `EnumType` or `Value` strings:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs L20-L51
    // GaugeButtonFlightComputer.DrawEditor reloads the binding when either selector changes.
    private sealed class BoundEnum
    {
        public Enum? Value;
        public string? EnumType;
        public string? ValueName;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GaugeButtonFlightComputer, BoundEnum>
        _boundEnums = new();

    private static bool IsAutoBurnButton(GaugeButtonFlightComputer button)
    {
        BoundEnum bound = _boundEnums.GetValue(button, static _ => new BoundEnum());
        if (bound.Value == null || bound.EnumType != button.EnumType || bound.ValueName != button.Value)
        {
            bound.Value = GameReflection.GaugeButtonFlightComputer_enumValue?.GetValue(button) as Enum;
            bound.EnumType = button.EnumType;
            bound.ValueName = button.Value;
        }
        return bound.Value is FlightComputerBurnMode mode && mode == FlightComputerBurnMode.Auto;
    }

    private static bool ShouldOverride(out bool active)
    {
        active = false;
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null)
            return false;
        active = RcsExecutor.IsActive(vehicle);
        return active || RcsExecutor.WouldExecuteRcsCached(vehicle);
    }
```

   The handle itself lives with the other validated reflection handles:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L297-L305
    // The gauge button keeps its bound enum private, and the RCS gauge patches need it to recognize the BurnMode button instance.
    [UsedBy(Feature.RcsTranslation)]
    public static readonly FieldInfo? GaugeButtonFlightComputer_enumValue =
        AccessTools.Field(typeof(GaugeButtonFlightComputer), "_enumValue");

    // Private tooltip hook for the Auto button, replaced with the RCS explanation when a burn resolves to RCS execution.
    [UsedBy(Feature.RcsTranslation)]
    public static readonly MethodInfo? Vehicle_Hovered_BurnMode =
        AccessTools.Method(typeof(Vehicle), "Hovered", new[] { typeof(FlightComputerBurnMode) });
```

2. **Postfix `IsDisabled`.** If stock says "disabled" and this is the Auto button on a vehicle that would execute (or is executing) the burn with RCS, return `false`:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs L53-L64
    [HarmonyPatch(typeof(GaugeButtonFlightComputer), nameof(GaugeButtonFlightComputer.IsDisabled),
        new Type[0])]
    internal static class IsDisabledPatch
    {
        static void Postfix(GaugeButtonFlightComputer __instance, ref bool __result)
        {
            if (!__result || !IsAutoBurnButton(__instance))
                return;
            if (ShouldOverride(out _))
                __result = false;
        }
    }
```

3. **Postfix `PackData`.** The rendered state comes from the bits, not from `IsDisabled`, so rewrite them too: clear bit2 (disabled), set bit3 (enabled), and while running set bit1 (lit) because internally the burn mode stays Manual during an RCS burn:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs L66-L88
    /// <summary>PackData drives the rendered button state: bit0 clicked,
    /// bit1 set/lit, bit2 disabled, bit3 enabled. While an RCS execution is
    /// armed or running, stock computes "disabled + unlit" (BurnMode stays
    /// Manual internally); rewrite the bits so the button reads enabled and,
    /// while running, lit.</summary>
    [HarmonyPatch(typeof(GaugeButtonFlightComputer), nameof(GaugeButtonFlightComputer.PackData),
        new Type[0])]
    internal static class PackDataPatch
    {
        static void Postfix(GaugeButtonFlightComputer __instance, ref uint2 __result)
        {
            if (!IsAutoBurnButton(__instance))
                return;
            if (!ShouldOverride(out bool active))
                return;
            uint bits = __result.X;
            bits &= ~0b0100u;
            bits |= 0b1000u;
            if (active)
                bits |= 0b0010u;
            __result = new uint2(bits, __result.Y);
        }
    }
```

4. **Replace the tooltip.** `Vehicle.Hovered(FlightComputerBurnMode)` is private, so give Harmony a `TargetMethod()` resolved from a validated handle (exact parameter types), and use a prefix that returns `false` only when you drew your own tooltip:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs L90-L130
    /// <summary>Replaces the red "no engine" hover warning with the RCS
    /// explanation whenever the button acts as the RCS trigger.</summary>
    [HarmonyPatch]
    internal static class HoveredPatch
    {
        static System.Reflection.MethodBase TargetMethod()
            => GameReflection.Vehicle_Hovered_BurnMode!;

        static bool Prefix(Vehicle __instance, FlightComputerBurnMode requestedMode)
        {
            if (requestedMode != FlightComputerBurnMode.Auto)
                return true;
            if (Program.ControlledVehicle != __instance)
                return true;
            if (RcsExecutor.IsActive(__instance))
            {
                DrawTooltip("RCS burn running - click to cancel");
                return false;
            }
            if (RcsExecutor.WouldExecuteRcsCached(__instance))
            {
                DrawTooltip("Execute burn with RCS");
                return false;
            }
            return true;
        }

        private static void DrawTooltip(string text)
        {
            try
            {
                ImGuiHelper.DrawTooltip(text, ImColor8.White);
            }
            catch (Exception ex)
            {
                // Cosmetic, but a silent swallow would hide a signature drift after the prefix already suppressed stock's tooltip.
                LogHelper.WarnOnce("rcs-gauge-tooltip",
                    $"[AFC] RCS gauge tooltip failed to draw: {ex.Message}");
            }
        }
    }
```

   Resolve it by exact parameter type, because `Hovered` is overloaded for several enums:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L302-L305
    // Private tooltip hook for the Auto button, replaced with the RCS explanation when a burn resolves to RCS execution.
    [UsedBy(Feature.RcsTranslation)]
    public static readonly MethodInfo? Vehicle_Hovered_BurnMode =
        AccessTools.Method(typeof(Vehicle), "Hovered", new[] { typeof(FlightComputerBurnMode) });
```

5. **Apply the three patches together**, in the same isolated block, so a partial application never leaves a half-working button:

```csharp
// AdvancedFlightComputer/Mod.cs L185-L193
    private static void PatchRcsTranslation(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(RcsSetEnumPatch)).Patch();
        harmony.CreateClassProcessor(typeof(RcsWarpPatch)).Patch();
        harmony.CreateClassProcessor(typeof(RcsGaugePatches.IsDisabledPatch)).Patch();
        harmony.CreateClassProcessor(typeof(RcsGaugePatches.PackDataPatch)).Patch();
        harmony.CreateClassProcessor(typeof(RcsGaugePatches.HoveredPatch)).Patch();
        harmony.CreateClassProcessor(typeof(RcsBurnWindowUi)).Patch();
        harmony.CreateClassProcessor(typeof(RcsBurnCanvasUi)).Patch();
```

6. **Leave the click alone.** The button queues the click as usual; stock applies `Vehicle.SetEnum` or `ToggleEnum` during `InputEvents` application, and the mod's own execution code intercepts it there (see [Execute burns with RCS and throttle inputs](../../../flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/overview.md)).

7. **Test.** Plan a burn on a vehicle with RCS thrusters but no engine: the Auto button is enabled with your tooltip; click it and it lights while the burn runs; without a burn it reverts to stock behaviour.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs#L18-L131) | The three patches and the button identification |
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L297-L305) | `_enumValue` and `Vehicle.Hovered(FlightComputerBurnMode)` handles |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L185-L193) | Isolated apply |

## Variations and gotchas

- **Patch the concrete class, not the generics.** AFC's RCS code comment says Harmony cannot reliably patch `Vehicle.IsFlightComputerDisabled<T>` / `IsSet<T>`. AFC's own AUTOSTAGE button ([approach 1](work-with-stock-cockpit-gauges-1.md)) does patch those, closed over `System.Enum`, so the conservative choice here is that, not the only working one.
- **Bits changed twice.** `IsDisabled` is used by hover and click handling, `PackData` by the render; patch both or the button looks enabled but ignores clicks (or the reverse).
- **A prefix that suppresses a stock tooltip** and then fails to draw its own hides the problem; AFC logs that case once.
- **Cache the "would execute" decision.** `IsDisabled` and `PackData` run for every button every frame; AFC uses cached helpers with a short lifetime.
- **`PackData` bit layout is an internal detail** of the instance buffer; re-check it after game updates ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L156-L173)).
- **Private `Hovered` overloads** can be renamed; validate the handle at load and degrade by skipping the tooltip patch.

## See also

- [Overview](overview.md)
- [Approach 1: register the enum at immediate load, append the Button by XML patch](work-with-stock-cockpit-gauges-1.md)
- [Inject UI into the game's own windows and menus](../inject-ui-into-a-stock-window/overview.md) (the burn editors next to this button)
- [Execute burns with RCS and throttle inputs](../../../flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/overview.md)
- Mod feature: [RCS burn editor and gauge UI](../../../../mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui.md)

[^ksa-ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-canvasrenderer]: KSA.CanvasRenderer (decompiled, build 2026.10.10.5554)
