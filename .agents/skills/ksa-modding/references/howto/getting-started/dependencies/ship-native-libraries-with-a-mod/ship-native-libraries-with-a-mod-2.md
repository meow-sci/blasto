---
type: How-To
title: Ship a per-RID native payload from the deploy targets and bind it with layout-exact structs
description: Build native solver libraries per runtime identifier, list them in one payload item list shared by deploy and release, load them with a resolver beside the assembly, and prove the C ABI matches the managed structs
category: getting-started
group: dependencies
topic: ship-native-libraries-with-a-mod
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/native-conic-solver-interop
  - mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets
  - mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check
techniques: [native-interop, msbuild, background-thread]
hooks: [reflection System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: []
tags: [native-interop, rid, msbuild, abi, clarabel, scs, payload, advancedflightcomputer]
---

KSA-AdvancedFlightComputer ships two native solvers per platform: Clarabel (Rust) and SCS (C), vendored and built from
source. The mod folder carries `clarabel_c.dll` and `scs.dll` for Windows and `libclarabel_c.so` and `libscs.so` for
Linux next to the managed `Conic` assembly. Getting native code to load and behave inside a StarMap mod comes down to
three things: telling .NET where the library is, shipping the right files for each runtime from one payload list, and
matching the C ABI exactly. The simpler, flat-file variant is [Flat per-OS file names with a libc mapping](./ship-native-libraries-with-a-mod-1.md).

## Background: how the game does it

StarMap's mod context resolves managed dependencies through an `AssemblyDependencyResolver` on the entry assembly but
does not override `LoadUnmanagedDll`, so it does nothing mod-specific for native libraries; StarMap only sets the
process working directory to the game folder.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)[StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L56) AFC therefore does not rely on default P/Invoke probing at
all: the `Conic` assembly registers one resolver for itself in a `[ModuleInitializer]`, which runs before any of its
code. `SetDllImportResolver` throws when called twice for the same assembly, so every native library of that assembly
must go through this one switch.

## Lifecycle and timing

The module initializer runs when code in the `Conic` assembly is first used, which is the first time guidance builds a
solver (after `StarMapAllModsLoaded`, inside the guidance step); nothing native loads if guidance never solves, and no
StarMap hook is involved. Native calls run on the main thread in `frame:dispatch-solvers` (G-FOLD, with a per-solve time
limit set just before each synchronous call) or on the mod's own threads (`thread:background`: `afc-6dof-solve`
per guided vehicle), never on the game's vehicle workers. A solver instance is never touched from two threads; inputs
are copied into immutable requests before dispatch.

## Steps

1. **Bind by logical name, resolve beside the assembly.** The resolver maps `clarabel_c` and `scs` to the OS file and
   loads it from `Path.GetDirectoryName(asm.Location)`:[AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs#L15-L37)

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs L15-L37
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
   }
   ```

2. **Keep the layout knowledge in one props file.** Natives are staged under `build/native/<rid>/`; a local build ships
   the host runtime only, a release passes both, so a missing cross build cannot fail an ordinary deployment:[build/GuidanceNative.props](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidanceNative.props)

   ```xml
   <!-- build/GuidanceNative.props L6-L14 -->
       <GuidanceNativeDir>$(MSBuildThisFileDirectory)native/</GuidanceNativeDir>

       <GuidanceHostRid Condition="'$(GuidanceHostRid)' == '' and '$(OS)' == 'Windows_NT'">win-x64</GuidanceHostRid>
       <GuidanceHostRid Condition="'$(GuidanceHostRid)' == ''">linux-x64</GuidanceHostRid>

       <!-- Which runtimes the payload carries. A local build ships the host runtime
            only, so a missing cross build cannot fail an ordinary deployment. A
            release build passes both, for example -p:GuidanceNativeRids=win-x64;linux-x64 -->
       <GuidanceNativeRids Condition="'$(GuidanceNativeRids)' == ''">$(GuidanceHostRid)</GuidanceNativeRids>
   ```

3. **List one payload that both deploy and package use**, with distinct file names per OS so one mod folder can hold
   both runtimes, and fail early when a required library is missing:[build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets#L1-L33)

   ```xml
   <!-- build/GuidancePayload.targets L13-L33 -->
     <ItemGroup Condition="$(GuidanceNativeRids.Contains('win-x64'))">
       <GuidanceNativeFile Include="$(GuidanceNativeDir)win-x64/clarabel_c.dll" TargetPath="clarabel_c.dll" />
       <GuidanceNativeFile Include="$(GuidanceNativeDir)win-x64/scs.dll" TargetPath="scs.dll" />
     </ItemGroup>
     <ItemGroup Condition="$(GuidanceNativeRids.Contains('linux-x64'))">
       <GuidanceNativeFile Include="$(GuidanceNativeDir)linux-x64/libclarabel_c.so" TargetPath="libclarabel_c.so" />
       <GuidanceNativeFile Include="$(GuidanceNativeDir)linux-x64/libscs.so" TargetPath="libscs.so" />
     </ItemGroup>

     <ItemGroup>
       <ModPayloadFile Include="@(GuidanceNativeFile)" />
       <ModPayloadFile Include="$(MSBuildThisFileDirectory)../LICENSE" TargetPath="LICENSE" />
       <ModPayloadFile Include="$(MSBuildThisFileDirectory)../THIRD-PARTY-NOTICES.md" TargetPath="THIRD-PARTY-NOTICES.md" />
       <ModPayloadFile Include="$(MSBuildThisFileDirectory)../licenses/*.txt" TargetPath="licenses/%(Filename)%(Extension)" />
     </ItemGroup>

     <Target Name="ValidateGuidanceRuntimeFiles" BeforeTargets="PrepareForBuild"
             Condition="'$(Configuration)' == 'Release' OR '$(DeployToMods)' == 'true'">
       <Error Condition="!Exists('%(GuidanceNativeFile.Identity)')"
              Text="%(GuidanceNativeFile.Identity) is required for deployment or Release. Build it with build/build-clarabel.ps1 or build/build-scs.ps1 for that runtime, or use Debug with -p:DeployToMods=false for a managed compile check." />
     </Target>
   ```

   The same list drives the release zip and its verification (see [Explicit payload list shared by deploy and packaging](../../build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md) and
   [Payload list verified file by file](../../build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md)).
4. **Build natives with pinned toolchains.** Clarabel's C library comes from `cargo build --locked --release --target
   <triple>`; SCS is compiled with `zig cc -target <triple> -shared`, leaving `DLONG` and `SFLOAT` undefined so the
   ABI keeps 32-bit ints and 64-bit floats. The toolchains (Rust 1.90.0, Zig 0.14.1) are downloaded and
   SHA-256-verified by `build/ci/install-native-tools.ps1`, so local and CI builds match.
5. **Match the ABI exactly.** `ClarabelNative` and `ScsNative` declare `[DllImport(Lib, CallingConvention.Cdecl)]`
   entry points and sequential structs copied from the vendored C headers. Clarabel CSC indices are `uintptr_t`
   (`nuint`), not 32-bit ints like SCS, and every `bool` field carries `[MarshalAs(UnmanagedType.U1)]` (Rust/C `bool` is
   one byte), otherwise every field after the first bool is read at the wrong offset. Pin arrays with
   `GCHandle` only for the duration of the call.
6. **Prove it in the build.** `build/NativeChecks` compiles a C program from the same headers that `static_assert`s the
   scalar sizes and prints `sizeof`/`offsetof` for every struct the bindings use, compares them with
   `Marshal.SizeOf`/`OffsetOf` and the exported symbol lists, and requires fixed counts (Clarabel 62 layout checks and 5
   exports, SCS 73 and 6) so a removed binding cannot pass unnoticed.
7. **Own the threads.** Dedicated background threads for long solves; copy inputs into immutable requests; never call
   a solver instance from two threads.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs) | The single resolver |
| [build/GuidanceNative.props](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidanceNative.props), [build/GuidancePayload.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/GuidancePayload.targets) | RID layout and the shared payload list |
| [AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs) | Layout-exact bindings (first 52 lines) |
| [build/build-clarabel.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-clarabel.ps1), [build/build-scs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-scs.ps1) | Native builds |

## Variations and gotchas

- **One `SetDllImportResolver` per assembly**; put every native library in the same switch.
- **Never let .NET marshal `bool` as 4 bytes** for a C/Rust struct, and never pass 32-bit index arrays where the C API
  wants `uintptr_t`.
- **`DllImport` resolution is lazy**; a missing library surfaces as `DllNotFoundException` at the first call, hence the
  build-time `ValidateGuidanceRuntimeFiles`.
- **The deploy target assumes Windows paths** (`%USERPROFILE%`); the native layout supports Linux builds, but the
  deploy as written does not.
- **ADMM tolerance is a time trade**: the author measured 33 ms versus 1266 ms per receding-horizon cycle between `eps`
  1e-5 and 1e-7 with the same answer.

## See also

- [Flat per-OS file names with a libc mapping](./ship-native-libraries-with-a-mod-1.md), [Ship and load native libraries from a mod folder](./overview.md)
- [Ship and call native solver libraries from a mod](../../../../mods/ksa-advancedflightcomputer/features/native-conic-solver-interop.md), [MSBuild deploy and packaging for a StarMap mod with native payload](../../../../mods/ksa-advancedflightcomputer/features/mod-deploy-msbuild-targets.md),
  [Build vendored native libraries for Windows and Linux and check the ABI against C#](../../../../mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check.md)
- [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md), [Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md)
