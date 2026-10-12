---
type: How-To
title: Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL
description: Declare your own ModMenuEntryAttribute with the same type name and constructor shape, tag a static method with it and guard the method because ModMenu calls it from inside its own menu build
category: ui
group: menus
topic: list-a-mod-in-modmenu
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback
  - mods/ksacars/features/car-panel-and-modmenu
  - mods/ksagolf/features/developer-panel-and-menu-bar
  - mods/ksarmory/features/operator-panel-imgui
  - mods/ksastructures/features/settings-menu
  - mods/realatmospheres/features/modmenu-page-and-toml-settings
techniques: [inter-mod-api, imgui-menu]
hooks: [modmenu ModMenuEntry, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [modmenu, modmenuentry, attribute, copy, no-dependency, type-name-match]
---
Six reference mods declare their own copy of `ModMenuEntryAttribute` instead of referencing `ModMenu.Attributes`: KSACars, KSAGolf, KSArmory, KSAStructures, KSA-Public-Telemetry and RealAtmospheres. A copy is about eighteen lines, needs no NuGet feed, ships no extra DLL and is inert when ModMenu is not installed. It works if (and only if) ModMenu matches the attribute by type name rather than by assembly identity, which is what each of these mods' comments state. Prerequisite: a StarMap mod. Demonstrated by [Car panel and ModMenu](../../../../mods/ksacars/features/car-panel-and-modmenu.md), [Telemetry ModMenu entry with a standalone fallback](../../../../mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback.md), [KSAGolf developer panel](../../../../mods/ksagolf/features/developer-panel-and-menu-bar.md), [KSArmory operator panel](../../../../mods/ksarmory/features/operator-panel-imgui.md), [KSAStructures settings menu](../../../../mods/ksastructures/features/settings-menu.md) and [Real Atmospheres' ModMenu page](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md).

## Background: how the game does it

ModMenu is not part of the game, so what follows is **mod-reported behaviour, unverified against ModMenu's source** (not in the corpus). The six mods say, in their code comments:

- ModMenu "scans every loaded assembly and matches the attribute by its type's name alone" (KSACars, KSAStructures; KSArmory and KSAGolf say `GetType().Name`), "never by assembly identity". Real Atmospheres says the same: "ModMenu finds the page by its attribute's name".
- ModMenu "resolves an instance only for a couple of hardcoded method names", so a tagged method should be `static`.
- ModMenu's own README or source is said to ask mods to copy the attribute rather than reference anything (KSArmory: "Its own README says to copy it"; Real Atmospheres: "its own source asks a mod to copy it").
- ModMenu calls the method while its menu is open, from inside its own menu build, and may scan before your UI exists.

KSAGolf and KSArmory additionally say ModMenu reaches the game's menu bar by transpiling `Program.DrawMenuBar`. That is a comment, not something checked here. The game's menu bar is one window drawn by `Program.DrawMenuBar` with an empty public hook, `DrawProgramMenusHook`, called inside it, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020).[^ksa-ksa-program] How your tagged method is then drawn is described in [Add a menu to the game's top menu bar](../add-items-to-the-game-menu-bar/overview.md). The menu calls themselves are `ImGui.BeginMenu` and `ImGui.MenuItem` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7429-L7465)).[^ksa-brutal-imguiapi-imgui]

## Lifecycle and timing

- **`frame:draw-ui`** at ModMenu's draw point (unverified). Do not assume the UI object you want to drive exists yet: KSAGolf and KSAStructures say ModMenu "may scan before" the first draw, so their static entry reads a nullable `Current` that the real draw sets.
- **Your window** is drawn from `[StarMapAfterGui]` (KSACars: `OnAfterGui` runs the frame's car step, draws the panel, then the mover) so the item you flip in the menu is seen on the next frame. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
- Your window draws from a StarMap hook, so it stops with the rest of the HUD while the HUD is hidden; whether ModMenu's own menu is reachable then is unverified.

## Steps

1. **Copy the attribute.** Same type name, same constructor shape as the shipped one (`menuName`, optional `isModMenuActivePropertyName`). KSACars' copy, identical in KSAGolf, KSAStructures and (with different doc comments) KSArmory:

```csharp
// src/KSACars/Ksa/ModMenuEntry.cs L1-L18
namespace KSACars;

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

   The namespace may be your own: KSA-Public-Telemetry declares a `public` copy in `KSATelemetryOverlay`, and Real Atmospheres an `internal` copy in `RealAtmospheres` with an explicit constructor.

2. **Tag a static method and make it fail-safe.** It runs inside ModMenu's menu build, so anything it throws is ModMenu's failure and not in your log. KSACars catches, logs once and returns:

```csharp
// src/KSACars/Ksa/CarPanel.cs L25-L44
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

   `ImGui.MenuItem(label, default, ref visible, true)` is the `ref bool` overload at `Brutal.ImGuiApi.ImGui.MenuItem(ImString, ImString, ref bool, bool)`; it draws a ticked item and writes the new state back.

3. **If the real UI is an instance, bridge with a static.** KSAGolf keeps a `Current` instance pointer set at the top of `Draw()` and has the static entry call through it. The null check is what happens when ModMenu scans before the first frame:

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L56-L86, L88-L90
private void DrawMenuContents()
{
    bool visible = Visible;
    if (ImGui.MenuItem("Panel", default, ref visible, true)) Visible = visible;
}

// Said once rather than per frame: this runs inside a menu build, so a failure repeats while it is open.
private static bool _warnedModMenu;

/// <summary>
/// Called by <b>ModMenu</b>, if the player has it, to fill this mod's entry in its shared menu.
/// Static because ModMenu resolves an instance only for a couple of hardcoded method names.
/// </summary>
[ModMenuEntry("KSAGolf")]
public static void DrawModMenu()
{
    try
    {
        Current?.DrawMenuContents();
    }
    catch (Exception e)
    {
        if (_warnedModMenu) return;
        _warnedModMenu = true;
        Log.Warn($"ModMenu entry failed, so its Panel item will not work: {e.Message}. "
                 + "Reopen from the floating KSAGolf button instead.");
    }
}

/// <summary>The panel ModMenu should drive. Null until the first draw, which ModMenu may scan before.</summary>
internal static Ui? Current { get; private set; }
// ...
public void Draw()
{
    Current = this;
// ...
```

4. **Optionally name an "active" property.** The second argument names a static `bool` property that ModMenu is said to set while it owns the entry; KSA-Public-Telemetry reads it to decide whether to show an "Install ModMenu" hint in its fallback window (see [the fallback pattern](../add-a-menu-with-modmenu-fallback/overview.md)):

```csharp
// Rendering/SettingsUi.cs L54-L64
    [ModMenuEntry("Telemetry Overlay", nameof(ModMenuActive))]
    public static void DrawSettingsMenu()
    {
        if (_config is null)
        {
            ImGui.TextDisabled("Overlay is still starting up."u8);
            return;
        }

        DrawContent(_config, inMenu: true);
    }
```

   That feature is documented from the 1.0.2 attribute assembly (unverified at run time). The mods that do not use it simply omit the argument.

5. **Draw your window as usual** from `[StarMapAfterGui]` or `[StarMapBeforeGui]` and let the menu only flip the flag the window reads.

6. **Test both ways.** With ModMenu installed the entry appears under its shared menu; without it the method is never called and the game logs nothing. Make sure the player has a way to open the window when ModMenu is absent.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/ModMenuEntry.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/ModMenuEntry.cs#L1-L18) | The attribute copy |
| [src/KSACars/Ksa/CarPanel.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CarPanel.cs#L25-L44) | Static entry with logged-once failure |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L56-L90) | Static entry bridging to an instance (`Current`) |
| [src/KSArmory/Ksa/Ui/ModMenuEntry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/ModMenuEntry.cs#L1-L20) and [src/KSAStructures/Ksa/Ui/ModMenuEntry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/ModMenuEntry.cs#L1-L18) | The same copy in two more mods |
| [ModMenuEntry.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/ModMenuEntry.cs#L1-L14) | `public` copy in its own namespace |
| [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L37-L49) | `internal` copy with an explicit constructor |

## Variations and gotchas

- **Keep the type name and constructor identical.** `ModMenuEntryAttribute`, with `(string menuName, string? isModMenuActivePropertyName = null)`. Per the mods, ModMenu compares names, so renaming it (even in your own namespace) would hide the entry. This is unverified.
- **Several mods, same type name.** Each mod's copy lives in its own load context; if ModMenu matches by name, all of them are found. That is exactly what these six mods rely on.
- **Presence checks are separate.** KSAGolf, KSArmory and KSAStructures also ship a `ModMenuPresence.Installed` that scans loaded assemblies for one named `ModMenu`, so their own bar entry is suppressed when ModMenu is present. Their own bar entry uses `BeginMainMenuBar`, which is not KSA's bar in this build; see [Add a menu to the game's top menu bar, approach 2](../add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-2.md). The game-side presence probe used by newer mods is `ModLibrary.Find("ModMenu")` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187-L190))[^ksa-ksa-modlibrary].
- **KSAGolf's and KSArmory's own notes say to delete the copy "at the first opportunity".** They call it a workaround for a missing engine feature (adding to the menu bar). Build 2026.10.10.5554 does have such a hook, the empty `Program.DrawProgramMenusHook`, and purrTTY, gatOS and Kitten Engineer Redux draw their fallback menus through it.
- **Do not open windows from the entry.** `ImGui.Begin` inside a menu build is not what ModMenu is documented to expect (unverified); flip a flag and draw elsewhere.

## See also

- [Overview](overview.md)
- [Approach 1: reference ModMenu.Attributes and ship it](list-a-mod-in-modmenu-1.md)
- [Approach 4: DynamicMethod wrappers](list-a-mod-in-modmenu-4.md)
- [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md)
- [Add a menu to the game's top menu bar](../add-items-to-the-game-menu-bar/overview.md)
- Mod features: [KSACars](../../../../mods/ksacars/features/car-panel-and-modmenu.md), [KSAGolf](../../../../mods/ksagolf/features/developer-panel-and-menu-bar.md), [KSArmory](../../../../mods/ksarmory/features/operator-panel-imgui.md), [KSAStructures](../../../../mods/ksastructures/features/settings-menu.md), [Telemetry](../../../../mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback.md), [Real Atmospheres](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md)

[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
