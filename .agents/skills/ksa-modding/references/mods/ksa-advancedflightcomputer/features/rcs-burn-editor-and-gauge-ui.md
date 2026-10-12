---
type: Mod Feature
title: RCS options in the burn editor and a re-enabled Auto gauge button
description: Postfixes add per-burn RCS settings to the stock burn editors (gauge-styled when docked, plain ImGui when detached) and rewrite the Auto gauge button's disabled bits and tooltip for engineless vehicles
mod: ksa-advancedflightcomputer
category: ui
techniques: [harmony-postfix, harmony-prefix, reflection, imgui-window]
hooks:
  - harmony-postfix KSA.Burn.DrawBurnEditorWindowContent
  - harmony-postfix KSA.BurnCanvasHost.Draw
  - harmony-postfix KSA.GaugeButtonFlightComputer.IsDisabled
  - harmony-postfix KSA.GaugeButtonFlightComputer.PackData
  - harmony-prefix KSA.Vehicle.Hovered
  - reflection KSA.GaugeButtonFlightComputer._enumValue
lifecycle: [frame:draw-ui, frame:render]
game_types:
  - KSA.Burn
  - KSA.BurnCanvasHost
  - KSA.GaugeCanvas
  - KSA.GaugeButtonFlightComputer
  - KSA.ImGauge
  - KSA.Vehicle
  - KSA.BurnTarget
tags: [gauges, burn-editor, imgui, imgauge, tooltip, rcs]
---

The stock Auto burn button is the single trigger for RCS burns, but stock disables it on a vehicle
without an engine (it requires an engine-derived `BurnDuration > 0`) and its tooltip says "no
engine". AFC re-enables the button for burns that resolve to RCS and adds an RCS block (execution
method, attitude strategy, allocator, estimates, warnings) under the stock burn editors.

## How it works

### Burn editor blocks

- **Rendezvous infobox.** A postfix on `Burn.DrawBurnEditorWindowContent(Vehicle, FlightComputer, bool, bool)`
  draws `RcsBurnUi.DrawBlock` after stock content; `TargetTrackWindow` is the only stock caller
  ([AdvancedFlightComputer/Features/RcsTranslation/RcsBurnWindowUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnWindowUi.cs), [current/decomp/KSA/TargetTrackWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TargetTrackWindow.cs#L755)).
- **Flight burn editor.** The editor on the gauge canvas is drawn by `BurnCanvasHost.Draw(GaugeCanvas, float2, float2)`,
  called from `GaugeCanvas` when the canvas is `Program.BurnCanvas` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L866-L874)).
  A postfix anchors AFC's block to the rectangle it is handed, so it appears and disappears with the
  editor. Docked, it draws a gauge-styled panel with `ImGauge.BeginWindow`, `DrawDressedBox`,
  `Label` and `Button` (labels upper case, at most 16 glyphs). A detached canvas lives in its own OS
  window where `ImGauge` cannot draw, so AFC falls back to a plain ImGui window pinned to the gauge's
  bottom-right corner ([AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs),
  [AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePanel.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePanel.cs)).
- **Estimates for later burns.** Stock loads only the first executable burn as its `BurnTarget`.
  For any other burn, `RcsBurnPreview` keeps a scratch `BurnTarget` per `Burn` in a
  `ConditionalWeakTable`, fills it with `BurnTarget.UpdateFromBurn(burn)` and estimates against the
  current vehicle, labelled "Estimate basis: Current vehicle"
  ([AdvancedFlightComputer/Features/RcsTranslation/RcsBurnPreview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnPreview.cs)).
- Selectors freeze while an execution holds the resolved strategy; the options are saved per burn in
  `rcs-exec.toml` (see [save-scoped registries](save-scoped-sidecar-registries.md)).

### Auto gauge button

- **Which button.** Patches target `GaugeButtonFlightComputer` itself, not the generic
  `Vehicle.IsFlightComputerDisabled<T>` / `IsSet<T>` helpers, which the RCS code's comment says Harmony
  cannot patch reliably. AFC's own [AUTOSTAGE button](autostage-gauge-button.md) does patch those helpers,
  closed over `System.Enum` (the instantiation the gauge binding calls), and `afc-autostage-per-vehicle`
  checks that `IsSet<Enum>` answers through the patch, so the RCS choice is the more conservative one, not
  the only working one.
  The button's bound value lives in the private `_enumValue`; AFC reads it through a validated
  `FieldInfo` and caches it per button in a `ConditionalWeakTable`, re-reading when the button's
  `EnumType`/`Value` strings change ([AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs#L18-L50)).
- **IsDisabled postfix.** When stock says disabled but the controlled vehicle would execute (or is
  executing) the burn with RCS, return `false`.
- **PackData postfix.** `PackData` packs the render state into bits (bit0 clicked, bit1 lit,
  bit2 disabled, bit3 enabled) ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L156-L173)).[^ksa-gaugebuttonflightcomputer]
  AFC clears bit2, sets bit3 and, while running, sets bit1, because internally the burn mode stays
  Manual during an RCS burn.
- **Tooltip prefix.** The private `Vehicle.Hovered(FlightComputerBurnMode)` is resolved by
  `TargetMethod()` from a validated handle; for Auto on the controlled vehicle it draws "Execute burn
  with RCS" or "RCS burn running - click to cancel" with `ImGuiHelper.DrawTooltip` and skips stock.
- Clicks themselves are untouched: stock queues them and `Vehicle.SetEnum` runs during the input
  drain, where [RCS burn execution](rcs-burn-execution.md) intercepts them.

## Lifecycle and timing

All of these run on the main thread. The burn editors and the gauge canvases' ImGui parts are drawn
from `Program.OnDrawUiThreadSafe` (`frame:draw-ui`), and so are `IsDisabled` and `Hovered`, which the
gauge's hover and click handling in `GaugeCanvas.OnDrawUi` calls. `PackData` runs later in the frame:
the gauge pass of `Program.RenderGame` fills each bundle's instance buffer through
`GaugeBundle.UpdateInstanceBuffer` -> `GaugeButtonReference.PackData1` -> `PackData` (`frame:render`)
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940), [current/decomp/KSA/CanvasRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CanvasRenderer.cs#L49-L59)).
A `PackData` postfix therefore must not call ImGui. Nothing here mutates simulation state; the estimate caches
have a 250 ms lifetime to bound repeated thruster and tank scans from per-frame draws.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsBurnWindowUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnWindowUi.cs) | Postfix on the rendezvous burn infobox |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs) | Postfix on the flight burn editor host, docked vs detached |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePanel.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePanel.cs) | Gauge-styled panel with `ImGauge` |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsBurnUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnUi.cs) | ImGui block: method, strategy, allocator, warnings |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsBurnPreview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnPreview.cs) | Scratch `BurnTarget` estimates for non-loaded burns |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePatches.cs) | Auto button enable bits and tooltip |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Burn.DrawBurnEditorWindowContent` ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L727)) | Rendezvous burn editor content |
| `BurnCanvasHost.Draw(GaugeCanvas, float2, float2)` ([current/decomp/KSA/BurnCanvasHost.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnCanvasHost.cs#L53)), `GaugeCanvas.Detached` | Flight burn editor host and docking state |
| `ImGauge.BeginWindow`, `DrawDressedBox`, `Label`, `Button` ([current/decomp/KSA/ImGauge.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGauge.cs)) | Gauge-styled drawing (main viewport only) |
| `GaugeButtonFlightComputer.IsDisabled`, `PackData`, `_enumValue` ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L151-L173)) | Button state |
| `Vehicle.Hovered(FlightComputerBurnMode)` (private, [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6832)) | Tooltip |
| `BurnTarget.UpdateFromBurn(Burn)` ([current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs)) | Scratch target for previews |
| `ImGuiHelper.DrawTooltip` ([current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs)) | Stock tooltip style |

## Pitfalls and version notes

- `ImGauge` only draws into the main viewport; any gauge-styled UI needs an ImGui fallback for
  detached canvases.
- Suppressing a stock tooltip in a prefix and then failing to draw your own hides the problem; AFC
  logs that case once.
- Patching the concrete caller (`GaugeButtonFlightComputer`) avoids depending on how a generic method
  such as `IsSet<T>` shares code between instantiations; AFC's AUTOSTAGE button takes the other route
  and patches the `Enum` instantiation directly.

## See also

- [RCS burn execution](rcs-burn-execution.md)
- [RCS thruster allocation](rcs-thruster-allocation.md)
- [AUTOSTAGE gauge button](autostage-gauge-button.md) (another gauge button, added by XML)
- [Stock-styled console UI](stock-styled-console-ui.md)
- [Add a block under the stock burn editors with Harmony postfixes](../../../howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-4.md) - how-to article demonstrating this feature
- [Re-enable a stock gauge button and rewrite its tooltip with Harmony postfixes on GaugeButtonFlightComputer](../../../howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-2.md) - how-to article demonstrating this feature

[^ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
