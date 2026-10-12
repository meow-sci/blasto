---
type: Mod Feature
title: Ship and call native solver libraries from a mod
description: Load clarabel_c and scs from beside the mod assembly with a single module-initializer DllImportResolver per platform, bind them with layout-exact structs, reuse sparse CSC patterns across solves, and keep native calls on one thread per solver instance
mod: ksa-advancedflightcomputer
category: external-io
techniques: [native-interop, assembly-load-context, background-thread]
hooks: []
lifecycle: [frame:dispatch-solvers, thread:background]
game_types: []
tags: [native-interop, p-invoke, dllimport, clarabel, scs, assembly-load-context, sparse-matrix]
---

AFC's G-FOLD and SCvx guidance solve second-order cone programs with two native solvers built from
vendored sources: Clarabel (Rust, interior point, Apache-2.0) and SCS (C, ADMM, MIT). The mod folder
carries `clarabel_c.dll`/`scs.dll` for Windows and `libclarabel_c.so`/`libscs.so` for Linux next to the
managed `AdvancedFlightComputer.Guidance.Conic.dll`. Getting native code to load and behave inside a
StarMap mod comes down to three things: telling .NET where the library is, matching the C ABI exactly,
and keeping the calls on a thread you control.

## How it works

### Finding the native library

StarMap loads each mod into its own `ModAssemblyLoadContext`, which resolves managed dependencies
through an `AssemblyDependencyResolver` on the entry assembly but does not override
`LoadUnmanagedDll` ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)), so the
loader does nothing mod-specific for native libraries, and StarMap sets the process working directory
to the game folder ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L43)). AFC does not rely on the runtime's default P/Invoke probing at all: the `Conic`
assembly registers one resolver for itself in a `[ModuleInitializer]`, which runs before any of its code:
`NativeLibrary.SetDllImportResolver(assembly, ...)` maps the logical names `clarabel_c` and `scs` to the
platform file name and loads it from `Path.GetDirectoryName(asm.Location)`
([AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs)). Because
`SetDllImportResolver` throws when called twice for the same assembly, every native library of that
assembly must go through this one switch. Resolving next to the assembly also makes the same DLL work in
the console test runner and a test host.

### Managed satellite assemblies

The four numerical projects (`Numerics`, `Conic`, `Gfold`, `Scvx`) are separate assemblies in the mod
folder; StarMap's context finds them through the entry assembly's dependency resolver. (The repo's
`docs/guidance/runtime.md` says the guidance diagnostics block installs a managed assembly resolver; no
such resolver exists in the code at this commit, so that sentence appears to be stale.)

### Matching the ABI

- `ClarabelNative` and `ScsNative` declare `[DllImport(Lib, CallingConvention.Cdecl)]` entry points and
  sequential structs copied from the vendored C headers, which mirror Rust's `#[repr(C)]` types.
- Clarabel CSC indices are `uintptr_t` (`nuint`), not 32-bit ints like SCS; `ClarabelSolver` widens the
  `int[]` patterns before pinning.
- Rust/C `bool` is one byte, so every `bool` field carries `[MarshalAs(UnmanagedType.U1)]`; otherwise every
  field after the first bool is read at the wrong offset.
- Three Clarabel functions return structs by value (hidden return pointer on x64), another reason layouts
  must be exact. `DumpLayouts` prints .NET's computed layouts, and the build's `NativeChecks` compares
  them and the export counts against a C program compiled from the same headers (see
  [native solver build and ABI check](native-solver-build-and-abi-check.md))
  ([AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs#L1-L52)).
- Arrays are pinned with `GCHandle.Alloc(..., Pinned)` only for the duration of the native call; SCS deep
  copies its inputs in `scs_init`, so nothing stays pinned between solves
  ([AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs)).

### Reusing the sparse pattern

`CcsAssembler` builds the column-compressed pattern once from (row, col, value) triplets and then only
refills values: across SCvx iterations the non-zero structure is fixed and only the numbers change, which
removes most of the canonicalization cost a Python/CVXPY reference spends
([AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs), [AdvancedFlightComputer/Features/Guidance/Conic/SparseCcs.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/SparseCcs.cs)).
SCS warm starts at the iterate level (previous x, y, s), discarded when dimensions change.

### Threads

| Caller | Thread |
|---|---|
| G-FOLD (`GfoldPlanner` -> Clarabel) | Synchronous on the main thread inside the `PrepareWorker` prefix, with a per-solve time limit set immediately before each call (the limit is a static, safe only because the call is synchronous) |
| 6-DOF SCvx (SCS) | Dedicated background thread `afc-6dof-solve` per guided vehicle (see [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)) |
| Convex ascent (Clarabel) | Its own background thread per plan (see [convex ascent](convex-ascent-scvx.md)) |

A solver instance is never touched from two threads; inputs are copied into immutable requests before
dispatch.

## Lifecycle and timing

The module initializer runs when code in the `Conic` assembly is first used, which is the first time
guidance builds a solver (after `[StarMapAllModsLoaded]`, inside the guidance step); nothing native is
loaded if guidance never solves, and no StarMap hook is involved.
Native calls run on the main thread in `frame:dispatch-solvers` (G-FOLD) or on the mod's own threads
(`thread:background`), never on the game's vehicle workers.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs) | Module-initializer resolver |
| [AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs) | Clarabel P/Invoke surface and layouts |
| [AdvancedFlightComputer/Features/Guidance/Conic/ClarabelSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ClarabelSolver.cs) | Pinning, index widening, solve wrapper |
| [AdvancedFlightComputer/Features/Guidance/Conic/ScsNative.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ScsNative.cs) | SCS P/Invoke surface |
| [AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs) | SCS solve and iterate warm start |
| [AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs) | Fixed-pattern CSC refill |
| [AdvancedFlightComputer/Features/Guidance/Conic/AdvancedFlightComputer.Guidance.Conic.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/AdvancedFlightComputer.Guidance.Conic.csproj) | Separate game-free assembly |

## Snippets

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

## Game APIs touched

None. The interop layer is game-independent by design, so it can be tested without the game (see
[game-free guidance checks](game-free-guidance-checks.md)).

## Pitfalls and version notes

- One `SetDllImportResolver` per assembly; put every native library in the same resolver.
- Never let .NET marshal `bool` as 4 bytes for a C/Rust struct, and never pass 32-bit index arrays where
  the C API wants `uintptr_t`.
- `DllImport` resolution happens lazily on the first call; a missing library surfaces as
  `DllNotFoundException` at that call, so the build refuses to deploy or package without both natives.
- ADMM (SCS) tolerance is a time trade: the author measured 33 ms vs 1266 ms per receding-horizon cycle
  between `eps` 1e-5 and 1e-7 with the same answer.

## See also

- [G-FOLD powered descent](gfold-powered-descent.md)
- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Native solver build and ABI check](native-solver-build-and-abi-check.md)
- [Mod deploy MSBuild targets](mod-deploy-msbuild-targets.md)
- [Clarabel and SCS solvers with layout-exact structs, reused sparse CSC patterns and single-thread ownership](../../../howto/external-io/processes/bind-and-vendor-native-c-libraries/bind-and-vendor-native-c-libraries-2.md) - how-to article demonstrating this feature
- [Ship a per-RID native payload from the deploy targets and bind it with layout-exact structs](../../../howto/getting-started/dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-2.md) - how-to article demonstrating this feature

