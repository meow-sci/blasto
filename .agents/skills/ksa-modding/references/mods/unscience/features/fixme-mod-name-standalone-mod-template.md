---
type: Mod Feature
title: Minimal standalone StarMap mod template with a separate feature library
description: The fixme-mod-name project pair is the repo's scaffold for a new mod, a StarMap entry class with the four lifecycle attributes, a Harmony patcher that always applies the hotkey guard, an F11 ImGui window and a sibling .lib project, copied and renamed by mkmod.ts
mod: unscience
submod: fixme-mod-name
category: getting-started
techniques: [starmap-lifecycle, harmony-prefix, imgui-window, toml-config]
hooks:
  - "starmap StarMapImmediateLoad (zero-argument signature: never dispatched)"
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapUnload
  - harmony-prefix KSA.GameSettings.OnKeyAll (HotkeyGuard)
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.GameSettings]
tags: [template, scaffold, starmap, harmony, csproj, mod-toml]
---

`fixme-mod-name` and `fixme-mod-name.lib` are the smallest complete mod in the repository and the
template every other sub-mod was created from. `mkmod.ts` copies both folders, skipping `bin`/`obj`,
and replaces the `fixme-mod-name` / `FixmeModName` placeholders in names and contents with a new
kebab-case and PascalCase name (`bun run mkmod.ts orbital-cam OrbitalCam`).[^mkmod][^unscience-agents]
The template is not bundled into the supermod; it exists to start new features.

Mechanism in one line: one `[StarMapMod]` class with the full set of lifecycle attributes, one static
`Patcher` owning a Harmony instance (always applying `HotkeyGuard`), and an ImGui window toggled from
`[StarMapAfterGui]`.

## How it works

Files and what each demonstrates:

| File | Shows |
|---|---|
| `fixme-mod-name/mod.toml` | Manifest: `name`, `description`, `version`, `author`, and a `[StarMap]` table with `EntryAssembly = "MeowSci.FixmeModName"` naming the assembly StarMap loads[^fixme-toml] |
| `fixme-mod-name/fixme-mod-name.csproj` | `StarMap.API` 0.3.6 and `Lib.Harmony` 2.4.2 with `PrivateAssets="all"`; game DLLs (`KSA`, `Brutal.Core.*`, `Brutal.ImGui*`) as `Private=false` references through `$(KSAFolder)` HintPaths; `mod.toml` copied to output[^fixme-csproj] |
| `fixme-mod-name/Mod.cs` | `[StarMapMod]` class with `ImmediateUnload => false` (StarMap 4dc60f8 never reads this property), `[StarMapImmediateLoad]` (zero-argument, so never dispatched), `[StarMapAllModsLoaded]` (patch), `[StarMapBeforeGui]`, `[StarMapAfterGui]` (F11 toggle and window), `[StarMapUnload]` (unpatch)[^fixme-mod] |
| `fixme-mod-name/Patcher.cs` | `new Harmony("fixme-mod-name")`, `PatchAll` of the assembly's attribute patches plus `HotkeyGuard.Patch`; reverse order on unload[^fixme-patcher] |
| `fixme-mod-name.lib/FixmeModNameLib.cs` | Where headless feature code goes, so it can later be hosted by the supermod[^fixme-lib] |

`KSAFolder` is resolved once for every project by `Directory.Build.props` (env var `KSA_DLL_DIR`, a
sibling `ksa-game-assemblies` checkout, or per-OS defaults), and a build target fails early with one
readable error when `KSA.dll` is missing, instead of hundreds of missing-type errors.[^build-props]

## Lifecycle and timing

| Attribute | Template action | Frame position |
|---|---|---|
| `[StarMapImmediateLoad]` | Nothing | Never runs: StarMap registers an `ImmediateLoad` method only with exactly one `KSA.Mod` parameter and skips the zero-argument `OnImmediateLoad()`[^starmap-baseattributes][^starmap-runtimemod] |
| `[StarMapAllModsLoaded]` | `Patcher.Patch()` | `startup:all-mods-loaded` (after `ModLibrary.LoadAll`) |
| `[StarMapBeforeGui]` | Nothing (place for per-frame non-UI work) | Prefix of `Program.OnDrawUiFrame`, skipped when the HUD is hidden |
| `[StarMapAfterGui]` | F11 via `ImGui.IsKeyPressed`, then `ImGui.Begin`/`End` | Postfix of `Program.OnDrawUiViewports`, inside the ImGui frame |
| `[StarMapUnload]` | `Patcher.Unload()` | Game exit, after `Program.Dispose` and after StarMap's id-less `UnpatchAll()`, so the unpatch finds nothing left to remove[^starmap-core] |

The template's comments call these "OnImmediateLoad / OnFullyLoaded / OnBeforeGui / OnAfterUi /
Unload"; the names are arbitrary, the attributes are what StarMap dispatches on.

## Key code

| Path | Role |
|---|---|
| [fixme-mod-name/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Mod.cs) | Entry class |
| [fixme-mod-name/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Patcher.cs) | Harmony owner with hotkey guard |
| [fixme-mod-name/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/mod.toml) | Manifest |
| [fixme-mod-name/fixme-mod-name.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/fixme-mod-name.csproj) | Project references and game DLL hint paths |
| [mkmod.ts](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/mkmod.ts) | Copy-and-rename scaffolder |

## Snippets

```csharp
// fixme-mod-name/Mod.cs L9-L56
[StarMapMod]
public class Mod
{
  public bool ImmediateUnload => false;
// ...
  [StarMapImmediateLoad]
  public void OnImmediateLoad() { }

  [StarMapAllModsLoaded]
  public void OnFullyLoaded()
  {
    try
    {
      Patcher.Patch();
      _isInitialized = true;
    }
// ...
  [StarMapBeforeGui]
  public void OnBeforeUi(double dt) { }

  [StarMapAfterGui]
  public void OnAfterUi(double dt)
  {
    try
    {
      if (!_isInitialized || _isDisposed) return;

      if (ImGui.IsKeyPressed(ImGuiKey.F11))
        _windowVisible = !_windowVisible;

      if (_windowVisible)
        RenderWindow();
    }
// ...
```

```csharp
// fixme-mod-name/Patcher.cs L9-L39
[HarmonyPatch]
internal static class Patcher
{
    private static Harmony? _harmony = new Harmony("fixme-mod-name");

    public static void Patch()
    {
        try
        {
            _harmony?.PatchAll(typeof(Patcher).Assembly);
            if (_harmony != null) HotkeyGuard.Patch(_harmony);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"fixme-mod-name: Error applying patches: {ex.Message}");
        }
    }

    public static void Unload()
    {
        try
        {
            if (_harmony != null) HotkeyGuard.Unpatch(_harmony);
            _harmony?.UnpatchAll("fixme-mod-name");
            _harmony = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"fixme-mod-name: Error removing patches: {ex.Message}");
        }
    }
```

```toml
# fixme-mod-name/mod.toml L1-L7
name = "fixme-mod-name"
description = "fixme-mod-name"
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "MeowSci.FixmeModName"
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GameSettings.OnKeyAll` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422) | Patched indirectly through `HotkeyGuard` |

## Pitfalls and version notes

- Copying the template's `OnImmediateLoad()` and putting code in it does nothing: the method has no
  `KSA.Mod` parameter, so StarMap never registers it. Declare `public void OnImmediateLoad(KSA.Mod mod)`
  when work really belongs in that window (StarMap's prefix on the mod's own `Mod.PrepareSystems`,
  before its systems and asset bundles load).[^starmap-baseattributes][^starmap-runtimemod]
- `UnpatchAll("fixme-mod-name")` removes every patch under that Harmony id; fine for a mod that owns its
  id. The supermod mostly unpatches method by method instead (Camera Controller Override is the
  exception). At game exit none of this matters: StarMap's `DeInit` has already called `UnpatchAll()`
  without an id before it dispatches `[StarMapUnload]`.[^starmap-core]
- `PatchAll(assembly)` applies every `[HarmonyPatch]` class in the assembly at once; the feature libraries
  instead expose explicit `Apply/Remove` so the supermod can control order and isolate failures.
- `F11` is shared with the supermod and several standalone hosts; two hosts loaded together would toggle
  together. Only the supermod ships, so this matters only during development.
- The template's README still shows an older `class Mod : StarMapMod` base-class style with `override`
  methods; the code uses StarMap attributes on a plain class. `jplrepo/README.md` is a renamed copy of the
  same README. Trust the code.

## See also

- [ISubmod contract](./lib-isubmod-contract.md)
- [Hotkey guard](./lib-hotkey-guard.md)
- [Build and release](./unscience-build-and-release.md)
- [Sub-mod: fixme-mod-name](../submods/fixme-mod-name.md)
- [Mod overview](../overview.md)
- [Scaffold a mod with mkmod.ts and grow it into a library hosted by a supermod](../../../howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-2.md) - how-to article demonstrating this feature

[^mkmod]: mkmod.ts
[^unscience-agents]: AGENTS.md
[^fixme-toml]: fixme-mod-name/mod.toml
[^fixme-csproj]: fixme-mod-name/fixme-mod-name.csproj
[^fixme-mod]: fixme-mod-name/Mod.cs
[^fixme-patcher]: fixme-mod-name/Patcher.cs
[^fixme-lib]: fixme-mod-name.lib/FixmeModNameLib.cs
[^build-props]: Directory.Build.props
[^starmap-baseattributes]: StarMap.API/BaseAttributes.cs
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
