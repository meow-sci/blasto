---
type: Mod Feature
title: Car panel and ModMenu entry
description: A closable ImGui panel drawn from the GUI hook only while a car is flown, built with flags that never take the keyboard so the flight keys keep driving, plus an optional ModMenu entry declared through a name-matched attribute copy
mod: ksacars
category: ui
techniques: [imgui-window, imgui-menu, starmap-lifecycle, inter-mod-api]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.Vehicle, Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiWindowFlags]
tags: [ui, imgui, panel, modmenu, soft-dependency, keyboard-capture]
---

While a car is the controlled vehicle, KSACars shows a small "Fast & Purrious" panel: speed and
gear, headlights off/low/high, scoop size and rock weight on the Eldorado, the craft mover switch
and an **Unflip** button. Closing it leaves a tiny **F&P** button in its place, and with the
community ModMenu mod installed the panel can also be toggled from **Mods > Fast & Purrious**.[^ksacars-readme]
The panel's one hard constraint is that it must never take the keyboard, because the keyboard is
the car's throttle and steering.[^ksacars-carpanel]

## How it works

- `CarPanel.Draw(mover)` returns immediately unless `KsaWorld.ControlledVehicle` is a car the mod
  is driving (`Buggies.Of(craft)`).
- Position: `SetNextWindowPos` at 16 px from the left of `ImGui.GetMainViewport().WorkPos`, halfway
  down `WorkSize`, with `ImGuiCond.FirstUseEver` so the player can move it.
- Flags: `AlwaysAutoResize | NoCollapse | NoFocusOnAppearing | NoNav`. `NoNav` (`NoNavInputs |
  NoNavFocus`) and `NoFocusOnAppearing` keep keyboard navigation off the window.[^ksa-imguiwindowflags]
- Closable: `ImGui.Begin("Fast & Purrious###Car", ref Visible, Flags)`; when `Visible` is false a
  separate untitled window `Fast & Purrious##reopen` draws one `SmallButton("F&P")`.
- Controls write mod state that later stages read: `car.Beam` (headlights) is read by the
  `PartTree.UpdateRenderData` postfix later in the same frame's `Render`; `car.Scoop` by
  `Buggies.Sync` in the next frame's car step; `Buggies.Right(craft)` (queued righting) by the next
  frame's `Vehicle.PrepareWorker` prefix, the first point where a pose write reaches the physics;
  `mover.Enabled` by the craft mover right after the panel.
- The rock-weight slider (`SliderFloat`, logarithmic, 0.01-100 %) is applied only on
  `IsItemDeactivatedAfterEdit()`, because each change makes KSA rebuild its rocks.

**ModMenu, without a dependency.** `ModMenuEntry.cs` declares an internal
`ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)`, a copy of
ModMenu's own attribute. According to the mod, ModMenu scans every loaded assembly and matches the
attribute by its type name alone, so `[ModMenuEntry("Fast & Purrious")]` on the static
`CarPanel.DrawModMenu()` is found when ModMenu is installed and inert otherwise.[^ksacars-modmenuentry]
`DrawModMenu` adds one `ImGui.MenuItem("Panel", ..., ref visible)` and catches its own exceptions,
since it runs inside ModMenu's menu build. ModMenu's source is not in this corpus, so the
name-matching claim is the mod's (unverified here); several other mods in the corpus (KSAGolf,
KSAStructures, KSA-Public-Telemetry, RealAtmospheres) carry the same attribute copy.

## Lifecycle and timing

Drawn from `[StarMapAfterGui]`, StarMap's postfix on `Program.OnDrawUiViewports`, inside the ImGui
frame (`frame:draw-ui`). `KSACarsMod.OnAfterGui` first runs the frame's car step, then draws the
panel, then updates and draws the craft mover, so a click on the panel's checkbox is consumed by
the panel and not by the world.[^ksacars-mod] The panel disappears with the rest of the UI when
`Program.DrawUI` is false (F2), because the GUI hook is not called then.[^ksa-program]

Why the keyboard matters: `Program.OnKey` drops key events entirely while
`ImGui.GetIO().WantCaptureKeyboard` is true, and `Vehicle.PrepareWorker` clears the flown craft's
held thruster and engine keys under the same condition. A focused, navigable window would freeze
the car mid-turn.[^ksa-vehicle][^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/CarPanel.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CarPanel.cs) | Panel, reopen button, ModMenu callback |
| [src/KSACars/Ksa/ModMenuEntry.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/ModMenuEntry.cs) | Attribute copy matched by name |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L87-L110) | GUI hook: step, panel, mover |

## Snippets

```csharp
// src/KSACars/Ksa/CarPanel.cs L13-L44
    // Neither takes the keyboard: KSA drops the flown craft's held keys while an ImGui window has it,
    // and those keys are the throttle and the steering.
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse
                                           | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    private static float? _rockWeight;

    /// <summary>Whether the panel is open. Closed, a small button that opens it is left in its place.</summary>
    public static bool Visible = true;

    private static bool _warnedModMenu;

    /// <summary>
    /// Called by ModMenu, if the player has it, to fill this mod's entry in its menu. Static, because
    /// that is what ModMenu can always call.
    /// </summary>
    [ModMenuEntry("Fast & Purrious")]
    public static void DrawModMenu()
    {
        // Inside ModMenu's own menu build, where anything thrown would be its failure and not in this log.
        try
        {
            bool visible = Visible;
            if (ImGui.MenuItem("Panel", default, ref visible, true)) Visible = visible;
        }
        catch (Exception e)
        {
            if (_warnedModMenu) return;
            _warnedModMenu = true;
            Log.Warn($"ModMenu entry failed, so its Panel item will not work: {e.Message}");
        }
    }
```

```csharp
// src/KSACars/Ksa/ModMenuEntry.cs L3-L18
/// <summary>
/// A copy of MrJeranimo's <c>ModMenuEntryAttribute</c>, so <b>ModMenu</b> lists this mod under its
/// shared <c>Mods</c> menu when a player has it installed.
///
/// <para><b>A copy on purpose, not a dependency.</b> ModMenu scans every loaded assembly and matches
/// the attribute by its type's name alone, so declaring it here is enough to be found, and it is inert
/// when ModMenu is absent.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)
    : Attribute
{
    public string MenuName { get; } = menuName;

    public string? IsModMenuActivePropertyName { get; } = isModMenuActivePropertyName;
}
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Brutal.ImGuiApi.ImGui` (`Begin`, `End`, `SetNextWindowPos`, `GetMainViewport`, `RadioButton`, `SliderFloat`, `Checkbox`, `Button`, `SmallButton`, `MenuItem`, `IsItemDeactivatedAfterEdit`) | The panel |
| `ImGuiWindowFlags.NoNav`, `NoFocusOnAppearing` ([current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs#L21-L29)) | Keep keyboard focus off the window |
| `Vehicle.PrepareWorker` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2400)) | Clears held input while ImGui wants the keyboard |
| `Program.OnKey` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754)) | Drops key events while ImGui wants the keyboard |

## Pitfalls and version notes

- **Do not let a mod window take the keyboard** while the player flies with keys. Avoid text inputs
  and navigation; a slider being dragged still captures input for the duration of the drag.
- **Apply expensive settings on release** (`IsItemDeactivatedAfterEdit`), not on every drag frame.
- **ModMenu's attribute is matched by name**: keep the namespace-free type name
  `ModMenuEntryAttribute` and the constructor shape identical to the original (per the mod's
  comment; not verified against ModMenu's source here).
- The panel only exists while the GUI hook runs; anything that must work with the UI hidden belongs
  in the frame hook.

## See also

- [Drive with the flight keys](./drive-with-flight-keys.md)
- [Craft mover](./craft-mover-mouse-pick-place.md)
- [Headlights as spotlights](./headlights-as-spotlights.md)
- [Scoop colliders and loose clutter](./scoop-colliders-and-loose-clutter.md)
- [Inject wheel forces from a Vehicle.PrepareWorker prefix](./prepareworker-impulse-injection.md) (Unflip)
- [Share input with the game: NoNav and NoFocusOnAppearing windows that never take the keyboard](../../../howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-3.md) - how-to article demonstrating this feature
- [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-3.md) - how-to article demonstrating this feature
- [Settings window: a menu entry, a small auto-sized window and wrapped tooltips](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-4.md) - how-to article demonstrating this feature

[^ksacars-carpanel]: src/KSACars/Ksa/CarPanel.cs
[^ksacars-modmenuentry]: src/KSACars/Ksa/ModMenuEntry.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-readme]: README.md
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-imguiwindowflags]: Brutal.ImGuiApi.ImGuiWindowFlags (decompiled)
