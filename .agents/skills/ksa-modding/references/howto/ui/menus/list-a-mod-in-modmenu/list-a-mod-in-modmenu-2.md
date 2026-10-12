---
type: How-To
title: Vendor ModMenu.Attributes.dll in your repository and let StarMap's per-mod resolver find it
description: Commit the attribute DLL, reference it by HintPath without Private=False, copy it to the output folder and understand how StarMap resolves it at run time
category: ui
group: menus
topic: list-a-mod-in-modmenu
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes]
techniques: [assembly-load-context, inter-mod-api]
hooks: [modmenu ModMenuEntry, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [modmenu, vendored-dll, hintpath, assembly-load-context, assemblydependencyresolver, copy-local]
---
This is approach 1 with the package feed taken out of the picture: the `ModMenu.Attributes.dll` file lives in your own repository, your project references it by file path, and the build copies it into the mod folder. KSA_AircraftHUD does exactly this for its "Aircraft HUD" entry (Enable/Disable HUD and Settings). The article also explains what StarMap does at run time with a private DLL that is not a declared dependency, which applies to any vendored library. Prerequisite: a StarMap mod; no Harmony needed for the entry itself. Demonstrated by [ModMenu entry with a vendored attributes DLL](../../../../mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes.md).

## Background: how the game does it

Neither KSA nor StarMap knows about ModMenu. What matters is how a mod's private DLL gets loaded. StarMap gives every mod its own `AssemblyLoadContext`. `ModAssemblyLoadContext.Load` runs in this order:[^starmap-starmap-core-modrepository-modassemblyloadcontext]

1. An assembly with the same simple name already loaded in `AssemblyLoadContext.Default` is returned.
2. Then one already loaded in the game's context.
3. Then the game context is asked to load it by name.
4. Then the mod's declared dependencies whose import list names the assembly.
5. Finally the mod's own `AssemblyDependencyResolver`, created from the entry assembly path, resolves the file next to the mod ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L15-L73)).

So AircraftHUD's shipped copy is used only if ModMenu has not already put an assembly called `ModMenu.Attributes` into the default or game context. Which of those happens depends on how ModMenu loads things and is **unverified** here (ModMenu's source is not in the corpus). The shared guide on [shipping third-party DLLs](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md) covers the same resolver for general libraries.

## Lifecycle and timing

- The attribute assembly is needed whenever something reflects over your methods' custom attributes (ModMenu, at `frame:draw-ui` time or earlier; unverified) or your own code touches `ModMenuEntryAttribute`. There is no startup step to write.
- The entry method itself only flips flags; AircraftHUD's settings window is drawn from `[StarMapBeforeGui]` (before the stock UI of a frame), so a "Settings" click in the menu takes effect the next time that hook runs (ModMenu's own draw point is unverified). The method that draws the options page:

```csharp
// AircraftHUDMain.cs L769-L776
        [StarMapBeforeGui]
        unsafe public void DrawHudMenu(double dt)
        {
            ImGuiWindowFlags menuFlags = ImGuiWindowFlags.MenuBar;
            // options page
            if (HUD.settingsPageOn)
            {
                ImGui.Begin("HUD Settings", ref HUD.settingsPageOn, menuFlags);
```

See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Obtain `ModMenu.Attributes.dll`.** From the ModMenu release or another mod (AircraftHUD's metadata identifies its copy as `ModMenu.Attributes` 1.0.2, .NET Standard 2.0, repository `https://github.com/MrJeranimo/ModMenu`). Commit it to the repository root; AircraftHUD keeps it beside `mod.toml`.

2. **Reference it by `HintPath`.** The props file lists the game assemblies with `Private="False"` (do not copy them) and then the attribute DLL **without** it, so MSBuild copies it to the output:

```xml
<!-- AircraftHUD.props L22-L26 -->
    <Reference Include="@(_KsaAssemblies->'%(FileName)')" HintPath="%(Path)" Private="False" />

    <Reference Include="ModMenu.Attributes">
      <HintPath>ModMenu.Attributes.dll</HintPath>
    </Reference>
```

3. **Copy it explicitly as well.** The project marks the file `CopyToOutputDirectory=Always`, which also covers the case where the output folder is not the build folder:

```xml
<!-- AircraftHUD.csproj L27-L29 -->
    <None Update="ModMenu.Attributes.dll">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
```

   AircraftHUD builds straight into `Documents/My Games/Kitten Space Agency/mods/AircraftHUD/` (`OutputPath` in the props file), so the DLL lands exactly where StarMap looks for the mod.

4. **Write the entry.** A public static method on the `[StarMapMod]` class:

```csharp
// AircraftHUDMain.cs L153-L165
        [ModMenuEntry("Aircraft HUD")]
        public static void CreateModMenu()
        {
            if (ImGui.MenuItem(HUD.enabled ? "Disable HUD" : "Enable HUD"))
            {
                HUD.enabled = !HUD.enabled;
            }

            if (ImGui.MenuItem("Settings"))
            {
                HUD.settingsPageOn = !HUD.settingsPageOn;
            }
        }
```

5. **Do not list ModMenu in `mod.toml`.** AircraftHUD's manifest has only `name`, `version`, `author` and `assets`; there is no dependency on ModMenu, so the mod loads without it and the attribute is inert:

```toml
# mod.toml L1-L5
name="AircraftHUD"
version="0.2.1"
author="UriahGnu"

assets = [ "HudAssests.xml" ]
```

6. **Check the output folder** after a build: `AircraftHUD.dll`, `mod.toml` and `ModMenu.Attributes.dll` should sit together. If the DLL is missing, ModMenu's reflection over your method may fail to resolve the attribute type (unverified failure mode).

## Complete example

| File | Role |
|---|---|
| [ModMenu.Attributes.dll](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ModMenu.Attributes.dll) | The vendored binary (repository root) |
| [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L24-L26) | `Reference` with `HintPath`, no `Private="False"` |
| [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L27-L29) | `CopyToOutputDirectory=Always` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L153-L165) | The entry method |
| [mod.toml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/mod.toml) | No ModMenu dependency |

## Variations and gotchas

- **No fallback menu.** Without ModMenu the settings page cannot be opened; the HUD itself is on by default and configured from `hud.ini`. See [the fallback pattern](../add-a-menu-with-modmenu-fallback/overview.md).
- **Version drift.** A committed binary does not update and is part of the contract with whichever ModMenu the player has. A package reference (approach 1) makes the version explicit and updatable.
- **Undeclared dependency.** Because `mod.toml` has no dependency on ModMenu, StarMap cannot warn about a missing ModMenu or order your mod after it.
- **Licence and size.** The DLL is a binary in your repository; check that redistribution is permitted by its author before shipping it in a release zip.
- **If ModMenu already loaded the attribute assembly** into the default context, step 1 of the resolver returns that copy and yours is never opened; either way the type identity is not what ModMenu compares if it matches by name (see [approach 3](list-a-mod-in-modmenu-3.md); mod claim, unverified).

## See also

- [Overview](overview.md)
- [Approach 1: reference ModMenu.Attributes and ship it](list-a-mod-in-modmenu-1.md)
- [Approach 3: copy the attribute source into the mod](list-a-mod-in-modmenu-3.md)
- [Ship a third-party DLL with a mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)
- Mod feature: [ModMenu entry with a vendored attributes DLL](../../../../mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes.md)

[^starmap-starmap-core-modrepository-modassemblyloadcontext]: StarMap: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
