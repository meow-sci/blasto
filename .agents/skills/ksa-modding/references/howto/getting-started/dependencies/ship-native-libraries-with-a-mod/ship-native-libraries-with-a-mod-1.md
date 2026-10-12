---
type: How-To
title: Load flat per-OS native libraries with a DllImportResolver and a libc mapping
description: Ship Windows, Linux and macOS native libraries side by side and pick the right file at the first P/Invoke with a ModuleInitializer resolver, because StarMap's mod context leaves native libraries to default probing
category: getting-started
group: dependencies
topic: ship-native-libraries-with-a-mod
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/purrtty/features/per-platform-native-library-resolver
  - mods/starmap/features/per-mod-assembly-load-context
techniques: [native-interop, assembly-load-context]
hooks: [reflection System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver]
lifecycle: [startup:all-mods-loaded]
game_types: []
tags: [native-interop, pinvoke, dllimportresolver, moduleinitializer, cross-platform, purrtty, libc]
---

purrTTY ships one zip for Windows, Linux and macOS. The three native `ghostty-vt` builds sit side by side in the mod
folder (`ghostty-vt.dll`, `libghostty-vt.so`, `libghostty-vt.dylib`), and a resolver picks the right one at the first
P/Invoke. The same pattern maps the logical `libc` import used by the Unix PTY code to the real system library. Any
KSA mod that bundles native code can copy it. The variant with one native pair per runtime identifier and an ABI
check is [Per-RID payload from the deploy targets](./ship-native-libraries-with-a-mod-2.md); managed dependencies are
[Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md).

## Background: how the game does it

StarMap loads each mod into its own `ModAssemblyLoadContext`, which overrides only `Load` (managed assemblies: default
context, game context, imports from dependency mods, then the mod's `AssemblyDependencyResolver`). It does **not**
override `LoadUnmanagedDll`, so native imports fall back to the runtime's default probing:[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)

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

purrTTY's resolver comment says that default probing does not reliably search the assembly directory or
`runtimes/<rid>/native` in KSA's plugin context, so an explicit per-assembly resolver is used. `Assembly.Location` is
the mod folder because StarMap loads mod assemblies from a path.

## Lifecycle and timing

A `[ModuleInitializer]` runs the first time anything in the assembly is touched, before any P/Invoke in it. For purrTTY
that is during `StarMapAllModsLoaded` (when the first session or layout code touches the binding) or later on the first
terminal open, so the resolver is installed before the first native call, whatever thread triggers it
(`startup:all-mods-loaded`). The OS loader keeps the library loaded for the process lifetime; there is no unload path.

## Steps

1. **Package every platform's file flat in the output**, with the OS-specific names:[vendor/Ghostty.Vt/Ghostty.Vt.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/Ghostty.Vt.csproj#L59-L69)

   ```xml
   <!-- vendor/Ghostty.Vt/Ghostty.Vt.csproj L59-L69 -->
     <ItemGroup>
       <None Include="native/osx-arm64/libghostty-vt.dylib" Link="libghostty-vt.dylib">
         <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
       </None>
       <None Include="native/win-x64/ghostty-vt.dll" Link="ghostty-vt.dll">
         <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
       </None>
       <None Include="native/linux-x64/libghostty-vt.so" Link="libghostty-vt.so">
         <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
       </None>
     </ItemGroup>
   ```

   The mod's deploy target copies whichever exist next to the entry DLL into the mod folder. Flat names work while
   there is one RID per OS; for more, use a `runtimes/<rid>/native` layout, which the resolver also probes.
2. **Register a resolver from a `[ModuleInitializer]`, once per assembly.** `SetDllImportResolver` throws if called
   twice for the same assembly:[vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs#L25-L60)

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

3. **Choose the file name per OS and probe two locations** (beside the assembly, then `runtimes/<rid>/native`):[vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs#L62-L90)

   ```csharp
   // vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs L62-L90
       private static IEnumerable<string> CandidatePaths(string? assemblyDir, string fileName)
       {
           if (string.IsNullOrEmpty(assemblyDir))
           {
               yield break;
           }

           // 1. Directly beside the assembly (our default copy target).
           yield return Path.Combine(assemblyDir, fileName);

           // 2. NuGet-style runtimes/<rid>/native layout, in case a packaged copy lands there.
           var rid = RuntimeInformation.RuntimeIdentifier;
           yield return Path.Combine(assemblyDir, "runtimes", rid, "native", fileName);
       }

       private static string PlatformFileName()
       {
           if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
           {
               return "ghostty-vt.dll";
           }

           if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
           {
               return "libghostty-vt.dylib";
           }

           return "libghostty-vt.so";
       }
   ```

4. **Return `IntPtr.Zero` for names you do not own**, so default probing continues for everything else.
5. **Map `libc` explicitly.** `DllImport("libc")` alone fails on many glibc systems (the file `libc.so` is a linker
   script `dlopen` cannot load); purrTTY's Unix PTY assembly registers its own resolver, skipped on Windows:[purrTTY.Terminal/Pty/Process/UnixPtyNative.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyNative.cs#L30-L62)

   ```csharp
   // purrTTY.Terminal/Pty/Process/UnixPtyNative.cs L42-L61
       private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
       {
           if (libraryName != Libc)
           {
               return IntPtr.Zero; // fall through to default probing (e.g. kernel32 on Windows)
           }

           string[] candidates = OperatingSystem.IsMacOS()
               ? ["/usr/lib/libSystem.B.dylib", "libSystem.B.dylib"]
               : ["libc.so.6", "libc.so", "libc"];

           foreach (string candidate in candidates)
           {
               if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
               {
                   return handle;
               }
           }

           return IntPtr.Zero;
       }
   ```

6. **Exercise each OS** before release: a missing library surfaces as a `DllNotFoundException` at the first call
   (lazily), not at startup.

## Complete example

[vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs) (ghostty-vt resolver), [vendor/Ghostty.Vt/Ghostty.Vt.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/Ghostty.Vt.csproj) (flat copy), [purrTTY.Terminal/Pty/Process/UnixPtyNative.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/Process/UnixPtyNative.cs) (libc mapping).

## Variations and gotchas

- **One resolver per assembly.** Put every native library of that assembly in the same switch.
- **Ship only shared libraries**; static archives and Windows import libs cannot be loaded.
- **`Assembly.Location` is empty** for single-file or in-memory loads; StarMap loads from paths, so it is valid (the
  csproj suppresses `IL3000` for this reason).
- **No unload.** The library stays loaded for the process lifetime, matching StarMap only unloading mods at exit.
- **Side-by-side zips** need three files; if two platforms share a file name, switch to `runtimes/<rid>/native`.

## See also

- [Per-RID payload from the deploy targets](./ship-native-libraries-with-a-mod-2.md), [Ship and load native libraries from a mod folder](./overview.md)
- [Per-platform native library resolver for a mod's AssemblyLoadContext](../../../../mods/purrtty/features/per-platform-native-library-resolver.md), [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md)
- [Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md), [Ship assets and code in one mod folder and find it at runtime](../../loader/ship-content-and-code-in-one-mod/overview.md)
