---
type: Mod Feature
title: Isolate each mod in its own AssemblyLoadContext
description: Every StarMap mod gets a non-collectible ModAssemblyLoadContext that resolves shared assemblies from the loader and the game first, then from declared dependency mods, then from the mod's own deps.json
mod: starmap
category: getting-started
techniques: [assembly-load-context, native-interop, reflection]
hooks: [assembly-load-context ModAssemblyLoadContext.Load]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Mod]
tags: [starmap, assembly-load-context, dependencies, packaging, native-libraries, type-identity]
---

Each mod's entry assembly is loaded into its own `ModAssemblyLoadContext` (ALC). The context
decides, for every assembly the mod references, whether to hand back a shared copy (Harmony, the
game, StarMap.API, an exporting dependency) or the mod's own private copy. This is what lets two
mods ship different versions of the same NuGet library, and also what makes some packaging
mistakes invisible until run time.

## How it works

`RuntimeMod.TryCreateMod` creates the context with the mod id, the path of
`<mod folder>/<EntryAssembly>.dll` and the core (game) context, then loads the entry assembly by
name. The constructor builds an `AssemblyDependencyResolver` rooted at the entry DLL and calls
`base()`, so the context is not collectible.[^sm-runtimemod][^sm-modalc]

`Load(AssemblyName)` tries, in order:

| Step | Source | What lands here |
|---|---|---|
| 1 | Any assembly already in `AssemblyLoadContext.Default` with the same simple name (case-insensitive) | `0Harmony` (loaded there by the launcher), `StarMap.Types`, framework assemblies[^sm-program] |
| 2 | Any assembly already loaded in the core context with that name | `KSA`, `Brutal.*`, `StarMap.API`, `StarMap.Core`, game libraries already in use |
| 3 | `coreContext.LoadFromAssemblyName(name)`, ignoring `FileNotFoundException` | Game libraries not loaded yet (resolved through KSA's dependency resolver) |
| 4 | For each dependency mod whose import set contains the name: that mod's context | Contract assemblies shared between mods (see [shared contract assemblies](shared-contract-assemblies.md)) |
| 5 | The mod's own `AssemblyDependencyResolver` -> `LoadFromAssemblyPath` | Private dependencies listed in `<EntryAssembly>.deps.json` |

If every step returns nothing, `Load` returns `null` and the runtime falls back to `Default`.

Consequences for packaging:

- `0Harmony.dll` and `StarMap.API.dll` copies in a mod folder never load (steps 1 and 2 win), so
  gatOS and purrTTY deliberately leave them out of their deployments.[^gatos-csproj][^purrtty-csproj]
- Any assembly name the game can resolve wins over the mod's copy (step 3), whatever version the
  mod was built against.
- Step 5 needs the mod's `.deps.json` next to the DLL; gatOS and purrTTY both copy
  `$(ProjectDepsFilePath)` into the deployed folder for this reason.
- `ModAssemblyLoadContext` does not override `LoadUnmanagedDll`, so native libraries are not
  probed in the mod folder. Mods that P/Invoke register a resolver with
  `NativeLibrary.SetDllImportResolver` from a `[ModuleInitializer]` and load the library from
  `Path.GetDirectoryName(assembly.Location)`: purrTTY's vendored Ghostty.Vt binding and
  AdvancedFlightComputer's clarabel/scs solvers both do this.[^purrtty-nativeresolver][^afc-nativelibraries]
- Two mods that both ship, say, a TOML library get two separate copies with separate type
  identities and separate statics. Sharing requires an explicit export/import.

## Lifecycle and timing

The context is created and the entry assembly is loaded during `StarMapCore.Init`
(`startup:starmap-before-main`). After that, `Load` runs lazily whenever the JIT first needs a
referenced assembly, which can be any time: inside a hook, on a background thread the mod
started, or while Harmony resolves a patch method. The resolution rules are the same at every
point. Contexts are never unloaded; StarMap made them non-collectible in release 0.3.5 ("Make the
AssemblyLoadContexts not collectable", PR #42) and stopped trying to unload them in 0.3.6 ("Do not
try to unload the assembly load contexts", PR #44).

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L14-L73) | The five-step resolution order |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L57-L73) | Creates the context and loads the entry assembly |
| [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L18) | Puts `0Harmony` in `Default` so step 1 shares it |

## Snippets

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L24-L47
protected override Assembly? Load(AssemblyName assemblyName)
{
    var existingInDefault = Default.Assemblies
        .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
    if (existingInDefault != null)
        return existingInDefault;

    var existingInGameContext = _coreAssemblyLoadContext?.Assemblies
        .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
    if (existingInGameContext != null)
        return existingInGameContext;

    if (_coreAssemblyLoadContext != null)
    {
        try
        {
            var asm = _coreAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
            if (asm != null)
                return asm;
        }
        catch (FileNotFoundException)
        {
        }
    }
```

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L49-L72
if (RuntimeMod is RuntimeMod modInfo && modInfo.Dependencies.Count > 0)
{
    foreach (var (dependency, importedAssemblies) in modInfo.Dependencies)
    {
        if (importedAssemblies.Contains(assemblyName.Name ?? string.Empty))
        {
            try
            {
                var asm = dependency.ModAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
                if (asm != null)
                    return asm;
            }
            catch (FileNotFoundException)
            {
            }
        }
    }
}

var foundPath = _modDependencyResolver.ResolveAssemblyToPath(assemblyName);
if (foundPath is null)
    return null;

return LoadFromAssemblyPath(Path.GetFullPath(foundPath));
```

```csharp
// KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs L15-L36
internal static class NativeLibraries
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, (name, asm, _) =>
        {
            string? fileName = name switch
            {
                "clarabel_c" => OperatingSystem.IsWindows() ? "clarabel_c.dll" : "libclarabel_c.so",
                "scs" => OperatingSystem.IsWindows() ? "scs.dll" : "libscs.so",
                _ => null,
            };
            if (fileName == null)
                return IntPtr.Zero;
            string dir = Path.GetDirectoryName(asm.Location) ?? ".";
            string candidate = Path.Combine(dir, fileName);
            return NativeLibrary.TryLoad(candidate, out IntPtr handle) ? handle : IntPtr.Zero;
        });
    }
```

```csharp
// RealAtmospheres: ModMain.cs L44-L57
string installRoot = Path.GetDirectoryName(modType.Assembly.Location)!;

// Planet.Render.Core loads lazily and is not in the process yet at
// BeforeMain: pre-load it into KSA's own load context so the instance
// we patch is the one the game will use.
Type? shaderUtils = AccessTools.TypeByName("RenderCore.ShaderModuleUtils");
if (shaderUtils == null)
{
    var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(modType.Assembly)
        ?? System.Runtime.Loader.AssemblyLoadContext.Default;
    var prc = alc.LoadFromAssemblyPath(Path.Combine(installRoot, "Planet.Render.Core.dll"));
    shaderUtils = prc.GetType("RenderCore.ShaderModuleUtils")
        ?? throw new InvalidOperationException("RenderCore.ShaderModuleUtils not found");
}
```

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `KSA.Mod` (as a type whose `Assembly` identifies the game's context) | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L14) | RealAtmospheres resolves `KSA.Mod` with `AccessTools.TypeByName`, then calls `AssemblyLoadContext.GetLoadContext(modType.Assembly)` to load a game library into the core context before the game does[^ra-modmain] |

## Pitfalls and version notes

- **Finding your own folder.** `typeof(MyMod).Assembly.Location` is the real path inside the mod
  folder because the context loads from a file path. RealAtmospheres and OhMyStars rely on it;
  `KSA.Mod.DirectoryPath` from `StarMapImmediateLoad` is the alternative.
- **Reaching types in another context.** `AccessTools.TypeByName` and `AssemblyLoadContext.All`
  see every context. KittenExtensions iterates `AssemblyLoadContext.All` to find asset-attribute
  types declared by other mods.[^kx-assetex]
- **Lazily loaded game assemblies.** A game library that KSA has not touched yet is not in the
  core context at `StarMapBeforeMain`; patch it after loading it into the core context, not into
  your own, or you patch a copy the game never uses.[^ra-modmain]
- **Version conflicts resolve to the game.** If a mod needs a newer version of a library the game
  already ships, step 3 still returns the game's copy. There is no per-mod override.
- **Case sensitivity differs.** Steps 1 and 2 compare names case-insensitively; the step-4 import
  set is a plain `HashSet<string>`, so its names must match the assembly name exactly.

## See also

- [Share contract assemblies between mods](shared-contract-assemblies.md)
- [Order mod loading with a ModDependencies graph](mod-dependency-graph.md)
- [Host KSA in-process from a launcher executable](host-process-launch.md)
- [Load flat per-OS native libraries with a DllImportResolver and a libc mapping](../../../howto/getting-started/dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-1.md) - how-to article demonstrating this feature
- [Package NuGet dependencies with deps.json and ship exactly the reference closure](../../../howto/getting-started/dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md) - how-to article demonstrating this feature
- [Follow the boot sequence: host process, load contexts and hook binding](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md) - how-to article demonstrating this feature
- [Preload a lazily loaded game assembly into the core load context before patching it](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-4.md) - how-to article demonstrating this feature
- [Find your mod's install folder at runtime: Assembly.Location, ModLibrary.Find, Documents](../../../howto/getting-started/loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md) - how-to article demonstrating this feature

[^sm-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-program]: StarMap/Program.cs
[^gatos-csproj]: gatOS gatOS.GameMod/gatOS.GameMod.csproj
[^purrtty-csproj]: purrtty purrTTY.GameMod/purrTTY.GameMod.csproj
[^purrtty-nativeresolver]: purrtty Ghostty.Vt NativeLibraryResolver.cs
[^afc-nativelibraries]: KSA-AdvancedFlightComputer NativeLibraries.cs
[^ra-modmain]: RealAtmospheres ModMain.cs
[^kx-assetex]: KittenExtensions/AssetEx.cs
