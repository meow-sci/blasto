---
type: Game Feature
title: Bind and vendor native C libraries with layout-exact P/Invoke
description: Call a C or Rust library from a KSA mod safely with layout-exact structs checked against the headers, a startup layout self-check, thread ownership rules, per-RID builds, a vendored and patched upstream binding and the Apple arm64 variadic-call pitfall
category: external-io
group: processes
topic: bind-and-vendor-native-c-libraries
demonstrated_by:
  - mods/purrtty/features/vendored-libghostty-vt-binding
  - mods/ksa-advancedflightcomputer/features/native-conic-solver-interop
  - mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check
  - mods/purrtty/features/posix-pty-spawn-without-fork
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers, thread:background]
game_types: []
tags: [pinvoke, native, interop, abi, vendoring, libraryimport, structs, variadic, rid]
---

Some jobs are better done by an existing native library than by a C# port: a terminal emulation engine, a convex solver, a compression codec. KSA mods can bind them with P/Invoke, but the managed/native boundary has failure modes that look like nothing at the call site: structs read at the wrong offset, a `bool` marshalled as four bytes, a span that points at a freed stack buffer, a variadic call whose last argument lands in the wrong place on one CPU. The three approaches here are the defences the reference mods use:

- **Vendor the binding, mark local changes, and fail loudly on drift.** A copied upstream binding is pruned, patched where it has lifetime bugs, and guarded by a startup self-check that compares the managed decode with the native accessors.
- **Make layouts exact and prove them.** Structs are copied from the headers, `bool` fields are one byte, index arrays are the width the C API wants, and a probe compiled from the same headers compares `sizeof` and `offsetof` with what .NET computes.
- **Know the ABI traps that no header shows.** For example, variadic functions on Apple arm64.

Loading the library from the mod folder (StarMap gives each mod its own `AssemblyLoadContext` and does not resolve natives for you) is covered in [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md).

## Engine background

KSA has no native-interop facility; the facts that matter are about loading and threading.

| Fact | Consequence |
|---|---|
| Each mod loads in its own `ModAssemblyLoadContext`, which does not override `LoadUnmanagedDll` | Register a `DllImportResolver` for the binding's assembly (from a `[ModuleInitializer]`) and load from the assembly directory |
| One `SetDllImportResolver` per assembly | Every native library of that assembly goes through one switch |
| `Vehicle.PrepareWorker` and the ImGui draw run on the main thread; solver workers run elsewhere | A native handle must have one owning thread; do not share it between the main thread and a background solve |
| Lazy P/Invoke resolution | A missing library surfaces as `DllNotFoundException` at the first call, not at load |

See [the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md) for the thread side.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: vendored libghostty-vt binding with managed packed-cell decoding and a startup self-check](bind-and-vendor-native-c-libraries-1.md) | purrtty | `native-interop` | `startup:all-mods-loaded`, `frame:draw-ui` | A whole engine you do not have to write; you now own a fork of the binding and three native builds |
| [2: Clarabel and SCS solvers with layout-exact structs, reused sparse patterns and single-thread ownership](bind-and-vendor-native-c-libraries-2.md) | ksa-advancedflightcomputer | `native-interop` | `frame:dispatch-solvers`, `thread:background` | Fast convex solves; hand-written layouts that must be checked per platform |
| [3: pad register arguments for variadic C calls on Apple arm64](bind-and-vendor-native-c-libraries-3.md) | purrtty | `native-interop` | `thread:background` | A small, searchable fix for one platform; easy to miss because Linux and Windows work |

## See also

- [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md), [Ship third-party DLLs with a mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)
- [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md)
- [Host and supervise an external process or virtual machine from a KSA mod](../host-and-supervise-an-external-process-from-a-mod/overview.md)
