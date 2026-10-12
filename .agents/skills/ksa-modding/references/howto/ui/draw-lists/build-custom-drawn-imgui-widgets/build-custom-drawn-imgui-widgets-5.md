---
type: How-To
title: Evaluate threshold rules once per frame and render them as banner, tile outline, warning row and graph line
description: Keep a fixed table of enable-able value-above-or-below rules, evaluate it once against a shared context, and let every widget ask whether a metric is triggered instead of recomputing
category: ui
group: draw-lists
topic: build-custom-drawn-imgui-widgets
approach: 5
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/hud-widgets-and-threshold-alerts]
techniques: [imgui-drawlist, harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImDrawListPtr, KSA.Program, KSA.Vehicle]
tags: [alerts, thresholds, tiles, banner, hud, rules]
---
Kitten-Engineer-Redux's flight HUD has a grid of stat tiles the player chooses and orders, and simple "value below or above threshold" alert rules the player enables per metric. When a rule trips, a banner lists it, any tile or row showing that metric turns the warning colour, and the history graph draws the threshold as a dashed line. The design is the useful part: a rule table evaluated once, against one context record, then consulted by every widget. Demonstrated by [HUD stat tiles and threshold alerts](../../../../mods/kitten-engineer-redux/features/hud-widgets-and-threshold-alerts.md). Prerequisite: a drawn panel such as the one in [approach 1](build-custom-drawn-imgui-widgets-1.md).

## Background: how the game does it

The game offers no generic threshold-alert system for mods to hook; for transient on-screen alerts and popups see [Show transient alerts and game-styled popups in game](../../../../howto/ui/windows/show-alerts-and-popups-in-game/overview.md). KER computes the alert values from vehicle state it reads each frame (the reads are described in [Show live vehicle and editor data in an ImGui window or HUD](../../../../howto/ui/windows/show-live-vehicle-data-in-a-window/overview.md)); the controlled vehicle is `Program.ControlledVehicle` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502)[^ksa-program]). Everything this approach adds is ordinary mod code over `ImDrawListPtr` and `PushFont(font, size)` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872-L5875)[^brutal-imguiapi-imgui]), which makes the tile value larger without loading a second font.

## Lifecycle and timing

Everything runs inside the flight HUD postfix on `Vehicle.OnDrawUi` (`frame:draw-ui`), once per frame for the controlled vehicle. Evaluation happens before any drawing in the same call, so banner, tiles and rows agree within a frame. Popups (`ImGui.OpenPopup`/`BeginPopup`) are opened from the HUD window, so they exist only while the HUD is drawn. Alerts are visual only: no sound, no pause, no change to the vehicle. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the metrics and a rule type.** One rule per metric with a default direction, threshold and input step; widget ids and the UTF-8 input label are built once in the constructor so per-frame drawing does not allocate them:

```csharp
// HudAlerts.cs L9-L56
internal enum AlertMetric
{
    TotalDeltaV,
    ActualTwr,
    MaximumTwr,
    Altitude,
    PeriapsisAltitude,
    ApoapsisAltitude,
    TimeToPeriapsis,
    TimeToApoapsis,
    VerticalSpeed,
    Acceleration,
    TotalFuel,
    StageFuel,
}

internal sealed class AlertRule
{
    public AlertRule(AlertMetric metric, string name, string unit, bool above, double threshold, double step)
    {
        Metric = metric;
        Name = name;
        Unit = unit;
        DefaultAbove = above;
        DefaultThreshold = threshold;
        Above = above;
        Threshold = threshold;
        Step = step;
        EnableId = $"##KerAlertEnable{(int)metric}";
        DirectionId = $"##KerAlertDirection{(int)metric}";
        InputId = Encoding.UTF8.GetBytes($"##KerAlertValue{(int)metric}");
    }

    public AlertMetric Metric { get; }
    public string Name { get; }
    public string Unit { get; }
    public double Step { get; }
    public bool DefaultAbove { get; }
    public double DefaultThreshold { get; }
    public string EnableId { get; }
    public string DirectionId { get; }
    public byte[] InputId { get; }
    public bool Enabled;
    public bool Above;
    public double Threshold;
    public double LastValue;
    public bool Triggered;
}
```

2. **Hold the rules in a fixed array** (all start disabled) and clear a list of active rules each frame:

```csharp
// HudAlerts.cs L58-L81
internal static class HudAlerts
{
    private const float RowNameWidth = 170f;
    private const float PanelWidth = 430f;

    public static bool Enabled { get; set; } = true;

    public static readonly AlertRule[] Rules =
    {
        new(AlertMetric.TotalDeltaV, "Total dV", "m/s", false, 500.0, 50.0),
        new(AlertMetric.ActualTwr, "TWR (actual)", "", false, 1.0, 0.1),
        new(AlertMetric.MaximumTwr, "TWR (max)", "", false, 1.0, 0.1),
        new(AlertMetric.Altitude, "Altitude", "m", false, 1000.0, 100.0),
        new(AlertMetric.PeriapsisAltitude, "Periapsis", "m", false, 70000.0, 1000.0),
        new(AlertMetric.ApoapsisAltitude, "Apoapsis", "m", false, 70000.0, 1000.0),
        new(AlertMetric.TimeToPeriapsis, "Time to periapsis", "s", false, 30.0, 5.0),
        new(AlertMetric.TimeToApoapsis, "Time to apoapsis", "s", false, 30.0, 5.0),
        new(AlertMetric.VerticalSpeed, "Vertical speed", "m/s", false, -50.0, 5.0),
        new(AlertMetric.Acceleration, "Acceleration", "g", true, 6.0, 0.5),
        new(AlertMetric.TotalFuel, "Total fuel", "%", false, 15.0, 5.0),
        new(AlertMetric.StageFuel, "Active stage fuel", "%", false, 10.0, 5.0),
    };

    private static readonly List<AlertRule> Active = new();
```

3. **Evaluate once per frame.** Reset every `Triggered` flag, skip disabled rules and metrics that are undefined right now, compare in the configured direction, and collect the active ones:

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

`TryGetValue` maps a metric to a value from the shared context and returns false for undefined ones (apoapsis on an escape trajectory), so an undefined value never trips a rule ([HudAlerts.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudAlerts.cs#L237-L289)).

4. **Build the context first, then evaluate, then draw.** The HUD builds a `HudWidgetContext` record, calls `Evaluate`, and only then opens the window:

```csharp
// FlightHud.cs L149-L153
ComputeFuelFractions(f.Burn, f.ActiveSequence, out float totalFuelFraction, out float stageFuelFraction);
HudWidgetContext widgetContext = new(
    f.Orbit, f.Burn.TotalDeltaV, f.ActualTwr, f.MaximumTwr, f.Mass,
    f.Engine.TotalThrust, f.AccelerationG, totalFuelFraction, stageFuelFraction);
HudAlerts.Evaluate(widgetContext);
```

5. **Let consumers ask instead of recomputing.** `IsTriggered(metric)` and `TryGetThreshold(metric, ...)` are linear scans over a dozen rules:

```csharp
// HudAlerts.cs L105-L132
public static bool IsTriggered(AlertMetric metric)
{
    for (int i = 0; i < Rules.Length; i++)
    {
        if (Rules[i].Metric == metric && Rules[i].Triggered)
            return true;
    }
    return false;
}

public static bool TryGetThreshold(AlertMetric metric, out float threshold, out bool above)
{
    threshold = 0f;
    above = false;
    if (!Enabled)
        return false;

    for (int i = 0; i < Rules.Length; i++)
    {
        AlertRule rule = Rules[i];
        if (rule.Metric != metric || !rule.Enabled)
            continue;
        threshold = (float)rule.Threshold;
        above = rule.Above;
        return true;
    }
    return false;
}
```

6. **Render the banner** straight onto the window draw list: a translucent warning rectangle, a solid left bar and one line per active rule:

```csharp
// HudAlerts.cs L145-L171
public static float DrawBanner(ImDrawListPtr dl, float2 origin, float width, float y)
{
    if (Active.Count == 0)
        return y;

    float rowHeight = PanelKit.RowHeight;
    float lineHeight = ImGui.GetTextLineHeight();
    float height = Active.Count * rowHeight + 8f;
    float4 warning = PanelKit.WarningColor;

    float2 bgMin = new float2(origin.X, y);
    float2 bgMax = new float2(origin.X + width, y + height);
    dl.AddRectFilled(in bgMin, in bgMax, PanelKit.ToColor(new float4(warning.X, warning.Y, warning.Z, 0.18f)), PanelKit.CornerRadius);
    float2 barMax = new float2(origin.X + 3f, y + height);
    dl.AddRectFilled(in bgMin, in barMax, PanelKit.ToColor(warning), PanelKit.CornerRadius);

    float textY = y + 4f + (rowHeight - lineHeight) * 0.5f;
    for (int i = 0; i < Active.Count; i++)
    {
        AlertRule rule = Active[i];
        string unit = rule.Unit.Length > 0 ? $" {rule.Unit}" : string.Empty;
        string text = $"{rule.Name}: {FormatNumber(rule.LastValue)}{unit} ({(rule.Above ? ">" : "<")} {FormatNumber(rule.Threshold)})";
        float2 textPos = new float2(origin.X + 12f, textY + i * rowHeight);
        dl.AddText(in textPos, PanelKit.ToColor(warning), text);
    }
    return y + height + 6f;
}
```

7. **Render tiles with an outline when triggered.** `HudWidgets.Build` produces parallel label, value and warning arrays, and `DrawStatTiles` outlines a warned tile with four lines and picks the warning colour for its value:

```csharp
// HudWidgets.cs L69-L82
public static void Build(HudWidgetContext context, out string[] labels, out string[] values, out bool[] warnings)
{
    int count = Selected.Count;
    labels = new string[count];
    values = new string[count];
    warnings = new bool[count];
    for (int i = 0; i < count; i++)
    {
        labels[i] = TileLabel(Selected[i]);
        values[i] = Value(Selected[i], context);
        AlertMetric? metric = AlertFor(Selected[i]);
        warnings[i] = metric.HasValue && HudAlerts.IsTriggered(metric.Value);
    }
}
```

```csharp
// PanelKit.cs L225-L259
for (int i = 0; i < count; i++)
{
    bool warning = i < warnings.Length && warnings[i];
    int col = i % perRow;
    int row = i / perRow;
    float2 tileMin = new float2(origin.X + col * (tileWidth + gap), y + row * (tileHeight + gap));
    float2 tileMax = tileMin + new float2(tileWidth, tileHeight);
    dl.AddRectFilled(in tileMin, in tileMax, ToColor(ToggleOffColor), CornerRadius);

    if (warning)
    {
        ImColor8 warnColor = ToColor(WarningColor);
        float2 topRight = new float2(tileMax.X, tileMin.Y);
        float2 bottomLeft = new float2(tileMin.X, tileMax.Y);
        dl.AddLine(in tileMin, in topRight, warnColor);
        dl.AddLine(in topRight, in tileMax, warnColor);
        dl.AddLine(in tileMax, in bottomLeft, warnColor);
        dl.AddLine(in bottomLeft, in tileMin, warnColor);
    }

    float2 labelPos = tileMin + new float2(8f, 5f);
    dl.AddText(in labelPos, ToColor(LabelColor), labels[i]);

    ImGui.PushFont(ImGui.GetFont(), bigSize);
    float2 valueSize = ImGui.CalcTextSize(values[i]);
    bool fits = valueSize.X <= tileWidth - 12f;
    if (!fits)
        ImGui.PopFont();

    float2 valuePos = tileMin + new float2(8f, 5f + lineHeight + 3f);
    dl.AddText(in valuePos, ToColor(warning ? WarningColor : ValueColor), values[i]);

    if (fits)
        ImGui.PopFont();
}
```

8. **Render rows and the graph line from the same answers.** Detail rows pass the trigger result as the `warning` argument, and the history graph passes the enabled threshold to the dashed line (see [Draw charts, scopes and relief maps with the ImGui draw list](../../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)):

```csharp
// FlightHud.cs L220-L232
{
    FrameData f = Frame;
    y = PanelKit.DrawRow(dl, origin, width, y, "Thrust (act / max)",
        $"{PanelKit.FormatThrust(f.Engine.TotalThrust)} / {PanelKit.FormatThrust(f.MaximumThrust)}");
    y = PanelKit.DrawRow(dl, origin, width, y, "TWR (act / max)",
        $"{f.ActualTwr:F2} / {f.MaximumTwr:F2}",
        HudAlerts.IsTriggered(AlertMetric.ActualTwr) || HudAlerts.IsTriggered(AlertMetric.MaximumTwr));
    y = PanelKit.DrawRow(dl, origin, width, y, "Vessel mass", PanelKit.FormatMass(f.Mass));
    y = PanelKit.DrawRow(dl, origin, width, y, "Acceleration", $"{f.AccelerationG:F2} g",
        HudAlerts.IsTriggered(AlertMetric.Acceleration));
    y = PanelKit.DrawRow(dl, origin, width, y, "Speed (vert / horiz)",
        $"{f.Orbit.VerticalVelocity:F1} / {f.Orbit.HorizontalVelocity:F1} m/s",
        HudAlerts.IsTriggered(AlertMetric.VerticalSpeed));
```

9. **Edit rules with real controls.** The configurator draws an ON/OFF mini-button, a `<`/`>` direction toggle and an `InputDouble` per rule, with one reset button ([HudAlerts.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudAlerts.cs#L173-L235)). Tile order and rule values are saved as enum names and `{Metric, Enabled, Above, Threshold}` in the mod's JSON settings.

## Complete example

| File | Role |
|---|---|
| [HudAlerts.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudAlerts.cs) | Rule table, evaluation, banner, configurator popup |
| [HudWidgets.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/HudWidgets.cs) | Tile catalogue, values, alert mapping, picker popup |
| [PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L209-L261) | `DrawStatTiles` |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L149-L195) | Context build, evaluate, banner and tile draw order |

## Variations and gotchas

- **Allocation.** `HudWidgets.Build` allocates three arrays per frame; fine for nine tiles, pool them for larger lists.
- **Fixed defaults.** The default periapsis and apoapsis thresholds are a flat 70 km, not derived from the current body's atmosphere height.
- **Balance push and pop.** The tile fit test pushes the large font, measures, and pops early when the value does not fit; both branches must pop exactly once.
- **Mutating while iterating.** The widget picker records clicks and applies them after its loop so the list is never changed while being drawn.
- **No latching.** A rule is true only while the condition holds; add hysteresis or a latch if flicker matters.
- **Version.** Only Brutal ImGui calls and vehicle reads are involved; the vehicle reads are the part that may change on game updates.

## See also

- [Overview of custom-drawn widgets](overview.md)
- [Approach 1: widget kit](build-custom-drawn-imgui-widgets-1.md)
- [Draw charts, scopes and relief maps with the ImGui draw list](../../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)
- [Show transient alerts and game-styled popups in game](../../../../howto/ui/windows/show-alerts-and-popups-in-game/overview.md)
- Mod feature: [HUD stat tiles and threshold alerts](../../../../mods/kitten-engineer-redux/features/hud-widgets-and-threshold-alerts.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
