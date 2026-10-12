---
type: Game Feature
title: Ship and load native libraries from a mod folder
description: Why StarMap's per-mod load context leaves native libraries to default probing, and two ways to ship and load them with an explicit resolver - flat per-OS files and a per-RID payload with an ABI check
category: getting-started
group: dependencies
topic: ship-native-libraries-with-a-mod
demonstrated_by:
  - mods/purrtty/features/per-platform-native-library-resolver
  - mods/starmap/features/per-mod-assembly-load-context
  - mods/ksa-advancedflightcomputer/features/native-conic-solver-interop
  - mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets
  - mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: []
tags: [native-interop, pinvoke, dllimportresolver, moduleinitializer, rid, packaging]
---

Native libraries (a terminal-emulation core, convex solvers) have to ship in the mod folder and be loaded by file path.
StarMap's per-mod context resolves managed assemblies only: `ModAssemblyLoadContext` overrides `Load` and does not
override `LoadUnmanagedDll`, so a P/Invoke falls back to the runtime's default probing: the calling assembly's folder
under the platform name variations of the import name, then the OS search path. That never looks in a
`runtimes/<rid>/native/` layout and leaves the file name to convention; purrTTY's comments also report it as unreliable
inside KSA.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)
Both approaches therefore install a `NativeLibrary.SetDllImportResolver` from a `[ModuleInitializer]` that loads the
file from `Path.GetDirectoryName(assembly.Location)`.

## Engine background

- No KSA type is involved; the mechanism is StarMap's load context plus .NET's `NativeLibrary` API.
- The resolver is registered the first time any code in the binding assembly is touched, which is during or after
  `startup:all-mods-loaded` ([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md)); native calls may then run on the main thread or on
  `thread:background` threads the mod owns.
- `SetDllImportResolver` may be called only once per assembly; the library stays loaded for the process lifetime.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Flat per-OS file names with a libc mapping](./ship-native-libraries-with-a-mod-1.md) | purrTTY | `native-interop` | `startup:all-mods-loaded` | One zip for three OSes with the simplest layout; assumes one RID per OS |
| [Per-RID payload from the deploy targets, layout-exact structs](./ship-native-libraries-with-a-mod-2.md) | KSA-AdvancedFlightComputer | `native-interop`, `msbuild` | `startup:all-mods-loaded`, `thread:background` | Pinned toolchains, shared payload list and an ABI check at the cost of build complexity |

## Checklist

- Resolver registered once, from a `[ModuleInitializer]`, for every native library of the assembly.
- Files beside the assembly (or under `runtimes/<rid>/native`); only shared libraries.
- `libc` mapped explicitly on Unix.
- Build fails when a payload native is missing; release verified against the payload list
  ([Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)).
- Structs and `bool`/index widths match the C headers; pin arrays only for the call.

## See also

- [Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md), [Ship assets and code in one mod folder and find it at runtime](../../loader/ship-content-and-code-in-one-mod/overview.md), [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md)
- [Per-platform native library resolver for a mod's AssemblyLoadContext](../../../../mods/purrtty/features/per-platform-native-library-resolver.md), [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md),
  [Ship and call native solver libraries from a mod](../../../../mods/ksa-advancedflightcomputer/features/native-conic-solver-interop.md),
  [MSBuild deploy and packaging for a StarMap mod with native payload](../../../../mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets.md),
  [Build vendored native libraries for Windows and Linux and check the ABI against C#](../../../../mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check.md)
