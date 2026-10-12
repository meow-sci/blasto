---
type: Mod Feature
title: ISubmod contract for features that run standalone or inside a supermod
description: A five-method ImGui panel interface plus static Apply and Remove Harmony patch classes lets each feature library be hosted either by its own StarMap mod or by one aggregating supermod without code changes
mod: unscience
category: getting-started
techniques: [starmap-lifecycle, imgui-window, harmony-prefix]
hooks:
  - starmap StarMapAllModsLoaded (host calls Initialize and the patch class Apply)
  - starmap StarMapBeforeGui (host calls Update)
  - starmap StarMapAfterGui (host calls RenderContent / RenderFloatingWindows)
  - starmap StarMapUnload (host calls Dispose and Remove)
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: []
tags: [architecture, starmap, imgui, harmony, supermod, project-layout]
---

unscience is about 30 independent features shipped as one mod. Each feature is written once, in a
`<name>.lib` class library, against a small host-agnostic contract, and is then hosted twice: by a
thin development-only StarMap mod in `<name>/` and by the shipped `unscience` supermod, which
renders every feature as a collapsible section of one window. The contract lives in
`ksa-abstractions.lib`.[^isubmod][^repository-index]

Mechanism in one line: a feature exposes an `ISubmod` (UI panel plus per-frame update) and a
static `<Feature>Patches.Apply(Harmony)` / `Remove(Harmony)` pair; any host owns the window, the
Harmony instance and the StarMap attributes, and calls those members at the matching lifecycle points.

## How it works

`ISubmod` members:

| Member | Host calls it | Contract |
|---|---|---|
| `Name`, `Tooltip` | When drawing headers/menus | Display strings |
| `Initialize()` | Once, from `[StarMapAllModsLoaded]` | Create lib state, subscribe to shared hooks |
| `Update(double dt)` | Every frame, even when the section is hidden or collapsed | Ticking, sampling, pruning stale targets |
| `RenderContent()` | Inside the host's window when the section is open | Draw ImGui content **without** `Begin`/`End` |
| `RenderFloatingWindows()` | Every frame, default no-op | Own secondary windows that must outlive a collapsed section |
| `Dispose()` | From `[StarMapUnload]` | Release resources and restore game state |

`SubmodUI.BeginContentArea(id)` / `EndContentArea()` wrap the content in an auto-height,
borderless child window with uniform padding so every feature looks the same inside the supermod's
collapsing headers.[^submod-ui]

Game integration is kept out of the interface. Each lib ships one or more static patch classes
(`GlassPatches`, `GraffitiPatches`, `PyroPatches`, ...) that take the caller's `Harmony` instance,
resolve their targets (`AccessTools.Method`, `nameof` where public, strings where private) and
patch manually; `Remove` unpatches exactly the methods it patched. Patching through the caller's
instance is what lets the supermod run every feature on a single `Harmony("MeowSci.Unscience")`
while a standalone host uses its own id.[^glass-patches]

Two hosts for one feature (Glass as the example):

| | Standalone dev host | Supermod |
|---|---|---|
| Entry | `glass/Mod.cs` `[StarMapMod]` | `unscience/Mod.cs` `[StarMapMod]` |
| Harmony | `new Harmony("MeowSci.Glass")`, `GlassPatches.Apply` + `HotkeyGuard.Patch` | `Patcher.TryApply("glass", () => GlassPatches.Apply(_harmony!))` among 30+ others |
| Window | Own `ImGui.Begin` toggled with F9 | Collapsing header in "Unscience Toolbox" (F11) |
| Update | `[StarMapBeforeGui]` -> `_submod.Update(dt)` | Same, plus hidden-HUD replay |

Only `unscience` is distributed; `Directory.Build.props` marks every other project non-publishable,
so the standalone hosts compile (keeping both integration paths honest) but are never deployed.[^glass-mod][^glass-patcher][^unscience-patcher]

## Lifecycle and timing

The interface itself has no timing; the host maps it onto StarMap's attributes, which StarMap
dispatches through Harmony patches on `Program`:[^starmap-program-patcher]

- `Initialize` and the patch classes' `Apply`: `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`),
  after all mod assemblies and asset bundles loaded, before any scene exists. Libraries therefore
  must not assume a vessel or celestial exists in `Initialize`.
- `Update(dt)`: `[StarMapBeforeGui]`, a prefix on `Program.OnDrawUiFrame` (`frame:draw-ui`). At this
  point the next physics step has already been dispatched, so `Update` is for UI-side state;
  vessel mutations go through [the PrepareFrame handoff](./lib-physics-frame-hook.md).
- `RenderContent` / `RenderFloatingWindows`: `[StarMapAfterGui]`, a postfix on
  `Program.OnDrawUiViewports`, inside the ImGui frame.
- `Dispose` and `Remove`: `[StarMapUnload]`, which StarMap invokes directly from `StarMapCore.DeInit`
  only after `Program.Main` has returned. By then `Program.Dispose` has destroyed the renderer and Vulkan
  device, terminated GLFW and released FMOD, and StarMap's id-less `UnpatchAll()` has removed every
  Harmony patch, so at game exit `Dispose` can only do managed cleanup and file writes; GPU and audio
  releases there act on torn-down systems.[^starmap-gamesurveyer][^starmap-core][^ksa-program]

The order inside `OnFullyLoaded` differs by host: the supermod initializes all sub-mods first and
then patches (so registries such as `IFeelSeenTracker` exist before patches capture them), while
the Glass host patches first.[^unscience-mod]

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/ISubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ISubmod.cs) | The interface |
| [ksa-abstractions.lib/SubmodUI.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SubmodUI.cs) | Shared padded content area |
| [glass.lib/GlassSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.cs#L9-L40) | A small `ISubmod` implementation |
| [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs#L18-L40) | Static Apply/Remove patch class |
| [glass/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass/Mod.cs) | Standalone host |
| [glass/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass/Patcher.cs) | Standalone Harmony owner |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L65-L148) | Supermod host building all sub-mods |

## Snippets

```csharp
// ksa-abstractions.lib/ISubmod.cs L7-L37
public interface ISubmod
{
    /// <summary>Display name shown in headers and menus.</summary>
    string Name { get; }

    /// <summary>Short description shown as a tooltip on the header in the host window.</summary>
    string Tooltip { get; }

    /// <summary>Called once during initialization to set up state and lib instances.</summary>
    void Initialize();

    /// <summary>Called every frame for pre-UI computation (sampling, ticking, etc.).</summary>
    void Update(double dt);

    /// <summary>
    /// Renders ImGui content. Caller is responsible for Begin/End window framing.
    /// Do NOT call ImGui.Begin/End for the main content area. Additional popup/child windows are fine.
    /// </summary>
    void RenderContent();

    /// <summary>
    /// Renders any floating/secondary ImGui windows (e.g. stand-alone editor windows).
    /// Called every frame regardless of whether the submod's section is collapsed, so
    /// windows opened by this submod remain consistent with their own visibility state.
    /// Default implementation is a no-op.
    /// </summary>
    void RenderFloatingWindows() { }

    /// <summary>Called during unload to clean up resources.</summary>
    void Dispose();
}
```

The standalone host is a few lines around the same calls the supermod makes:

```csharp
// glass/Mod.cs L22-L58
    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        try
        {
            _submod = new GlassSubmod();
            Patcher.Patch();
            _submod.Initialize();
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"glass: Error during initialization: {ex.Message}");
        }
    }

    [StarMapBeforeGui]
    public void OnBeforeUi(double dt)
    {
        if (!_isInitialized || _isDisposed) return;
        _submod.Update(dt);
    }

    [StarMapAfterGui]
    public void OnAfterUi(double dt)
    {
        try
        {
            if (!_isInitialized || _isDisposed) return;
            if (ImGui.IsKeyPressed(ImGuiKey.F9)) _windowVisible = !_windowVisible;
            if (_windowVisible) RenderWindow();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"glass: Error in OnAfterUi: {ex.Message}");
        }
    }
```

```csharp
// ksa-abstractions.lib/SubmodUI.cs L26-L42
    public static void BeginContentArea(string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(ContentPaddingX, ContentPaddingY));
        ImGui.BeginChild(id, new float2(0, 0),
            ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Closes the content area opened by <see cref="BeginContentArea"/>.
    /// </summary>
    public static void EndContentArea()
    {
        ImGui.Dummy(new float2(0, ContentExtraBottomPaddingY));
        ImGui.EndChild();
    }
```

## Game APIs touched

None directly: the contract is pure C# and ImGui (`Brutal.ImGuiApi`). The host side relies on
StarMap's `StarMapMod`, `StarMapAllModsLoaded`, `StarMapBeforeGui`, `StarMapAfterGui` and
`StarMapUnload` attributes. StarMap dispatches `StarMapAllModsLoaded` from a postfix on
`ModLibrary.LoadAll`, `StarMapBeforeGui` and `StarMapAfterGui` from a prefix on `Program.OnDrawUiFrame`
and a postfix on `Program.OnDrawUiViewports`, and `StarMapUnload` by direct invocation from
`StarMapCore.DeInit` after the game has exited.[^starmap-program-patcher][^starmap-gamesurveyer][^starmap-core]

## Pitfalls and version notes

- `RenderContent` must not call `ImGui.Begin`/`End` for its main area; the supermod already opened the
  window and a collapsing header.
- `Update` runs for hidden and collapsed sections too, so it must stay cheap.
- A feature that needs per-frame work while the HUD is hidden must not rely on `Update` alone in a
  standalone host: only the supermod registers the [hidden-HUD replay](./lib-hidden-ui-frame-hook.md).
- Patch classes resolve private targets by string; a failure throws from `Apply`. The supermod isolates
  each `Apply` in its own try/catch so one stale target does not disable the rest.
- Every host must apply the [hotkey guard](./lib-hotkey-guard.md) (repository rule).

## See also

- [Supermod host](./unscience-supermod-host.md)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Standalone mod template](./fixme-mod-name-standalone-mod-template.md)
- [Glass FOV override](./glass-fov-override.md) (the example feature)
- [Mod overview](../overview.md)
- [Scaffold a mod with mkmod.ts and grow it into a library hosted by a supermod](../../../howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-2.md) - how-to article demonstrating this feature
- [Consolidated patcher with TryApply and ordered shared seams](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-2.md) - how-to article demonstrating this feature

[^isubmod]: ksa-abstractions.lib/ISubmod.cs
[^repository-index]: REPOSITORY_INDEX.md
[^submod-ui]: ksa-abstractions.lib/SubmodUI.cs
[^glass-patches]: glass.lib/GlassPatches.cs
[^glass-mod]: glass/Mod.cs
[^glass-patcher]: glass/Patcher.cs
[^unscience-patcher]: unscience/Patcher.cs
[^starmap-program-patcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^unscience-mod]: unscience/Mod.cs
[^starmap-gamesurveyer]: StarMap/GameSurveyer.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
