---
type: Mod Feature
title: ModMenu entry that toggles the window
description: A static method tagged [ModMenuEntry("Compendium Window")] from the bundled ModMenu.Attributes.dll draws a MenuItem that flips the window flag, giving ModMenu users a menu-bar toggle while the mod itself needs only the small attributes assembly
mod: compendium
category: ui
techniques: [imgui-menu, assembly-load-context]
hooks: [modmenu ModMenuEntry]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [modmenu, menu, imgui, attributes, assembly-load-context]
---

With the ModMenu mod installed, the game's menu bar gets a "Compendium Window" entry that opens or
closes the window; the new state is saved to `settings.ini` on the next frame. The integration is one
attribute on one method and a 5 KB DLL shipped next to Compendium's.[^cmp-compendiummain]

## How it works

1. **The attribute.** `ModMenu.Attributes.dll` (decompiled with ilspycmd from the copy in the
   repository) is a .NET Standard 2.0 library, version 1.0.1 by MrJeranimo, with repository URL
   `https://github.com/MrJeranimo/ModMenu`. It contains one type,
   `ModMenu.ModMenuEntryAttribute(string menuName)`, valid on methods, exposing `MenuName`.
2. **The entry.** `[ModMenuEntry("Compendium Window")]` decorates
   `public static void DrawSubMenuEntry()`, which draws `ImGui.MenuItem("Compendium Window")` and flips
   the static window flag when it is clicked ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L45-L54)).
3. **Build reference.** The csproj references the DLL by `HintPath`, with the NuGet reference commented
   out because the package was not available, and copies the DLL into the mod folder after each build
   ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L70-L72), [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L98-L100),
   [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L37)).[^cmp-compendium-csproj]
4. **Runtime resolution.** StarMap's per-mod `ModAssemblyLoadContext.Load` first returns an assembly of
   the same name that is already loaded in the default context or the game's context, then tries mod
   dependencies, and only then resolves the file through the mod's own dependency resolver
   ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L73)).[^starmap-modassemblyloadcontext]
   Whether Compendium ends up sharing ModMenu's copy of the attribute type therefore depends on how
   ModMenu loads it.
5. **Discovery and call.** How ModMenu finds tagged methods and when it calls them is defined in ModMenu's
   source, which is not among the analyzed repositories, so it is unverified here.

## Lifecycle and timing

- **`frame:draw-ui` (by inference).** The method only issues ImGui calls, so ModMenu must call it while
  an ImGui frame is open, presumably while it draws its menu. The game draws its menu bar in
  `Program.DrawMenuBar`, which `Program.OnDrawUiThreadSafe` calls when the hovered viewport is the main
  one, after `[StarMapAfterGui]` in the same frame ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3189)).[^ksa-program]
  ModMenu's exact call site is unverified.
- **Effect.** Flipping the flag takes effect when the Compendium hook next runs; the change is saved by
  the per-frame comparison described in [settings.ini persistence](settings-ini-persistence.md).

## Key code

| Path | Role |
|---|---|
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L45-L54) | The tagged menu method |
| [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L70-L100) | Commented NuGet reference and `HintPath` reference |
| [src/ModMenu.Attributes.dll](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/ModMenu.Attributes.dll) | Bundled attribute assembly |

## Snippets

```csharp
// src/CompendiumMain.cs L45-L54
[ModMenuEntry("Compendium Window")]

// Makes the menu entry to open/close the compendium window using ModMenu.Attributes.dll
public static void DrawSubMenuEntry()
{
    if (ImGui.MenuItem("Compendium Window"))
    { CompendiumWindow = !CompendiumWindow; 
    Console.WriteLine("Toggled Compendium ModMenu Window variable");
    }
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGui.MenuItem` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7442) | Menu entry |

## Pitfalls and version notes

- **No fallback menu.** Without ModMenu nothing calls the method; a player who closes the window with its
  X has no in-game way to reopen it except setting `ShowWindow=True` in `settings.ini`. The gatOS
  feature linked above shows a fallback through the game's empty `Program.DrawProgramMenusHook`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4018)).
- **Binary in the repository.** The DLL is checked in rather than restored from a package feed, so
  updating it is manual.

## See also

- [settings.ini persistence](settings-ini-persistence.md)
- [Celestial compendium window](celestial-info-window.md)
- [ModMenu entry with a fallback menu (gatOS)](../../gatos/features/modmenu-entry-with-fallback-menu.md)
- [Reference ModMenu.Attributes, tag a static method and ship the DLL beside your mod](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-1.md) - how-to article demonstrating this feature

[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-compendium-csproj]: Compendium: src/Compendium.csproj
[^starmap-modassemblyloadcontext]: StarMap: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
