---
type: Mod Feature
title: HUD stat tiles and threshold alerts
description: A player-ordered set of up to nine stat tiles and twelve threshold alert rules evaluated against one per-frame context record, surfaced as a warning banner, outlined tiles, warning-coloured rows and a dashed threshold line on the history graph
mod: kitten-engineer-redux
category: ui
techniques: [imgui-drawlist, imgui-window, json-config]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [hud, alerts, widgets, imgui, drawlist, customization, flight]
---

The flight HUD's top row is a grid of stat tiles the player chooses and orders (apoapsis, periapsis,
delta-v, TWR, max TWR, altitude, orbital speed, time to apoapsis or periapsis, mass, thrust, vertical
speed, acceleration, fuel). Alerts are simple "value below or above threshold" rules the player can
enable per metric; when one trips, a banner lists it, any tile and row showing that metric turns the
warning colour, and the history graph draws the threshold as a dashed line.

## How it works

- **One context, three consumers.** `FlightHud.Draw` builds a `HudWidgetContext` record (orbit
  summary, total delta-v, actual and max TWR, mass, thrust, acceleration, total and active-stage fuel
  fraction) and calls `HudAlerts.Evaluate(context)` before drawing anything
  ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L149-L153)).[^ker-flighthud] Tiles, rows and the graph then ask
  `HudAlerts.IsTriggered(metric)` or `TryGetThreshold` instead of recomputing.
- **Rules.** `HudAlerts.Rules` is a fixed array of `AlertRule` objects, one per `AlertMetric`, each with
  a default direction, threshold and input step. All rules start disabled. `Evaluate` clears every
  `Triggered` flag, maps the metric to a context value (`TryGetValue` returns false for metrics that
  are undefined right now, such as apoapsis on an escape trajectory) and compares
  ([HudAlerts.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudAlerts.cs#L65-L103)).[^ker-hudalerts]
- **Banner.** `DrawBanner` paints a translucent warning rectangle with a solid left bar and one line
  per active rule, `Name: value unit (< threshold)`, straight onto the window draw list
  ([HudAlerts.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudAlerts.cs#L145-L171)).
- **Configurator.** A popup with an ON/OFF mini-button, a `<`/`>` direction toggle and an
  `ImGui.InputDouble` per rule. Widget IDs (`##KerAlertEnable{n}`) and the UTF-8 input label are built
  once in the rule constructor so the per-frame draw does not allocate them.
- **Tiles.** `HudWidgets.Selected` is a `List<HudWidget>` (default apoapsis, periapsis, delta-v; max
  nine). `Build` produces parallel label, value and warning arrays; `PanelKit.DrawStatTiles` lays them
  out in as many columns as fit (96 px minimum per tile, scaled by text scale), draws the value in a
  1.3x font via `ImGui.PushFont(ImGui.GetFont(), size)` and drops back to the normal size when the
  value would not fit ([HudWidgets.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudWidgets.cs#L69-L82), [PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L209-L261)).[^ker-hudwidgets] [^ker-panelkit]
- **Picker.** The "Customize widgets" popup lists active tiles with up/down/remove mini-buttons and
  available tiles with Add; all edits are collected during the draw and applied once after it, so the
  list is never mutated while being iterated ([HudWidgets.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudWidgets.cs#L94-L180)).
- **Persistence.** Tile order is saved as enum names and alert rules as
  `{Metric, Enabled, Above, Threshold}` in `settings.json`; unknown names are ignored on load
  ([SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L143-L226)).[^ker-settingsstore]

## Lifecycle and timing

- Everything runs inside the flight HUD postfix on `Vehicle.OnDrawUi` (`frame:draw-ui`), once per
  frame for the controlled vehicle. Evaluation happens before any drawing in the same call, so the
  banner, tiles and section rows all agree within a frame.
- Popups (`ImGui.OpenPopup` / `BeginPopup`) are opened from the HUD window, so they live only while
  the HUD is drawn.
- Alerts are visual only. Nothing in the mod plays a sound, pauses the game or touches the vehicle.

## Key code

| Path | Role |
|---|---|
| [HudAlerts.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudAlerts.cs) | Rule table, evaluation, banner, configurator popup |
| [HudWidgets.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudWidgets.cs) | Tile catalogue, values, alert mapping, picker popup |
| [PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L209-L261) | `DrawStatTiles` |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L149-L189) | Context build, evaluate, banner and tile draw order |

## Snippets

```csharp
// HudAlerts.cs L83-L103
    public static void Evaluate(HudWidgetContext context)
    {
        Active.Clear();
        for (int i = 0; i < Rules.Length; i++)
            Rules[i].Triggered = false;

        if (!Enabled)
            return;

        for (int i = 0; i < Rules.Length; i++)
        {
            AlertRule rule = Rules[i];
            if (!rule.Enabled || !TryGetValue(rule.Metric, context, out double value))
                continue;

            rule.LastValue = value;
            rule.Triggered = rule.Above ? value > rule.Threshold : value < rule.Threshold;
            if (rule.Triggered)
                Active.Add(rule);
        }
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ImGui.PushFont(ImFontPtr, float)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872) | Larger tile value text without loading a second font |
| `ImGui.InputDouble`, `OpenPopup`, `BeginPopup` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Threshold editing and popups |

The metric values themselves come from the game reads documented in
[Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md).[^brutal-imgui]

## Pitfalls and version notes

- `HudWidgets.Build` allocates three new arrays per frame; fine for nine tiles, but pool them if you
  copy the pattern for larger lists.
- The default periapsis and apoapsis thresholds are a fixed 70 km, not derived from the current
  body's atmosphere height.
- The tile fit test measures the value with `ImGui.CalcTextSize` while the larger font is pushed and
  pops it early when it does not fit; keep push and pop balanced on both branches if you adapt it.

## See also

- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [Draw-list line graph](draw-list-line-graph.md)
- [Draw-list widget kit with pop-out sections](draw-list-widget-kit.md)
- [JSON settings with autosave](json-settings-autosave.md)
- [Evaluate threshold rules once per frame and render them as banner, tile outline, warning row and graph line](../../../howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-5.md) - how-to article demonstrating this feature

[^ker-flighthud]: FlightHud.cs
[^ker-hudalerts]: HudAlerts.cs
[^ker-hudwidgets]: HudWidgets.cs
[^ker-panelkit]: PanelKit.cs
[^ker-settingsstore]: SettingsStore.cs
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
