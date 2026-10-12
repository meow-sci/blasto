---
type: How-To
title: Build native solver libraries with pinned toolchains and prove their ABI in per-platform CI jobs
description: Build a Rust C library with cargo and a C library with zig cc under hash-pinned compilers, then compare sizeof and offsetof from a C probe with Marshal.SizeOf and OffsetOf on the managed bindings, with fixed expected counts so a removed binding cannot pass
category: testing-and-tooling
group: offline-tests
topic: run-repo-checks-from-a-pre-push-hook-and-ci
approach: 3
difficulty: advanced
prerequisites: [powershell, github-actions, rust, zig]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check]
techniques: [github-actions, native-interop, abi-check]
hooks: []
lifecycle: [thread:background]
game_types: []
tags: [testing, ci, native, abi, zig, cargo, pinned-toolchains, per-platform]
---

Outcome: CI builds each native library on the platform it ships for, runs a managed-versus-native layout check that fails on any struct size or field offset mismatch, runs the solver console checks against the freshly built libraries, and uploads exactly two libraries per runtime.
Demonstrated by KSA-AdvancedFlightComputer, which ships Clarabel (Rust) and SCS (C via Zig) for Windows and Linux ([build/ci/test-guidance.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/test-guidance.ps1#L127-L142)). Prerequisites: native sources vendored in the repository, a managed binding with explicit layouts, and a GitHub Actions matrix with one job per runtime.

## Background: how the game does it

KSA mods are .NET assemblies loaded by StarMap; native code is a mod-side choice. The mod resolves its libraries itself (see [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md)), so a mismatch between what .NET marshals and what C sees is invisible until a solve returns garbage in flight. The check therefore belongs in CI, on the target platform, before a package is built.

## Lifecycle and timing

Build time and CI only. At run time the libraries are loaded by the mod's native resolver; the solver itself runs on a background thread (`thread:background`) fed from the vehicle step. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for where that dispatch sits.

## Steps

1. **Build the Rust library with cargo, no CMake.** The crate's C library output is used directly; `build-clarabel.ps1` picks the triple per runtime and copies the library to `build/native/<rid>/`:

   ```powershell
   # build/build-clarabel.ps1 L1-L21
   # Builds Clarabel's C library from third_party/clarabel/rust_wrapper.
   # Cargo produces the C library directly, so this build does not need CMake.
   # Cross builds need the selected Rust target and a linker for that platform.

   [CmdletBinding()]
   param(
       [string]$CargoExe = "cargo",
       [ValidateSet("win-x64", "linux-x64")]
       [string]$Rid = $(if ($env:OS -eq "Windows_NT") { "win-x64" } else { "linux-x64" }),
       [string]$OutDir
   )

   Set-StrictMode -Version Latest
   $ErrorActionPreference = "Stop"

   $runtimes = @{
       "win-x64"   = @{ Triple = "x86_64-pc-windows-msvc";   Library = "clarabel_c.dll" }
       "linux-x64" = @{ Triple = "x86_64-unknown-linux-gnu"; Library = "libclarabel_c.so" }
   }
   $triple = $runtimes[$Rid].Triple
   $library = $runtimes[$Rid].Library
   ```

2. **Build the C library with `zig cc` as a portable cross compiler.** Leave `DLONG` and `SFLOAT` undefined (SCS tests whether they are defined, so defining them as 0 still changes the types) to keep 32-bit ints and 64-bit floats; `CTRLC=0` leaves signal handling to the host; compile only `aa.c` and a small BLAS shim with `USE_LAPACK`, because SCS's Anderson acceleration is a silent no-op without it:

   ```powershell
   # build/build-scs.ps1 L1-L6
   # Builds SCS from third_party/scs with Zig for Windows or Linux.
   # Leave DLONG and SFLOAT undefined to keep 32-bit integers and 64-bit floats in the C ABI.
   # Defining either macro as 0 still changes the types because SCS tests whether it is defined.
   # CTRLC=0 leaves signal handling to the host process, and NDEBUG disables debug output.
   # Anderson acceleration uses aa.c with the small BLAS shim in native_src/blas_shim.c.
   # Compile only those two files with USE_LAPACK to avoid a full BLAS/LAPACK dependency.
   ```

   ```powershell
   # build/build-scs.ps1 L23-L26, L61-L68
   $runtimes = @{
       "win-x64"   = @{ Target = "x86_64-windows-gnu"; Library = "scs.dll";   Pic = @() }
       "linux-x64" = @{ Target = "x86_64-linux-gnu";   Library = "libscs.so"; Pic = @("-fPIC") }
   }
   # ...
   Push-Location -LiteralPath $scs
   try {
       $aaArgs = @("cc", "-target", $target, "-c") + $pic +
           $opt.Split(" ") +
           @("-DCTRLC=0", "-DNDEBUG", "-DUSE_LAPACK") +
           $includes + @("src/aa.c", "-o", $aaObj)
       & $ZigExe @aaArgs
       if ($LASTEXITCODE -ne 0) { throw "zig cc failed compiling aa.c with exit code $LASTEXITCODE" }
   ```

3. **Pin the toolchains by hash.** The install script refuses any version but the one whose official hashes it contains, downloads to a partial file and verifies SHA-256 before use, so local and CI builds use identical compilers:

   ```powershell
   # build/ci/install-native-tools.ps1 L71-L89
   function Get-VerifiedDownload {
       param(
           [Parameter(Mandatory = $true)][uri]$Uri,
           [Parameter(Mandatory = $true)][string]$Destination,
           [Parameter(Mandatory = $true)][string]$Sha256
       )

       if (-not (Test-Path -LiteralPath $Destination)) {
           $partial = "$Destination.download"
           Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
           Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $partial
           Move-Item -LiteralPath $partial -Destination $Destination
       }

       $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
       if ($actual -ne $Sha256) {
           throw "The SHA-256 hash for $Destination is $actual, but the pinned official hash is $Sha256."
       }
   }
   ```

4. **Write a C probe that asserts the assumptions and prints every layout.** `_Static_assert` states the platform and type-width facts; `SIZE` and `FIELD` macros print `sizeof` and `offsetof` for every struct and field the bindings use:

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

5. **Compare it with the managed side.** The check compiles the probe with Zig against the vendored headers, runs it, then loads the binding assembly and compares `Marshal.SizeOf` and `Marshal.OffsetOf` for every nested value type. It refuses to run for a non-host runtime, because layouts can differ between platforms:

   ```csharp
   // build/NativeChecks/Program.cs L69-L87
   try
   {
       (string zigTarget, string clarabelName, string scsName) = Runtime(rid);
       if (rid != HostRid())
           throw new InvalidOperationException(
               $"The layout probe has to run as a {rid} process, so run this check on {rid}.");

       string workDir = Path.Combine(AppContext.BaseDirectory, "probe");
       Directory.CreateDirectory(workDir);
       string probe = Path.Combine(workDir, OperatingSystem.IsWindows() ? "native-layout.exe" : "native-layout");
       string probeSource = Path.Combine(repoRoot, "build", "NativeChecks", "native-layout.c");

       Run(zig, ["cc", "-target", zigTarget, "-std=c11", "-I" + scsInclude, "-I" + clarabelInclude, probeSource, "-o", probe], workDir,
           "The check compiles its probe with Zig. Put zig on the PATH or name it with --zig, see docs/guidance/native-build.md.");
       Dictionary<string, int> layout = Run(probe, [], workDir)
           .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(line => line.Contains('='))
           .Select(line => line.Split('='))
           .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]));
   ```

   ```csharp
   // build/NativeChecks/Program.cs L98-L119
       int checks = 0;
       void Equal(string key, int actual)
       {
           if (!layout.TryGetValue(key, out int expected))
               throw new InvalidOperationException($"The probe printed no value for {key}, so the binding has a type the headers do not.");
           if (actual != expected)
               throw new InvalidOperationException($"ABI mismatch for {key}, managed {actual}, native {expected}");
           checks++;
       }

       foreach (Type type in binding.GetNestedTypes(BindingFlags.NonPublic)
           .Where(type => type.IsValueType && !type.IsEnum && !type.IsDefined(typeof(CompilerGeneratedAttribute))))
       {
           Equal(type.Name + ".size", Marshal.SizeOf(type));
           foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
               Equal(type.Name + "." + field.Name, checked((int)Marshal.OffsetOf(type, field.Name)));
       }
       if (expectedChecks >= 0 && checks != expectedChecks)
           throw new InvalidOperationException(
               $"{library} ran {checks} size and field-offset checks on {rid}, but {expectedChecks} were expected. "
               + "Review the bindings before updating the expected count.");
       Console.WriteLine($"{library} ABI passed on {rid}, {checks} size and field-offset checks");
   ```

6. **Require fixed counts.** The script passes the expected number of layout checks and exports, so a removed binding (which would otherwise simply stop being compared) fails the job. The comment tells the reader to review binding changes before updating the numbers:

   ```powershell
   # build/ci/test-guidance.ps1 L135-L142
   # Fixed counts catch removed bindings that the layout and export checks would no longer see.
   # Review binding changes before updating these values.
   $expectedClarabelChecks = 62
   $expectedClarabelExports = 5
   $expectedScsChecks = 73
   $expectedScsExports = 6
   Invoke-Checked -FilePath "dotnet" -ArgumentList ($nativeCheckBase + @("clarabel", $conicAssembly,"--rid", $RuntimeIdentifier, "--zig", $ZigExe, "--expect-checks", $expectedClarabelChecks, "--expect-exports", $expectedClarabelExports)) -FailureMessage "The Clarabel ABI check failed."
   Invoke-Checked -FilePath "dotnet" -ArgumentList ($nativeCheckBase + @("scs", $conicAssembly,"--rid", $RuntimeIdentifier, "--zig", $ZigExe, "--expect-checks", $expectedScsChecks, "--expect-exports", $expectedScsExports)) -FailureMessage "The SCS ABI check failed."
   ```

7. **Run the solver checks against the fresh natives.** The same script then copies the libraries beside each console and runs every flag ([Check numerics against an independent implementation](../unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-3.md)), and finally asserts the artifact directory holds exactly the two expected files.

8. **Define one job per runtime in the workflow.** A matrix of `windows-latest` and `ubuntu-latest` installs the pinned tools, runs the script, and uploads the two libraries; a later `mod` job downloads both runtimes and packages them together:

   ```yaml
   # .github/workflows/ci.yml L15-L27, L42-L63
     test:
       name: native (${{ matrix.runtime }})
       runs-on: ${{ matrix.os }}
       timeout-minutes: 45
       strategy:
         fail-fast: false
         matrix:
           include:
             - os: windows-latest
               runtime: win-x64
             - os: ubuntu-latest
               runtime: linux-x64
       env:
   # ...
         - name: Install native tools
           shell: pwsh
           run: >-
             ./build/ci/install-native-tools.ps1
             -InstallRoot '${{ runner.temp }}/guidance-tools'
             -RuntimeIdentifier '${{ matrix.runtime }}'

         - name: Build and test guidance
           shell: pwsh
           run: >-
             ./build/ci/test-guidance.ps1
             -RepositoryRoot '${{ github.workspace }}'
             -RuntimeIdentifier '${{ matrix.runtime }}'
             -Configuration Release
             -ArtifactsRoot '${{ runner.temp }}/guidance-artifacts'

         - name: Upload native libraries
           uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
           with:
             name: guidance-native-${{ matrix.runtime }}
             path: ${{ runner.temp }}/guidance-artifacts/${{ matrix.runtime }}
             if-no-files-found: error
   ```

## Complete example

| File | Role |
|---|---|
| [build/build-clarabel.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-clarabel.ps1) | Cargo build of Clarabel's C library |
| [build/build-scs.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/build-scs.ps1) | Zig cross build of SCS with the BLAS shim |
| [build/ci/install-native-tools.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/install-native-tools.ps1) | Hash-pinned Rust and Zig |
| [build/NativeChecks/native-layout.c](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/native-layout.c) | C layout probe |
| [build/NativeChecks/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/NativeChecks/Program.cs) | Managed-versus-native comparison |
| [build/ci/test-guidance.ps1](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ci/test-guidance.ps1) | Local and CI runner |
| [.github/workflows/ci.yml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/.github/workflows/ci.yml) | Native jobs per runtime |

## Variations and gotchas

- **Run it on the target.** The probe has to run as a process of the runtime under test; the scripts throw when the host does not match.
- **A shim is cheaper than a BLAS.** Check what a macro-guarded fallback does without the dependency; here it was a silent no-op.
- **Locked restores.** The restore step uses locked mode against lock files, then builds with `--no-restore --runtime <rid>` to avoid NU1004.
- **Counts are a contract.** Update `--expect-checks` only after reviewing the binding diff.
- **Game updates.** None of this depends on a KSA build; native updates are driven by the vendored library versions.

## See also

- [Overview](overview.md); [approach 2](run-repo-checks-from-a-pre-push-hook-and-ci-2.md), [approach 4](run-repo-checks-from-a-pre-push-hook-and-ci-4.md)
- [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md), [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md)
- Mod feature: [AFC native solver build and ABI check](../../../../mods/ksa-advancedflightcomputer/features/native-solver-build-and-abi-check.md)
