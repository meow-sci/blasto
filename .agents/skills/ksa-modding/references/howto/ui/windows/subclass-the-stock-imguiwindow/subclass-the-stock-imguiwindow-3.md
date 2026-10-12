---
type: How-To
title: "Stock window base class: IStaticWindow auto-registration with a mod-owned layout persisted to config"
description: Let the game draw your ImGuiWindow subclass by implementing IStaticWindow, and persist position and size yourself because the base class uses NoSavedSettings
category: ui
group: windows
topic: subclass-the-stock-imguiwindow
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/panels-as-static-imgui-windows]
techniques: [imgui-window, json-config, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.ImGuiWindow, KSA.IStaticWindow, KSA.IViewport, KSA.Program]
tags: [window, istaticwindow, static-window, placement, persistence, noSavedSettings]
---
Besides its fixed bottom bar, KSA-Public-Telemetry lets every element (each readout, the engine cluster, the mission clock, the timeline) open as a normal movable, resizable window. They look and behave like the game's own because they are the game's own window class, and
they need no `Begin`/`End` code and no draw hook of the mod's: the subclass also implements `IStaticWindow`, and the game draws it. This article covers that registration, and the persistence the base class does not give you (placement is `NoSavedSettings`). Demonstrated by
KSA-Public-Telemetry. Prerequisite: StarMap.

## Background: how the game does it

- `IStaticWindow` is a tiny interface: `bool IsShown { get; }` and `void OnDrawUi(IGameViewport viewport)` (plus `IDisposable`).[current/decomp/KSA/IStaticWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IStaticWindow.cs#L1-L10)[^ksa-istaticwindow]
- The `ImGuiWindow` constructor adds `this` to a static list when it is an `IStaticWindow`, and `ImGuiWindow.DrawAllStaticWindows(viewport)` calls `OnDrawUi` on every shown entry, iterating from the end of the list. `Program.OnDrawUiFrame` calls it with the main viewport (inside `if (DrawUI)`), between
  `GameSettings.OnDrawUi` and the renderer debug windows.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L99-L138)[^ksa-imguiwindow][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3141)[^ksa-program]
- The game's own tool windows use the same mechanism: `ThreadProfiler`, `UniverseManifest`, `GameSaves`, `VehicleSaves`, `LayoutSaves`, `KittenRosterWindow` and others derive from `ImGuiWindow, IStaticWindow`.
- `OnDrawUi` applies `_initialPosition` and `_initialSize` with `ImGuiCond.Once` and begins the window with `NoSavedSettings`, so ImGui never remembers where the window was.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L186-L210)[^ksa-imguiwindow]
- **Window id.** The default-chrome window name is the title plus `###` plus the subclass's full type name and an underscore (no per-instance part). Two *open* instances of one subclass therefore get the same ImGui id and would merge into one window (inferred from the code; the mod itself notes it was not tested in game).
  The console chrome path adds an instance counter to its id.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L89-L97)[^ksa-imguiwindow][current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L140-L144)[^ksa-imguiwindow]

## Lifecycle and timing

- **Drawn by the game in `frame:draw-ui`.** `DrawAllStaticWindows` runs in `Program.OnDrawUiFrame`, which is *before* StarMap's `[StarMapAfterGui]` postfix. The windows read what the mod stored during the previous frame's overlay draw: a reference to the mod's single `TelemetrySnapshot`, refilled by
  `[StarMapAfterOnFrame]` at the end of the previous frame, so the windows show the same values as the bar drawn later in the same frame.
- **Hidden with the UI.** `OnDrawUiFrame` is inside `if (DrawUI)`, so these windows disappear on F2 even when the bar stays.
- **Construction needs an ImGui frame.** The base constructor calls `ImGui.GetMainViewport()` and `ImGui.GetStyle()`, so the windows are built on the first UI frame (`EnsureBuilt` from `DrawEverything`), not at startup. See
  [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Subclass and implement `IStaticWindow`**, calling the protected base constructor with `show: false`, setting the title, and recording placement from `DrawContent`. `IStaticWindow`'s members are already satisfied by the base class (`IsShown`, `OnDrawUi`):

```csharp
// Rendering/TelemetryWindow.cs L8-L42
public sealed class TelemetryWindow : ImGuiWindow, IStaticWindow
{
    private readonly IOverlayPanel _panel;
    private readonly WindowHost _host;

    public TelemetryWindow(IOverlayPanel panel, string title, float2 initialSize, WindowHost host)
        : base(initialSize, lockAspectRatio: false, show: false)
    {
        _panel = panel;
        _host = host;

        PanelId = panel.Id;
        DisplayTitle = title;
        SetWindowTitle(title);
    }

    public string PanelId { get; }
    public string DisplayTitle { get; }
    public float2 LastPosition { get; private set; }

    public float2 LastSize { get; private set; }
    public void SeedPlacement(float2 position, float2 size)
    {
        if (position.X > 0f || position.Y > 0f)
        {
            _initialPosition = position;
            LastPosition = position;
        }

        if (size.X > 0f && size.Y > 0f)
        {
            _initialSize = size;
            LastSize = size;
        }
    }
```

2. **Draw your content in `DrawContent`.** Use the window draw list and reserve space with `Dummy` so the window can auto-size and scroll:

```csharp
// Rendering/TelemetryWindow.cs L44-L77
public override void DrawContent(IViewport viewport)
{
    LastPosition = ImGui.GetWindowPos();
    LastSize = ImGui.GetWindowSize();

    if (!_host.TryGetFrame(out TelemetrySnapshot? snapshot, out OverlayConfig? config, out double dt)
        || snapshot is null || config is null)
    {
        ImGui.TextDisabled("Waiting for telemetry."u8);
        return;
    }

    if (!snapshot.HasVehicle)
    {
        ImGui.TextDisabled("No vehicle."u8);
        return;
    }

    // skip animations for the spawnable windows
    PanelContext context = new(
        ImGui.GetWindowDrawList(), snapshot, config, dt, IntroPhases.Complete,
        ImGui.GetWindowPos(), ImGui.GetWindowSize());

    float2 size = _panel.Measure(in context) * config.Scale;
    if (size.X <= 0f || size.Y <= 0f)
    {
        return;
    }

    float2 origin = ImGui.GetCursorScreenPos();
    _panel.Draw(in context, origin, size);

    ImGui.Dummy(in size);
}
```

3. **Build the windows on the first UI frame**, seed placement from saved config, and restore visibility:

```csharp
// Rendering/WindowHost.cs L67-L90
public void EnsureBuilt(OverlayConfig config)
{
    if (_built)
    {
        return;
    }

    _built = true;

    for (int i = 0; i < _catalogue.Length; i++)
    {
        (string id, string title, float2 size) = _catalogue[i];

        TelemetryWindow window = new(CreatePanel(id), title, size, this);

        if (FindState(config, id) is { } state)
        {
            window.SeedPlacement(new float2(state.X, state.Y), new float2(state.Width, state.Height));
            window.SetShown(state.Open);
        }

        _windows.Add(window);
    }
}
```

4. **Capture placement and debounce the write.** Read `GetWindowPos`/`GetWindowSize` inside `DrawContent` (step 1 stores them as `LastPosition`/`LastSize`), then each frame copy them into the config when they move by more than half a pixel and mark the config dirty; the config store flushes after a quiet second:

```csharp
// Rendering/WindowHost.cs L138-L141,L154-L184
public bool CaptureInto(OverlayConfig config)
{
    const float epsilon = 0.5f;
    bool changed = false;
// ...
    for (int i = 0; i < _windows.Count; i++)
    {
        TelemetryWindow window = _windows[i];
        OverlayWindowState state = _states[i];

        if (state.Open != window.IsShown)
        {
            state.Open = window.IsShown;
            changed = true;
        }
        
        if (window.LastSize.X <= 0f || window.LastSize.Y <= 0f)
        {
            continue;
        }

        if (MathF.Abs(state.X - window.LastPosition.X) > epsilon
            || MathF.Abs(state.Y - window.LastPosition.Y) > epsilon
            || MathF.Abs(state.Width - window.LastSize.X) > epsilon
            || MathF.Abs(state.Height - window.LastSize.Y) > epsilon)
        {
            state.X = window.LastPosition.X;
            state.Y = window.LastPosition.Y;
            state.Width = window.LastSize.X;
            state.Height = window.LastSize.Y;
            changed = true;
        }
    }

    return changed;
}
```

```csharp
// Config/ConfigStore.cs L139-L159
public static void MarkDirty()
{
    _dirty = true;
    _sinceChange = 0.0;
}

public static void Flush(double dt)
{
    if (!_dirty)
    {
        return;
    }

    _sinceChange += dt;
    if (_sinceChange < SaveIntervalSeconds)
    {
        return;
    }

    SaveNow();
}
```

5. **Offer the windows in your settings UI** with a checkbox per window calling `SetShown`, and clear per-panel smoothing when the vehicle changes (`ResetPanels`).

## Complete example

| File | Role |
|---|---|
| [Rendering/TelemetryWindow.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TelemetryWindow.cs) | `ImGuiWindow` subclass drawing a panel |
| [Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs) | Catalogue, lazy construction, placement capture |
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L139-L159) | `MarkDirty` and debounced `Flush` |

## Variations and gotchas

- **Shared id.** Use one subclass per distinct window, or do not rely on the base title, if more than one instance is open at once.
- **Protected-field seeding** depends on `_initialPosition` / `_initialSize` keeping their names and `ImGuiCond.Once` semantics.
- **First frame.** The windows are created in the first AfterGui call and can only appear from the next frame's static-window pass.
- **Shared mutable snapshot.** The hand-off is a reference, safe only because sampling and both draws run on the main thread at different points of the frame.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of subclass-the-stock-imguiwindow](overview.md)
- [Approach 1: lazily created window drawn from StarMapAfterGui](subclass-the-stock-imguiwindow-1.md)
- [Overlay on the foreground draw list](../../overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-3.md)
- [Persist ImGui window state and layouts](../../../persistence/user-data/persist-imgui-window-state/overview.md)
- Mod feature: [Panels as static ImGui windows](../../../../mods/ksa-public-telemetry/features/panels-as-static-imgui-windows.md)

[^ksa-istaticwindow]: KSA.IStaticWindow (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
