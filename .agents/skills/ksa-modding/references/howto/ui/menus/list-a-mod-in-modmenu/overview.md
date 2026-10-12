---
type: Game Feature
title: List a mod in ModMenu with the ModMenuEntry attribute
description: How a KSA mod gets an entry in the ModMenu community mod by tagging a static method with ModMenuEntry, and the four ways mods obtain or avoid the attribute type
category: ui
group: menus
topic: list-a-mod-in-modmenu
demonstrated_by:
  - mods/compendium/features/modmenu-window-toggle
  - mods/ohmystars/features/modmenu-entry-and-console-style-windows
  - mods/mpfx/features/effect-settings-window
  - mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes
  - mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback
  - mods/ksacars/features/car-panel-and-modmenu
  - mods/ksagolf/features/developer-panel-and-menu-bar
  - mods/ksarmory/features/operator-panel-imgui
  - mods/ksastructures/features/settings-menu
  - mods/realatmospheres/features/modmenu-page-and-toml-settings
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImString, KSA.Constants, KSA.ModLibrary]
tags: [modmenu, modmenuentry, menu, attribute, imgui-menu, dynamicmethod, assembly-load-context]
---
ModMenu is a separate community mod (by MrJeranimo) that gives every other mod a submenu under one shared menu on the game's menu bar. A mod opts in by putting `[ModMenuEntry("<label>")]` on a public or internal static method that draws ImGui menu items or a short settings page. Ten reference mods do this, and they differ only in where the attribute type comes from: the shipped `ModMenu.Attributes` assembly, a vendored copy of that DLL, a source copy of the attribute, or no compile-time reference to the game at all.

**What is verified and what is not.** ModMenu's own source is not in this corpus, and neither the decompiled game nor StarMap mentions it. Everything below about how ModMenu discovers and calls a tagged method is attributed to comments in the mods that use it (for example [KSACars](../../../../mods/ksacars/features/car-panel-and-modmenu.md) and [KSAGolf](../../../../mods/ksagolf/features/developer-panel-and-menu-bar.md)) and is marked *unverified*. The mod-side half, the attribute declaration, the method shape and how the DLL reaches the CLR, is checked against the mod sources and StarMap.

## Engine background

ModMenu is not part of the game, so the engine side is small: the menu calls the tagged method makes, and the loader rules that decide whether the attribute type resolves.

| Piece | Where | Why it matters here |
|---|---|---|
| `ImGui.MenuItem`, `ImGui.BeginMenu` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7429-L7465)[^ksa-brutal-imguiapi-imgui] | What an entry method calls. `MenuItem` has a `selected` overload and a `ref bool pSelected` overload |
| `ImString` | [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L14)[^ksa-brutal-imguiapi-imstring] | A `ref struct`, with an implicit conversion from `string`; reflection cannot box it (approach 4) |
| `ModLibrary.Find(string)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187-L190)[^ksa-ksa-modlibrary] | Looks a loaded mod up by id; used by the fallback pattern, not by the entry itself |
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)[^ksa-ksa-constants] | `Documents/My Games/Kitten Space Agency`, where a ModMenu page's settings file can live (approach 4) |
| `ModAssemblyLoadContext.Load` | [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L73)[^starmap-starmap-core-modrepository-modassemblyloadcontext] | Resolution order for the attribute assembly: default context, game context, declared dependencies, then the mod's own folder |

**The contract as the mods describe it** (unverified, ModMenu source not available):

- ModMenu scans the loaded assemblies for methods carrying an attribute whose **type name** is `ModMenuEntryAttribute`; assembly identity is not compared. This is why a source copy works (approach 3).
- The method must be `static`; ModMenu resolves instances only for "a couple of hardcoded method names".
- ModMenu calls the method while it builds its menu, so the body should only issue menu calls (`MenuItem`, `BeginMenu`), never `ImGui.Begin`, and must not throw.
- The attribute takes a menu name and an optional second argument naming a static `bool` property ModMenu sets while the entry is active (present in `ModMenu.Attributes` 1.0.2; the 1.0.1 build has only the one-argument constructor).
- With ModMenu absent nothing calls the method and the attribute is inert metadata. Your window then needs another way to be opened: see [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Reference ModMenu.Attributes and ship it](list-a-mod-in-modmenu-1.md) | Compendium, OhMyStars, MPFX | `inter-mod-api` | `frame:draw-ui` | Simplest source; one extra DLL to ship and keep in step with ModMenu's version |
| 2 | [Vendored ModMenu.Attributes.dll resolved by StarMap's per-mod resolver](list-a-mod-in-modmenu-2.md) | KSA_AircraftHUD | `assembly-load-context` | `frame:draw-ui` | Same as 1 when the package feed does not help; explains how the DLL is found at run time |
| 3 | [Copy the attribute source into the mod](list-a-mod-in-modmenu-3.md) | KSA-Public-Telemetry, KSACars, KSAGolf, KSArmory, KSAStructures, RealAtmospheres | `inter-mod-api` | `frame:draw-ui` | No DLL and no dependency; relies on ModMenu matching by type name (mod claim) |
| 4 | [DynamicMethod wrappers for ImGui.MenuItem and SetItemTooltip](list-a-mod-in-modmenu-4.md) | RealAtmospheres | `reflection` | `frame:draw-ui` | Mod needs no game reference at all; emitted IL is the price |

## Choosing

- Starting a new mod that already references the game: approach 3 is the lightest (one 18-line file, nothing to ship). Approaches 1 and 2 are the documented way to follow ModMenu's own package, but two mods record that the package could not be restored from nuget.org (Compendium and OhMyStars comment it out), while four others restore `ModMenu.Attributes` 1.0.1 or 1.0.2 from NuGet without complaint, so the feed situation is unclear. Check before you rely on it.
- Building without game references (see [Build a mod without game references](../../../getting-started/build/build-a-mod-without-game-references/overview.md)): approach 4, because even `ImGui.MenuItem` cannot be compiled against.
- Whichever you choose, a mod whose only way to open a window is its ModMenu entry strands players who lack ModMenu. Pair it with [a fallback](../add-a-menu-with-modmenu-fallback/overview.md) or a hotkey.

## See also

- [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md)
- [Add a menu to the game's top menu bar](../add-items-to-the-game-menu-bar/overview.md)
- [Ship a third-party DLL with a mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imstring]: Brutal.ImGuiApi.ImString (decompiled, build 2026.10.10.5554)
[^ksa-ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^starmap-starmap-core-modrepository-modassemblyloadcontext]: StarMap: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
