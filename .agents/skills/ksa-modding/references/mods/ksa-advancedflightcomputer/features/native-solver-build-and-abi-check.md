---
type: Mod Feature
title: Build vendored native libraries for Windows and Linux and check the ABI against C#
description: Build Clarabel with cargo and SCS with zig cc from vendored sources with pinned, hash-verified toolchains, and compare the C# struct layouts and export list against a C probe compiled from the same headers on each target platform
mod: ksa-advancedflightcomputer
category: testing-and-tooling
techniques: [native-interop, msbuild-targets]
hooks: []
lifecycle: []
game_types: []
tags: [native-interop, build, rust, zig, abi, ci, cross-platform]
---

AFC ships two native solver libraries per platform, built from sources vendored under `third_party/`.
The build scripts and checks show how to ship native code in a KSA mod without a system toolchain zoo
and without silent ABI mismatches: Rust's cargo builds Clarabel's C library, Zig acts as a portable C
cross compiler for SCS, toolchains are pinned by hash, and a probe program proves that what .NET marshals
matches what C sees.

## How it works

### Clarabel (Rust)

`build/build-clarabel.ps1` runs `cargo build --locked --release --target <triple>` in
`third_party/clarabel/rust_wrapper`, a crate whose C library output is used directly (no CMake), and
copies `clarabel_c.dll` or `libclarabel_c.so` to `build/native/<rid>/`
([build/build-clarabel.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-clarabel.ps1)).

### SCS (C via Zig)

`build/build-scs.ps1` uses `zig cc -target <triple> -shared` with `-DCTRLC=0 -DNDEBUG`, leaving `DLONG`
and `SFLOAT` undefined (SCS tests whether they are defined, so defining them as 0 still changes the types)
to keep 32-bit ints and 64-bit floats in the C ABI ([build/build-scs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-scs.ps1)). Only `aa.c` and a small
`blas_shim.c` are compiled with `-DUSE_LAPACK`: SCS's Anderson acceleration is wrapped in
`#ifndef USE_LAPACK` and its fallback is a no-op, so building without LAPACK silently removed the
accelerator. The shim implements the few BLAS/LAPACK routines `aa.c` needs, avoiding a full BLAS
dependency ([build/native_src/blas_shim.c](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/native_src/blas_shim.c)).

### Pinned toolchains

`build/ci/install-native-tools.ps1` downloads Rust 1.90.0 (via rustup, verifying the rustup binary and
the Rust channel manifest by SHA-256) and Zig 0.14.1 (verified by SHA-256) into a temporary folder and
returns their paths, so local and CI builds use identical compilers ([build/ci/install-native-tools.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/install-native-tools.ps1)).

### ABI check

`build/NativeChecks` is a console program run on the target platform. It compiles
`native-layout.c` with Zig against the vendored headers; the C program `static_assert`s x64, 32-bit
`scs_int`, 64-bit `scs_float` and one-byte `bool`, then prints `sizeof` and `offsetof` for every struct and
field the bindings use. The C# side prints the same from `Marshal.SizeOf`/`OffsetOf` on the bindings in the
`Conic` assembly, and the tool compares both and the exported symbol list, requiring fixed counts
(Clarabel 62 layout checks and 5 exports, SCS 73 and 6) so a removed binding cannot pass unnoticed
([build/NativeChecks/native-layout.c](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/native-layout.c), [build/NativeChecks/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/Program.cs), [docs/guidance/native-build.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/native-build.md)).

### Layout and payload

`build/GuidanceNative.props` defines `build/native/<rid>/` and the per-platform file names; the payload
targets ship both runtimes in one mod folder because the file names differ (see
[mod deploy MSBuild targets](mod-deploy-msbuild-targets.md)). CI's native jobs build and check on Windows and
Linux and upload exactly two libraries per runtime; the `mod` job packages both.

## Lifecycle and timing

Build time and CI only. At run time the libraries are loaded by the
[native conic solver interop](native-conic-solver-interop.md) resolver.

## Key code

| Path | Role |
|---|---|
| [build/build-clarabel.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-clarabel.ps1) | Cargo build of Clarabel's C library |
| [build/build-scs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-scs.ps1) | Zig cross build of SCS with the BLAS shim |
| [build/native_src/blas_shim.c](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/native_src/blas_shim.c) | Minimal BLAS/LAPACK for Anderson acceleration |
| [build/ci/install-native-tools.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/install-native-tools.ps1) | Hash-pinned Rust and Zig |
| [build/NativeChecks/native-layout.c](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/native-layout.c) | C layout probe |
| [build/NativeChecks/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/Program.cs) | Managed-vs-native comparison |
| [.github/workflows/ci.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/ci.yml) | Native jobs per runtime |
| [THIRD-PARTY-NOTICES.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/THIRD-PARTY-NOTICES.md) | Licenses of vendored code |

## Snippets

```c
// build/NativeChecks/native-layout.c L1-L23
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include "scs.h"
#include "CscMatrix.h"
#include "DefaultSettings.h"
#include "DefaultSolution.h"
#include "SupportedConeT.h"

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
```

## Game APIs touched

None.

## Pitfalls and version notes

- ABI checks must run on the target platform; layouts can differ between platforms.
- A C macro tested with `#ifdef` changes behavior even when defined as 0.
- Removing an optional dependency (LAPACK) can silently disable a feature rather than fail to build;
  measure, then shim.
- Restore without `--runtime` and build with `--no-restore --runtime <rid>`; passing `--runtime` to a locked
  restore narrows the runtime set and fails with NU1004.

## See also

- [Native conic solver interop](native-conic-solver-interop.md)
- [Game-free guidance checks](game-free-guidance-checks.md)
- [CI reference assemblies](ci-reference-assemblies.md)
- [Clarabel and SCS solvers with layout-exact structs, reused sparse CSC patterns and single-thread ownership](../../../howto/external-io/processes/bind-and-vendor-native-c-libraries/bind-and-vendor-native-c-libraries-2.md) - how-to article demonstrating this feature
- [Ship a per-RID native payload from the deploy targets and bind it with layout-exact structs](../../../howto/getting-started/dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-2.md) - how-to article demonstrating this feature
- [Build native solver libraries with pinned toolchains and prove their ABI in per-platform CI jobs](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-3.md) - how-to article demonstrating this feature

