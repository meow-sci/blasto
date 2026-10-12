---
type: Mod Feature
title: Per-platform native library resolver for a mod's AssemblyLoadContext
description: A ModuleInitializer installs NativeLibrary.SetDllImportResolver so P/Invoke finds the right per-OS shared library beside the mod DLL inside StarMap's mod AssemblyLoadContext, plus a libc resolver for glibc and macOS
mod: purrtty
category: getting-started
techniques: [native-interop, assembly-load-context]
hooks:
  - module-initializer Ghostty.Vt.Native.NativeLibraryResolver.Register
  - module-initializer purrTTY.Core.Terminal.Process.UnixPtyNative.RegisterResolver
lifecycle: [startup:all-mods-loaded]
game_types: []
tags: [native, p-invoke, assembly-load-context, cross-platform, starmap, deployment]
---

purrTTY ships one zip for Windows, Linux and macOS. The three native libghostty-vt builds sit side
by side in the mod folder (`ghostty-vt.dll`, `libghostty-vt.so`, `libghostty-vt.dylib`), and a
resolver chooses the right one at the first P/Invoke. The same pattern maps the logical `libc`
import used by the Unix PTY code to the real system library. Any KSA mod that bundles native code
can copy it.

## How it works

1. **Packaging.** The binding's csproj copies all three libraries flat into the build output
   (`Link=` names), and the GameMod `CopyCustomContent` target copies whichever exist next to
   `purrTTY.GameMod.dll` in the deployed `purrTTY/` folder.[^vt-csproj][^gamemod-csproj]
2. **Why a resolver.** StarMap loads each mod into its own `ModAssemblyLoadContext`, which overrides
   only `Load` (managed assemblies: default context, game context, the assemblies the mod imports
   from its dependencies through `ImportedAssemblies`, then the mod's `AssemblyDependencyResolver`). It does not override
   `LoadUnmanagedDll`, so native imports fall back to the runtime's default probing.[^starmap-mod-alc]
   purrTTY's comment says that probing does not reliably search the assembly directory in KSA's
   plugin context; a per-assembly resolver makes the lookup explicit and independent of the host.[^resolver]
3. **Registration.** `NativeLibraryResolver.Register` is a `[ModuleInitializer]`, so it runs the
   first time anything in `Ghostty.Vt.dll` is touched, before any P/Invoke. It calls
   `NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve)` once
   (guarded by `Interlocked.Exchange`, because a second call for the same assembly throws).
4. **Resolution.** For the import name `ghostty-vt` it picks the OS file name, tries
   `<assembly dir>/<file>` then `<assembly dir>/runtimes/<rid>/native/<file>`, and finally falls
   back to `NativeLibrary.TryLoad(name, assembly, searchPath)`. Other import names return zero so
   default probing continues. `Assembly.Location` is the mod folder because StarMap loads mod
   assemblies with `LoadFromAssemblyPath`.
5. **libc.** `UnixPtyNative` registers its own resolver for `purrTTY.Terminal.dll` (skipped on
   Windows): `libc` maps to `libc.so.6` (glibc ships `libc.so` as a linker script, which `dlopen`
   cannot load) or to `/usr/lib/libSystem.B.dylib` on macOS (gotcha 16).[^unix-native][^gotchas]

## Lifecycle and timing

- Module initializers run on first access to the assembly. For purrTTY that is during
  `TerminalMod.OnFullyLoaded` (`[StarMapAllModsLoaded]`, `startup:all-mods-loaded`) when the first
  session or layout code touches the binding, or later on the first terminal open. The resolver is
  therefore installed before the first `VTWrite`, whatever thread triggers it.
- The OS loader keeps the library loaded for the process lifetime; there is no unload path, which
  matches StarMap only unloading mods at game exit.

## Key code

| Path | Role |
|---|---|
| [vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs#L19-L90) | Module initializer, resolver, candidate paths, per-OS file name |
| [purrTTY.Terminal/Pty/Process/UnixPtyNative.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyNative.cs#L22-L62) | `libc` resolver |
| [vendor/Ghostty.Vt/Ghostty.Vt.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/Ghostty.Vt.csproj) | Flat copy of all three native libraries |
| [purrTTY.GameMod/purrTTY.GameMod.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/purrTTY.GameMod.csproj#L47-L53) | Deploys the native files beside the mod DLL |
| [vendor/Ghostty.Vt/src/Native/NativeMethods.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeMethods.cs#L5-L13) | `LibraryName = "ghostty-vt"` used by every import |

## Snippets

```csharp
// vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs L25-L60
    [ModuleInitializer]
    internal static void Register()
    {
        // Idempotent: SetDllImportResolver throws if called twice for one assembly.
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        var fileName = PlatformFileName();
        var assemblyDir = Path.GetDirectoryName(assembly.Location);

        foreach (var candidate in CandidatePaths(assemblyDir, fileName))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        // Fall back to the OS loader's default search (handles cases where the
        // library is installed system-wide or already resolvable by name).
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallback)
            ? fallback
            : nint.Zero;
    }
```

StarMap's mod context: managed resolution only, no `LoadUnmanagedDll` override.

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L24-L34
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
```

## Game APIs touched

None from KSA. The mechanism relies on .NET (`NativeLibrary`, `ModuleInitializerAttribute`) and on
StarMap's `ModAssemblyLoadContext` behavior.

## Pitfalls and version notes

- `SetDllImportResolver` can be called only once per assembly; guard it.
- Flat file names work only while there is one RID per OS. Adding osx-x64 or linux-arm64 needs the
  `runtimes/<rid>/native` layout, which the resolver already probes.[^vt-csproj]
- Ship only shared libraries; static archives and Windows import libs cannot be loaded.
- `DllImport("libc")` alone fails on many glibc systems; map it explicitly.
- `Assembly.Location` is empty for single-file or in-memory loads; StarMap loads from paths, so it
  is valid here (the csproj suppresses `IL3000` for this reason).

## See also

- [Vendored libghostty-vt binding](./vendored-libghostty-vt-binding.md)
- [POSIX pty spawn without fork](./posix-pty-spawn-without-fork.md)
- [Cross-platform build and release](./cross-platform-build-and-release.md)
- [Inter-mod shell contract shared through StarMap assembly imports](./custom-shell-contract-via-assembly-imports.md) (the managed side of StarMap's ALCs)
- [purrTTY overview](../overview.md)
- [Load flat per-OS native libraries with a DllImportResolver and a libc mapping](../../../howto/getting-started/dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-1.md) - how-to article demonstrating this feature

[^vt-csproj]: vendor/Ghostty.Vt/Ghostty.Vt.csproj
[^gamemod-csproj]: purrTTY.GameMod/purrTTY.GameMod.csproj
[^starmap-mod-alc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^resolver]: vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs
[^unix-native]: purrTTY.Terminal/Pty/Process/UnixPtyNative.cs
[^gotchas]: purrTTY docs/gotchas.md
