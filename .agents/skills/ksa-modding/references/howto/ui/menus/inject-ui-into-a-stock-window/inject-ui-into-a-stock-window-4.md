---
type: How-To
title: Add a block under the stock burn editors with Harmony postfixes
description: Postfix Burn.DrawBurnEditorWindowContent and BurnCanvasHost.Draw to draw extra controls after the stock content, choosing a gauge-styled panel when the canvas is docked and a plain ImGui window when it is detached
category: ui
group: menus
topic: inject-ui-into-a-stock-window
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui]
techniques: [harmony-postfix, imgui-window]
hooks:
  - harmony-postfix KSA.Burn.DrawBurnEditorWindowContent
  - harmony-postfix KSA.BurnCanvasHost.Draw
  - starmap StarMapAllModsLoaded
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.Burn
  - KSA.BurnCanvasHost
  - KSA.GaugeCanvas
  - KSA.GaugeBase
  - KSA.Program
  - KSA.ImGauge
  - KSA.ImGaugeWindow
tags: [burn-editor, gauge-canvas, postfix, imgui, imgauge, detached, stock-window]
---
Not every stock window needs IL surgery. When the stock method **ends** where your content should start, a Harmony postfix is enough. AFC adds an RCS block (execution method, attitude strategy, allocator, estimates, warnings) under the two stock burn editors this way: a postfix on `Burn.DrawBurnEditorWindowContent` for the rendezvous infobox, and a postfix on `BurnCanvasHost.Draw` for the flight burn editor on the gauge canvas. The second one has a twist: a docked gauge canvas can only be drawn into with the game's gauge-styled `ImGauge` primitives, while a detached canvas lives in its own OS window where `ImGauge` cannot draw, so AFC falls back to a plain ImGui window pinned to the gauge's corner. Prerequisites: StarMap and Harmony. Demonstrated by [RCS burn editor blocks and gauge UI](../../../../mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui.md).

## Background: how the game does it

- **Rendezvous infobox.** `Burn.DrawBurnEditorWindowContent(Vehicle vehicle, FlightComputer flightComputer, bool allowTimeChanges = true, bool showParentBurn = true)` draws the editor body ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L727));[^ksa-ksa-burn] its only stock caller is `TargetTrackWindow` ([current/decomp/KSA/TargetTrackWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TargetTrackWindow.cs#L755)).[^ksa-ksa-targettrackwindow]
- **Flight burn editor.** `BurnCanvasHost.Draw(GaugeCanvas canvas, float2 canvasMinPixels, float2 canvasSizePixels)` is handed the canvas rectangle and returns early when `Program.ActiveBurn` is null ([current/decomp/KSA/BurnCanvasHost.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnCanvasHost.cs#L53-L60)).[^ksa-ksa-burncanvashost] `GaugeCanvas` calls it for `Program.BurnCanvas` inside the gauge host scope ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L866-L874)).[^ksa-ksa-gaugecanvas]
- **Docked or detached.** `GaugeBase.Detached` is a public bool; a detached canvas draws in its own ImGui window ([current/decomp/KSA/GaugeBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeBase.cs#L26)).[^ksa-ksa-gaugebase] `ImGauge.BeginWindow(in ImGaugeWindow, out float2, out float2)` is the gauge-styled window API. It positions the window from the main viewport, calls `ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID)` and adds a draw-list callback that renders the queued gauge primitives ([current/decomp/KSA/ImGauge.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGauge.cs#L175-L214)).[^ksa-ksa-imgauge] It therefore always lands in the main viewport, which is why a canvas detached onto its own OS window needs a different renderer.

## Lifecycle and timing

- **Patch at `startup:all-mods-loaded`**; both postfix classes are plain `[HarmonyPatch]` attributes.
- **Both postfixes run at `frame:draw-ui`** on the main thread: the burn infobox from `TargetTrackWindow`, the canvas host from `GaugeCanvas` drawing (itself on the UI pass). Neither runs while the HUD is hidden. They run **every frame the editor is open**, so allocation and log noise matter: AFC logs a persistent failure once per exception type.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Postfix the infobox.** Match the target by exact parameter types (the method has default arguments) and draw after stock. Wrap in `try`/`catch` and log once:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsBurnWindowUi.cs L14-L30
[HarmonyPatch(typeof(Burn), nameof(Burn.DrawBurnEditorWindowContent),
    new Type[] { typeof(Vehicle), typeof(FlightComputer), typeof(bool), typeof(bool) })]
internal static class RcsBurnWindowUi
{
    static void Postfix(Burn __instance, Vehicle vehicle, FlightComputer flightComputer)
    {
        try
        {
            RcsBurnUi.DrawBlock(__instance, vehicle, flightComputer);
        }
        catch (Exception ex)
        {
            // Once per load: this runs every frame the editor is open, and a persistent draw failure would otherwise flood the log.
            LogHelper.WarnOnce("rcs-burn-window:" + ex.GetType().Name,
                $"[AFC] RcsBurnWindowUi failed for vehicle='{vehicle.Id}': {ex}");
        }
    }
```

2. **Postfix the canvas host and branch on docking.** Use the rectangle the host was given so your block appears and disappears with the editor; ignore burns the block does not apply to:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs L19-L43
[HarmonyPatch(typeof(BurnCanvasHost), nameof(BurnCanvasHost.Draw),
    new[] { typeof(GaugeCanvas), typeof(float2), typeof(float2) })]
internal static class RcsBurnCanvasUi
{
    static void Postfix(GaugeCanvas canvas, float2 canvasMinPixels, float2 canvasSizePixels)
    {
        Burn? burn = Program.ActiveBurn;
        if (burn == null || burn.ParentDepartureBurn)
            return;
        Vehicle vehicle = burn.Vehicle;
        try
        {
            if (canvas.Detached)
                DrawImGuiFallback(burn, vehicle, canvasMinPixels, canvasSizePixels);
            else
                RcsGaugePanel.Draw(burn, vehicle, vehicle.FlightComputer,
                    canvasMinPixels, canvasSizePixels);
        }
        catch (Exception ex)
        {
            // Once per load: this runs every frame the editor is open, and a persistent draw failure would otherwise flood the log.
            LogHelper.WarnOnce("rcs-burn-canvas:" + ex.GetType().Name,
                $"[AFC] RcsBurnCanvasUi failed for vehicle='{vehicle.Id}': {ex}");
        }
    }
```

3. **Draw the detached fallback as a plain ImGui window** anchored to the gauge's bottom-right corner (`SetNextWindowPos` with a pivot of `(1, 0)`), with flags that keep it from stealing the keyboard, and always pair `Begin` with `End` in a `finally`:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs L45-L66
    private static void DrawImGuiFallback(
        Burn burn, Vehicle vehicle, float2 canvasMinPixels, float2 canvasSizePixels)
    {
        // Top-right pinned to the gauge's bottom-right corner; AlwaysAutoResize then grows the window down and to the left.
        float2 anchor = new float2(
            canvasMinPixels.X + canvasSizePixels.X,
            canvasMinPixels.Y + canvasSizePixels.Y);
        ImGui.SetNextWindowPos(in anchor, ImGuiCond.Always, new float2(1f, 0f));
        ImGui.Begin("RCS###AfcRcsBurnPanel"u8,
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
        try
        {
            RcsBurnUi.DrawBlock(burn, vehicle, vehicle.FlightComputer);
        }
        finally
        {
            // The matching End must run even if the block throws, or every later ImGui window nests inside this one.
            ImGui.End();
        }
    }
```

4. **Draw the docked panel with `ImGauge`.** Build an `ImGaugeWindow` from the canvas rectangle in UV space (relative to the main viewport, hence subtracting its position), `BeginWindow`, `DrawDressedBox`, then labels and buttons; always `EndWindow`. Labels are upper case, at most 16 glyphs, and the window registry has no removal, so use one stable id:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePanel.cs L91-L106
        // OffsetUv is relative to the main viewport, hence the Pos subtraction.
        float2 topLeft = canvasMinPixels + new float2(0f, canvasSizePixels.Y)
            - ImGui.GetMainViewport().Pos;
        // ImGauge's window registry has no removal API, so these resources outlive a mod unload; a stable id caps that at one reused entry.
        var window = new ImGaugeWindow(
            "AfcRcsBurn", "AFC RCS Burn",
            anchorUv: float2.Zero,
            pivotUv: float2.Zero,
            offsetUv: ScreenReference.PixelsToUv(topLeft),
            sizeUv: ScreenReference.PixelsToUv(new float2(canvasSizePixels.X, heightPixels)));

        // BeginWindow always succeeds and pushes style state that EndWindow must pop.
        ImGauge.BeginWindow(in window, out float2 posPixels, out float2 sizePixels);
        try
        {
            ImGauge.DrawDressedBox(posPixels, sizePixels);
```

5. **Keep the layout in step with stock.** AFC's constants mirror the row height and pitch of `BurnCanvasHost.Draw` ([current/decomp/KSA/BurnCanvasHost.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnCanvasHost.cs#L40-L52)); if stock changes its layout the block misaligns but still draws.

6. **Check both modes in game.** Open the burn editor docked and detached (the gauge's own context menu has a "Detached" item when `Program.AllowMultipleWindows` is on, [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L959)) and confirm the block follows the editor in each.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsBurnWindowUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnWindowUi.cs#L14-L31) | Postfix on the rendezvous burn infobox |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsBurnCanvasUi.cs#L19-L67) | Postfix on the canvas host, docked versus detached |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePanel.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsGaugePanel.cs#L30-L120) | Gauge-styled panel with `ImGauge` |

## Variations and gotchas

- **`ImGauge` draws into the main viewport only.** Any gauge-styled UI needs an ImGui fallback for detached canvases.
- **A postfix cannot go inside the window.** It runs after the method returns, so it works here only because the stock content is last. For anything in the middle, use a transpiler (approaches 1 to 3).
- **Hot path.** These postfixes run every frame; cache estimates (AFC keeps a short-lived cache) and log once.
- **Stock tooltip suppression** in a prefix is risky: if you skip the stock tooltip and then fail to draw your own, nothing explains the state. AFC logs that case once.
- **Layout drift.** Hard-coded fractions copied from stock (row height, gap) can drift on updates; keep them in one place.
- **Gauge buttons.** To change a stock gauge button's behavior rather than add UI, see [Add to, drive and hide the stock cockpit gauges and navball](../work-with-stock-cockpit-gauges/overview.md).

## See also

- [Overview](overview.md)
- [Approach 3: call-site swap in the plan window](inject-ui-into-a-stock-window-3.md)
- [Add to, drive and hide the stock cockpit gauges and navball](../work-with-stock-cockpit-gauges/overview.md)
- [Match the stock console UI style](../../windows/match-the-stock-console-ui-style/overview.md)
- [Create and read maneuver nodes](../../../flight-and-orbits/autopilot/create-and-read-maneuver-nodes/overview.md)
- Mod feature: [RCS burn editor blocks and gauge UI](../../../../mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui.md)

[^ksa-ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
[^ksa-ksa-targettrackwindow]: KSA.TargetTrackWindow (decompiled, build 2026.10.10.5554)
[^ksa-ksa-burncanvashost]: KSA.BurnCanvasHost (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gaugebase]: KSA.GaugeBase (decompiled, build 2026.10.10.5554)
[^ksa-ksa-imgauge]: KSA.ImGauge (decompiled, build 2026.10.10.5554)
