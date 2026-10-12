---
type: How-To
title: Clarabel and SCS solvers with layout-exact structs, reused sparse CSC patterns and single-thread ownership
description: Bind two native conic solvers with struct layouts copied from their C headers and proven by a C probe, one-byte bools, widened index arrays, per-call pinning and a rule that a solver instance is never touched by two threads
category: external-io
group: processes
topic: bind-and-vendor-native-c-libraries
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/native-conic-solver-interop
  - mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check
techniques: [native-interop, background-thread, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, thread:background]
game_types: []
tags: [pinvoke, clarabel, scs, abi, struct-layout, bool, uintptr, csc, pinning, threading]
---

Outcome: guidance code solves second-order cone programs with two native solvers, Clarabel (Rust, interior point) and SCS (C, ADMM), built from vendored sources and shipped beside the managed assembly. Getting native code to load and behave inside a StarMap mod comes down to three things: telling .NET where the library is, matching the C ABI exactly, and keeping the calls on a thread you control. Demonstrated by [Native conic solver interop](../../../../mods/ksa-advancedflightcomputer/features/native-conic-solver-interop.md) and [Native solver build and ABI check](../../../../mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check.md) in KSA-AdvancedFlightComputer. The loader side is general; see [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md).

## Background: how the game does it

StarMap loads each mod into its own `ModAssemblyLoadContext`, which resolves managed dependencies through an `AssemblyDependencyResolver` on the entry assembly but does not override `LoadUnmanagedDll` ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72)), so the loader does nothing mod-specific for natives, and StarMap sets the process working directory to the game folder ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L43-L43)). The mod therefore resolves its own native files.

## Lifecycle and timing

- The module initializer runs when code in the `Conic` assembly is first used, which is the first time guidance builds a solver (after `[StarMapAllModsLoaded]`, inside the guidance step); nothing native is loaded if guidance never solves.
- G-FOLD (Clarabel) runs synchronously on the main thread inside the `Vehicle.PrepareWorker` prefix in `frame:dispatch-solvers`, with a per-solve time limit set immediately before each call (the limit is a static, safe only because the call is synchronous).
- The 6-DOF SCvx solver (SCS) runs on a dedicated background thread per guided vehicle, and the convex ascent (Clarabel) on its own thread per plan (`thread:background`). Never on the game's vehicle workers. See [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md).

## Steps

1. **Register one resolver for the assembly, from a module initializer.** It maps the logical names to platform file names and loads from the assembly directory. Because `SetDllImportResolver` throws when called twice for one assembly, every native library of that assembly goes through this one switch; resolving next to the assembly also makes the same DLL work in a console test runner:

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

2. **Copy struct layouts from the vendored C headers, not from documentation.** Sequential structs mirror the headers, which mirror the Rust `#[repr(C)]` types, which are the real ABI. Two traps are specific to this binding: Clarabel's CSC dimensions and index arrays are `uintptr_t` (`nuint`), unlike SCS's 32-bit ints, and a Rust or C `bool` is one byte while .NET marshals `bool` as four by default:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs L47-L58
           public nuint N;          // columns
           public IntPtr ColPtr;    // const uintptr_t*, length n+1
           public IntPtr RowVal;    // const uintptr_t*, length colptr[n]
           public IntPtr NzVal;     // const double*,    length colptr[n]
       }

       /// <summary>Cone tags, from ClarabelSupportedConeT_Tag.</summary>
       internal enum ConeTag
       {
           Zero = 0,
           Nonnegative = 1,
           SecondOrder = 2,
   ```

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs L135-L147
       [StructLayout(LayoutKind.Sequential)]
       internal struct ClarabelDefaultSettings
       {
           public uint MaxIter;
           public double TimeLimit;
           [MarshalAs(UnmanagedType.U1)] public bool Verbose;
           public double MaxStepFraction;

           public double TolGapAbs;
           public double TolGapRel;
           public double TolFeas;
           public double TolInfeasAbs;
           public double TolInfeasRel;
   ```

   Every `bool` field carries `[MarshalAs(UnmanagedType.U1)]`; without it every field after the first bool is read at the wrong offset. Three Clarabel entry points return structs by value (a hidden return pointer on x64), another reason the layouts must be exact. Never construct a settings struct from scratch: call the library's default-settings function and modify it, because a zeroed struct disables tuned defaults silently.

3. **Widen the indices before pinning.** The sparse-matrix builder produces 32-bit `int[]` patterns; Clarabel wants `uintptr_t`, so they are widened into `nuint[]` first. Passing the int arrays directly would be read at twice the stride:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Conic/ClarabelSolver.cs L139-L146
           // WIDEN THE INDICES.
           // Clarabel's CSC arrays are uintptr_t; SparseCcs speaks the 32-bit ints ECOS and SCS both want.
           // Handing the int arrays over directly would have the native side read them at twice the stride.
           var colPtr = new nuint[jc.Length];
           for (int i = 0; i < jc.Length; i++) colPtr[i] = (nuint)jc[i];
           var rowVal = new nuint[ir.Length];
           for (int i = 0; i < ir.Length; i++) rowVal[i] = (nuint)ir[i];

   ```

4. **Pin only for the duration of the call.** Arrays are pinned with `GCHandle.Alloc(..., Pinned)` for the native call and released in a `finally`. SCS deep copies its inputs in `scs_init`, so nothing stays pinned between solves ([AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs#L1-L25), [AdvancedFlightComputer/Features/Guidance/Conic/ClarabelSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ClarabelSolver.cs#L157-L165)).

5. **Reuse the sparse pattern.** `CcsAssembler` builds the column-compressed pattern once from (row, column, value) triplets and then only refills values; across SCvx iterations the non-zero structure is fixed and only the numbers change, which removes most of the canonicalization cost of a Python reference. SCS also warm-starts at the iterate level (previous x, y, s), discarded when dimensions change ([AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs)).

6. **Give each solver instance one owning thread.** A solver instance is never touched from two threads; inputs are copied into immutable requests before dispatch, so a background solve never reads state the main thread is mutating.

7. **Prove the layouts with a C probe built from the same headers.** `NativeChecks` is a console program run on the target platform. It compiles a C file with Zig against the vendored headers; the C program `_Static_assert`s a 64-bit pointer, a 4-byte `scs_int`, an 8-byte `scs_float` and a one-byte `bool`, then prints `sizeof` and `offsetof` for every struct and field the bindings use:

   ```
   // build/NativeChecks/native-layout.c L9-L24

   _Static_assert(sizeof(void *) == 8, "An x64 target is required");
   _Static_assert(sizeof(scs_int) == 4, "DLONG must be undefined");
   _Static_assert(sizeof(scs_float) == 8, "SFLOAT must be undefined");
   _Static_assert(sizeof(bool) == 1, "The Clarabel ABI requires one-byte bool");

   #define SIZE(name, type) printf(#name ".size=%zu\n", sizeof(type))
   #define FIELD(name, type, managed, native) printf(#name "." #managed "=%zu\n", offsetof(type, native))

   int main(void) {
       SIZE(ScsMatrix, ScsMatrix);
       FIELD(ScsMatrix, ScsMatrix, X, x);
       FIELD(ScsMatrix, ScsMatrix, I, i);
       FIELD(ScsMatrix, ScsMatrix, P, p);
       FIELD(ScsMatrix, ScsMatrix, M, m);
       FIELD(ScsMatrix, ScsMatrix, N, n);
   ```

   The C# side prints the same from `Marshal.SizeOf` and `OffsetOf` on the bindings, and the tool compares both and the exported symbol list, requiring fixed counts so a removed binding cannot pass unnoticed ([build/NativeChecks/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/Program.cs#L1-L35)). Layouts can differ per platform, so the check runs on each target in CI.

8. **Build natives with pinned toolchains.** `cargo build --locked --release` for Clarabel's C library, `zig cc -shared` for SCS with a small BLAS shim (SCS's Anderson acceleration is wrapped in `#ifndef USE_LAPACK` and silently turns into a no-op without it), and Rust and Zig downloaded by hash so local and CI builds use identical compilers ([build/build-clarabel.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-clarabel.ps1), [build/build-scs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-scs.ps1), [build/ci/install-native-tools.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/install-native-tools.ps1)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/NativeLibraries.cs) | Module-initializer resolver |
| [AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ClarabelNative.cs) | Clarabel P/Invoke surface and layouts |
| [AdvancedFlightComputer/Features/Guidance/Conic/ClarabelSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ClarabelSolver.cs) | Pinning, index widening, solve wrapper |
| [AdvancedFlightComputer/Features/Guidance/Conic/ScsNative.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ScsNative.cs) | SCS P/Invoke surface |
| [AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/ScsWorkspace.cs) | SCS solve and iterate warm start |
| [AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Conic/CcsAssembler.cs) | Fixed-pattern CSC refill |
| [build/NativeChecks/native-layout.c](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/native-layout.c) | C layout probe |
| [build/NativeChecks/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/Program.cs) | Managed-versus-native comparison |

## Variations and gotchas

- **One `SetDllImportResolver` per assembly;** put every native library in the same resolver.
- **Never let .NET marshal `bool` as 4 bytes** for a C or Rust struct, and never pass 32-bit index arrays where the C API wants `uintptr_t`.
- **Resolution is lazy.** A missing library surfaces as `DllNotFoundException` at the first call, so the build refuses to deploy or package without both natives.
- **A feature flag can change the layout.** The Clarabel settings struct documents that enabling the `sdp` feature would add four fields and the binding must change with it.
- **Keep the interop layer game-independent** so it can be tested without the game.

## See also

- [Overview](overview.md)
- [Approach 1: vendored libghostty-vt](bind-and-vendor-native-c-libraries-1.md)
- [Native conic solver interop](../../../../mods/ksa-advancedflightcomputer/features/native-conic-solver-interop.md)
- [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md)
- [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md)
