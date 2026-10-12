---
type: Mod Feature
title: ModMenu entry with a standalone menu fallback
description: Registers a "Kitten Engineer Redux" menu with ModMenu through the attribute's active-flag property and, when ModMenu does not claim it, draws the same toggles itself from a postfix on the game's empty Program.DrawProgramMenusHook
mod: kitten-engineer-redux
category: ui
techniques: [harmony-postfix, imgui-menu, inter-mod-api]
hooks: [harmony-postfix KSA.Program.DrawProgramMenusHook, modmenu ModMenuEntry]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.IGameViewportLifecycle, KSA.GameViewport]
tags: [modmenu, menu-bar, imgui, harmony, postfix, optional-dependency, toggles]
---

KER puts two checkable items, "Flight HUD" and "Editor Panel", in a "Kitten Engineer Redux" menu.
With the ModMenu mod installed the menu lives inside ModMenu; without it, KER adds its own top-level
menu to the game's menu bar. Both paths call the same `MenuContent.DrawToggles()`, which flips
`FlightHud.Visible` and `EditorPanel.Visible` (both persisted).

## How it works

- **ModMenu path.** `ModMenuIntegration.DrawMenu` carries
  `[ModMenuEntry("Kitten Engineer Redux", nameof(IsHandledByModMenu))]` from the `ModMenu.Attributes`
  NuGet package 1.0.2 ([ModMenuIntegration.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ModMenuIntegration.cs)).[^ker-modmenuintegration]
  The second argument names a public static `bool` property on the same class. In the 1.0.2
  attribute (the copy shipped in the release zip) it is the optional `isModMenuActivePropertyName`
  constructor parameter, stored as `IsModMenuActivePropertyName`, and the assembly description reads
  "Optionally provide a boolean property name to let ModMenu signal when your menu is active"; 1.0.1
  has only the one-argument constructor. KER never sets the property itself and relies on ModMenu
  setting it to true. ModMenu's runtime source is not among the analysed repositories, so when and how
  ModMenu writes the property is unverified.[^ker-dist]
- **Fallback path.** `Patch_StandaloneMenu` in [StandaloneMenu.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/StandaloneMenu.cs) is a
  `[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]` postfix, installed by the
  same `PatchAll` as everything else. It returns if `IsHandledByModMenu` is true; otherwise it opens
  `ImGui.BeginMenu("Kitten Engineer Redux")`, calls
  `((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true)`, draws the toggles and ends
  the menu.[^ker-standalonemenu] `DrawMenuBar` clears the flag every frame before it draws and, with
  auto-hide on, hides the bar on the next frame when the mouse is below it unless the flag is set
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3468-L3479)), so setting it while the menu is open keeps
  the bar visible while the player moves into the dropdown.
- **Why that target.** `Program.DrawProgramMenusHook()` is a public, empty method that
  `Program.DrawMenuBar` calls inside `ImGui.BeginMenuBar()` after the stock menus and before the
  version label ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020)), an
  intended extension point.[^ksa-program]
- **Shipping the attribute.** So that the attribute type can resolve when ModMenu (or anything else)
  reflects over KER's methods, the post-build step and the release zip both include
  `ModMenu.Attributes.dll` next to `KittenEngineerRedux.dll`
  ([KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L68-L83)).[^ker-csproj]

## Lifecycle and timing

- `Mod.OnFullyLoaded` (`[StarMapAllModsLoaded]`) installs the postfix along with the HUD patches.[^ker-mod]
- `Program.DrawMenuBar` runs from `Program.OnDrawUiThreadSafe` when the hovered viewport is the main
  one, and from `GameViewport.DrawImGui` for a hovered secondary viewport
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3186-L3189),
  [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257)). Both are
  `frame:draw-ui`. The game's menu bar is its own window (`ImGui.Begin("Menu Bar")` with the
  `MenuBar` flag) and the hook runs between its `BeginMenuBar` and `EndMenuBar`, so the postfix can
  call `BeginMenu` directly; a `BeginMainMenuBar` from elsewhere would open a different bar.[^ksa-gameviewport]
- When ModMenu owns the entry, ModMenu decides when `DrawMenu` runs.

## Key code

| Path | Role |
|---|---|
| [ModMenuIntegration.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ModMenuIntegration.cs) | ModMenu entry and the handled flag |
| [StandaloneMenu.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/StandaloneMenu.cs) | Fallback postfix on `Program.DrawProgramMenusHook` |
| [MenuContent.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MenuContent.cs) | Shared toggles |
| [Patch_StandaloneMenu](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_StandaloneMenu) | Stale copy without a `.cs` extension, not compiled (see pitfalls) |

## Snippets

```csharp
// ModMenuIntegration.cs L5-L14
public static class ModMenuIntegration
{
    public static bool IsHandledByModMenu { get; set; }

    [ModMenuEntry("Kitten Engineer Redux", nameof(IsHandledByModMenu))]
    public static void DrawMenu()
    {
        MenuContent.DrawToggles();
    }
}
```

```csharp
// StandaloneMenu.cs L7-L23
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_StandaloneMenu
{
    private static void Postfix()
    {
        if (ModMenuIntegration.IsHandledByModMenu)
            return;

        if (!ImGui.BeginMenu("Kitten Engineer Redux"u8))
            return;

        ((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true);
        MenuContent.DrawToggles();

        ImGui.EndMenu();
    }
}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.DrawProgramMenusHook()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018-L4020) | Empty hook; postfix target |
| `Program.DrawMenuBar(IGameViewport, int)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455) | Caller, inside `BeginMenuBar` |
| `IGameViewportLifecycle.SetMenuBarInUse(bool)` | [current/decomp/KSA/IGameViewportLifecycle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewportLifecycle.cs#L9), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L183-L191) | Keep an auto-hidden menu bar visible |
| `GameViewport.MenuBarInUse` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L28) | Read-only flag in current builds |

## Pitfalls and version notes

- The repository root also contains `Patch_StandaloneMenu` (no extension), an older version of the
  same class that assigns `Program.MainViewport.MenuBarInUse = true`
  ([Patch_StandaloneMenu](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_StandaloneMenu)).[^ker-patch-standalonemenu] In build 5554
  `MenuBarInUse` is a get-only property on `GameViewport` and `Program.MainViewport` is typed
  `IGameViewport`, so that code would not compile; it is excluded from the build only because the
  SDK compiles `*.cs` files. The compiled version goes through `IGameViewportLifecycle`, the same
  call the stock menus (File and the others) make inside `DrawMenuBar`.[^ksa-igvlifecycle]
- `DrawProgramMenusHook` runs for secondary viewport menu bars too, so the fallback menu appears
  there as well, and it marks the main viewport's menu bar in use rather than the one being drawn.
- If ModMenu is installed but never sets the property (for example an older ModMenu that ignores the
  second attribute argument), both menus appear. gatOS avoids this dependency by checking
  `ModLibrary.Find("ModMenu")` instead ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L192-L213)).[^gatos-modgame]

## See also

- [JSON settings with autosave](json-settings-autosave.md)
- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [Editor panel injected into the vehicle editor](editor-panel-injection.md)
- [gatOS: ModMenu entry with fallback menu](../../gatos/features/modmenu-entry-with-fallback-menu.md)
- [purrTTY: game menu with ModMenu fallback](../../purrtty/features/game-menu-with-modmenu-fallback.md)
- [Name an active-flag property in ModMenuEntry and skip your own menu when ModMenu sets it](../../../howto/ui/menus/add-a-menu-with-modmenu-fallback/add-a-menu-with-modmenu-fallback-2.md) - how-to article demonstrating this feature

[^ker-modmenuintegration]: ModMenuIntegration.cs
[^ker-standalonemenu]: StandaloneMenu.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ker-csproj]: KittenEngineerRedux.csproj
[^ker-mod]: Mod.cs
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ker-patch-standalonemenu]: Patch_StandaloneMenu
[^ksa-igvlifecycle]: KSA.IGameViewportLifecycle (decompiled, build 2026.10.10.5554)
[^gatos-modgame]: gatOS.GameMod/Game/Mod.Game.cs
[^ker-dist]: dist/KittenEngineerRedux.zip
