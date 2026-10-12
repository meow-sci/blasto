---
type: Mod Feature
title: Every element as its own ImGui window
description: Wraps each overlay panel in a subclass of the game's KSA.ImGuiWindow implementing IStaticWindow, so it is drawn by the game's own static-window pass like stock windows, and persists open state, position and size in the mod config because those windows opt out of imgui.ini
mod: ksa-public-telemetry
category: ui
techniques: [imgui-window, imgui-drawlist, json-config]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.ImGuiWindow, KSA.IStaticWindow, KSA.Program, Brutal.ImGuiApi.ImGui]
tags: [imgui, windows, istaticwindow, layout, persistence]
---

Besides the fixed bottom bar, every element (each readout kind, the engine cluster, the mission clock,
the timeline) can be opened as a normal movable, resizable window, for example to put a big altitude
gauge in a corner of a recording.[^kpt-readme] The windows look and behave like the game's own because
they are the game's own window class.

## How it works

1. **Subclass the game's window.** `TelemetryWindow : ImGuiWindow, IStaticWindow` calls the protected
   base constructor `(initialSize, lockAspectRatio: false, show: false)`, sets the title with
   `SetWindowTitle`, and overrides `DrawContent(IViewport)` ([Rendering/TelemetryWindow.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TelemetryWindow.cs#L8-L80)).
   The base constructor adds any `IStaticWindow` to a static list, and
   `ImGuiWindow.DrawAllStaticWindows` draws every shown entry each frame
   ([current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L99-L138)).[^ksa-imguiwindow] No `Begin`/`End` code in the mod: the base
   `OnDrawUi` handles flags, menu bar, position and size.
2. **Reuse the panels.** `DrawContent` builds a `PanelContext` on `ImGui.GetWindowDrawList()` with
   `IntroPhases.Complete` (no animation), measures the panel, draws it at `GetCursorScreenPos()` and then
   reserves the space with `ImGui.Dummy(size)` so the window can auto-size and scroll
   ([Rendering/TelemetryWindow.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TelemetryWindow.cs#L44-L77)). Each window gets its own panel instance, so smoothing
   state is independent of the bar.
3. **Catalogue.** `WindowHost` creates one window per `ReadoutKind` plus engine cluster, mission clock and
   timeline, with default sizes ([Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs#L7-L65)). Settings list them under
   "Windows" with checkboxes that call `SetShown` ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L440-L467)).
4. **Lazy construction.** The base constructor calls `ImGui.GetMainViewport()` and `ImGui.GetStyle()`,
   so the windows are built on the first UI frame (`EnsureBuilt` from `DrawEverything`), not at startup
   ([Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs#L67-L90), [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L109-L110)).
5. **Own persistence.** The base `OnDrawUi` uses `ImGuiWindowFlags.NoSavedSettings` and applies
   `_initialPosition`/`_initialSize` with `ImGuiCond.Once` ([current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L188-L207)), so ImGui
   never remembers where these windows were. The mod records `GetWindowPos`/`GetWindowSize` inside
   `DrawContent`, copies them into `OverlayConfig.Windows` when they move by more than half a pixel
   (`WindowHost.CaptureInto`, marking the config dirty), and on the next start seeds the protected
   `_initialPosition`/`_initialSize` fields before the first draw
   ([Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs#L138-L184), [Rendering/TelemetryWindow.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TelemetryWindow.cs#L29-L42)).
6. **Vehicle change.** When the snapshot's vehicle name changes, `ResetPanels` clears per-panel smoothing
   so numbers do not glide from the previous vehicle's values ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L112-L116)).

## Lifecycle and timing

- **Drawn in `frame:draw-ui`, by the game.** `DrawAllStaticWindows` runs in `Program.OnDrawUiFrame`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3140)), which is before StarMap's `[StarMapAfterGui]` postfix. The windows
  read what `WindowHost.Update` stored during the previous frame's overlay draw
  ([Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs#L92-L107)).[^ksa-program] That is a reference to the mod's single
  `TelemetrySnapshot` object, which `[StarMapAfterOnFrame]` refilled at the end of the previous frame, so
  the windows show the same values as the bar drawn later in the same frame; only the stored `dt` is
  the previous UI frame's.
- **Hidden with the UI.** `OnDrawUiFrame` is inside `if (DrawUI)`, so these windows disappear on F2
  even when the bar stays (see [Keep drawing while the game UI is hidden](draw-while-ui-hidden-drawfps-postfix.md)).
- **Capture after drawing.** `CaptureInto` runs in the StarMap hook after the windows drew this frame,
  so the stored position is current.

## Key code

| Path | Role |
|---|---|
| [Rendering/TelemetryWindow.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/TelemetryWindow.cs#L8-L80) | `ImGuiWindow` subclass drawing a panel |
| [Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs#L22-L90) | Catalogue and lazy construction with saved placement |
| [Rendering/WindowHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/WindowHost.cs#L138-L184) | Capture position and size into the config |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGuiWindow` constructor, static-window registration, `DrawAllStaticWindows` | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L99-L138) | Window lifetime and drawing |
| `ImGuiWindow.SetWindowTitle`, `SetShown`, `IsShown`, `_initialPosition`, `_initialSize` | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L39-L41), [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L75-L87), [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L140-L144) | Title, visibility, seeded placement |
| `ImGuiWindow.OnDrawUi` flags and `SetNextWindowPos`/`Size` | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L181-L207) | `NoSavedSettings`, `ImGuiCond.Once` |
| `Program.OnDrawUiFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3140) | When static windows draw |

## Pitfalls and version notes

- **Shared `###` id (inferred from the decomp, not tested in game).** `ImGuiWindow` builds the ImGui id
  from a debug name that is the window's full type name plus an underscore, with no per-instance part
  ([current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L89-L97), [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L140-L144)). Every `TelemetryWindow`
  therefore gets `"<title>###KSATelemetryOverlay.Rendering.TelemetryWindow_"`, and ImGui would merge two
  open ones into one window. A copy should use one subclass per window or not rely on the base title.
- **Shared mutable snapshot.** The hand-off is a reference, not a copy, which is what keeps the windows
  in step with the bar; it is safe only because sampling and both draws run on the main thread at
  different points of the frame. A copy that samples on another thread would need a real copy.
- **First frame.** The windows are created in the first `[StarMapAfterGui]` call and can only appear
  from the next frame's static-window pass, and only once `TryGetFrame` has data.
- **Protected-field seeding** depends on `_initialPosition`/`_initialSize` keeping their names and
  `ImGuiCond.Once` semantics.

## See also

- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [JSON config in the KSA documents folder](json-config-in-documents.md)
- [ModMenu entry with a standalone fallback window](modmenu-entry-with-standalone-fallback.md)
- [Stock window base class: IStaticWindow auto-registration with a mod-owned layout persisted to config](../../../howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-3.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
