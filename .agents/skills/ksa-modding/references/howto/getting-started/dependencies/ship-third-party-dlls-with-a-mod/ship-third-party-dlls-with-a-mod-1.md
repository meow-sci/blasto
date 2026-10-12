---
type: How-To
title: Copy a vendored DLL beside the mod and let AssemblyDependencyResolver find it
description: Ship an attribute or helper assembly such as ModMenu.Attributes.dll next to your mod DLL without a mod.toml dependency, and know what StarMap resolves from the game instead
category: getting-started
group: dependencies
topic: ship-third-party-dlls-with-a-mod
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy
  - mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes
techniques: [assembly-load-context, msbuild]
hooks: [reflection System.Runtime.Loader.AssemblyDependencyResolver]
lifecycle: [startup:starmap-before-main]
game_types: []
tags: [third-party-dll, modmenu, vendored, assembly-load-context, deps-json, packaging]
---

Some DLLs are not part of the game and not part of StarMap: the `ModMenu.Attributes` assembly that gives your mod a
submenu under ModMenu, a helper library, a binding. They must travel with your mod. This article shows the simplest
route, used by Kitten-Engineer-Redux and KSA_AircraftHUD: reference the DLL, copy it into the mod folder, and let the
mod's own `AssemblyDependencyResolver` find it. It also lists what you must **not** ship. For NuGet packages with a
`deps.json` see [Package NuGet dependencies with deps.json](./ship-third-party-dlls-with-a-mod-2.md); for native libraries
[Ship and load native libraries from a mod folder](../ship-native-libraries-with-a-mod/overview.md).

## Background: how the game does it

When your code first needs an assembly, your mod's `ModAssemblyLoadContext.Load` asks, in order: (1) anything already in
`AssemblyLoadContext.Default` with that simple name (Harmony, `StarMap.Types`, framework), (2) anything already loaded
in the game context (KSA, Brutal, `StarMap.API`), (3) the game context's `LoadFromAssemblyName` for game libraries not
loaded yet, (4) dependency mods whose import set contains the name, (5) your own `AssemblyDependencyResolver`, built on
your entry DLL, which finds private dependencies beside it.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72) Consequences: a copy of
`0Harmony.dll`, `StarMap.API.dll` or any game assembly in your folder is **never loaded** (steps 1 to 3 win); and an
assembly that nobody else loaded, like `ModMenu.Attributes`, is found by step 5.

## Lifecycle and timing

Resolution is lazy and happens at JIT time, the first time code needs the type: for an attribute assembly that is when
ModMenu or the runtime reads your attribute, long after `startup:starmap-before-main`. The entry assembly loads during
`StarMapCore.Init`; its siblings load on demand. If ModMenu had already loaded the attribute assembly into the default
context, that copy would be returned first.

## Steps

1. **Reference the DLL by `HintPath`** (or the NuGet package) and leave `Private` at its default so it is copy-local.
   AircraftHUD vendors `ModMenu.Attributes.dll` at the repo root:[AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L24-L26)

   ```xml
   <!-- AircraftHUD.props L24-L26 -->
       <Reference Include="ModMenu.Attributes">
         <HintPath>ModMenu.Attributes.dll</HintPath>
       </Reference>
   ```

   and marks the file `CopyToOutputDirectory=Always` in the csproj:[AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L27-L29)

   ```xml
   <!-- AircraftHUD.csproj L27-L29 -->
       <None Update="ModMenu.Attributes.dll">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
   ```

   Kitten-Engineer-Redux takes the same assembly from the `ModMenu.Attributes` 1.0.2 NuGet package and copies it in a
   post-build target.
2. **Use the attribute** on a public static method of your `[StarMapMod]` class; ModMenu discovers it:[AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L153-L165)

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

3. **Deploy it with the mod.** Kitten-Engineer-Redux's post-build target copies the mod DLL and, if present,
   `ModMenu.Attributes.dll`:[KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj#L68-L83)

   ```xml
   <!-- KittenEngineerRedux.csproj L68-L83 -->
       <Target Name="PostBuild" AfterTargets="PostBuildEvent">
           <PropertyGroup>
               <SourceDll>$(TargetDir)KittenEngineerRedux.dll</SourceDll>
               <SourceAttributesDll>$(TargetDir)ModMenu.Attributes.dll</SourceAttributesDll>
               <DestinationDir>C:\Users\Matthew\Documents\My Games\Kitten Space Agency\mods\KittenEngineerRedux\</DestinationDir>
           </PropertyGroup>
           
           <Message Importance="High" Text="Executing Post-Build Deployment..." />
           
           <MakeDir Directories="$(DestinationDir)" Condition="!Exists('$(DestinationDir)')" />
           
           <Copy SourceFiles="$(SourceDll)" DestinationFolder="$(DestinationDir)" SkipUnchangedFiles="false" />
           <Copy SourceFiles="$(SourceAttributesDll)" DestinationFolder="$(DestinationDir)" SkipUnchangedFiles="false" Condition="Exists('$(SourceAttributesDll)')" />
           
           <Message Importance="High" Text="Mod successfully deployed to: $(DestinationDir)" />
       </Target>
   ```

   (Improved deploy targets: [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md).) The packaged zip holds `KittenEngineerRedux.dll`,
   `mod.toml` and `ModMenu.Attributes.dll` at its root.
4. **Do not add a `mod.toml` dependency** unless you need the other mod's *code*. ModMenu is not required for the
   attribute to compile or load; without ModMenu installed the attribute is inert metadata.
5. **Never ship these:** `0Harmony.dll`, `StarMap.API.dll`, `KSA.dll` and other `Brutal.*` assemblies. They are resolved
   from the default and game contexts regardless; gatOS and purrTTY leave them out of their deployments on purpose.

## Complete example

| File | Role |
|---|---|
| [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props), [AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj) | Vendored DLL reference and copy |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs) | The `[ModMenuEntry]` method |
| [KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj) | NuGet attribute assembly plus post-build copy |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs) | `TryCreateMod` builds the context from the entry DLL path |

## Variations and gotchas

- **Version drift.** A committed binary does not update; the attribute's constructor signature is part of the contract
  with whatever ModMenu version the player has. The NuGet package makes the version explicit.
- **No fallback menu.** Without ModMenu nothing calls the method; AircraftHUD's settings are then reachable only by
  editing `hud.ini`. A fallback is possible (gatOS draws the same items into the game menu bar when ModMenu is
  absent).
- **The deploy copy matters.** Kitten-Engineer-Redux's deploy copies only the two DLLs and not `mod.toml` (a first
  deploy needs the manifest by hand), and no `deps.json`. Without a `.deps.json` the resolver probes the entry
  assembly's folder (documented .NET behaviour for components without one; not exercised here). With a `deps.json`
  present, only assemblies listed in it resolve.
- **Case.** Names match case-insensitively for steps 1 and 2 but the import set (step 4) is case-sensitive.
- **Licences.** Vendored binaries carry their licences; see [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md).

## See also

- [Package NuGet dependencies with deps.json](./ship-third-party-dlls-with-a-mod-2.md), [Ship third-party DLLs next to your mod](./overview.md)
- [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md), [ModMenu entry with a vendored attributes DLL](../../../../mods/ksa-aircrafthud/features/modmenu-entry-vendored-attributes.md)
- [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md), [Ship and load native libraries from a mod folder](../ship-native-libraries-with-a-mod/overview.md), [Host process, contexts and hook binding walkthrough](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md)
