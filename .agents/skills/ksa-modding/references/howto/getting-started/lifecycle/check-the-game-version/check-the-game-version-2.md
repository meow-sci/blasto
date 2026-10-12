---
type: How-To
title: Stamp the targeted KSA build into assembly metadata and compare it at run time
description: Write the KSA build a mod was compiled against into AssemblyMetadata from a lock file at build time, read it back at startup and log it beside the running version
category: getting-started
group: lifecycle
topic: check-the-game-version
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/mod-log-file-with-rotation
  - mods/ksacars/features/mod-log-file
  - mods/ksarmory/features/own-log-file-and-developer-switch
techniques: [msbuild, starmap-lifecycle, reflection]
hooks: [starmap StarMapImmediateLoad, reflection System.Reflection.AssemblyMetadataAttribute]
lifecycle: [startup:immediate-load]
game_types: [KSA.Vehicle]
tags: [version-check, msbuild, assembly-metadata, lock-file, build-stamp]
---

The runtime check of [Runtime VersionInfo compare at AllModsLoaded](./check-the-game-version-1.md) compares against a constant in the code. KSAGolf, KSACars and
KSArmory keep the targeted build in a **lock file** (`ksa-assemblies.lock`, which also pins SHA-256 hashes of the
reference DLLs) and have the csproj stamp it into `AssemblyMetadata` at build time. At startup the mod reads the
stamp and the running game's assembly version and logs "built for X, running Y". The build is therefore written
down once, the report always carries both, and the same file can drive CI.

## Background: how the game does it

KSA itself exposes its version through `VersionInfo.Current` (see [Runtime VersionInfo compare at AllModsLoaded](./check-the-game-version-1.md)), and the build string
appears in `current/version.json` (`"build": "2026.10.10.5554"`).[current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json) These mods read the running version
differently: `typeof(KSA.Vehicle).Assembly.GetName().Version`, i.e. the `KSA` assembly's own `Version`.[src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs#L40-L80) For build
5554 the two agree: `AssemblyName.GetAssemblyName` on `current/dll/KSA.dll` returns `2026.10.10.5554`, and the
assembly's informational version is `2026.10.10.5554+<commit>` (checked against the shipped DLL; the decompiled
sources carry no assembly attributes). KSArmory's `ReportDraft.GameIsSupported` trims trailing `.0` components
before comparing and treats a null or empty running version as supported.

## Lifecycle and timing

Build time: the csproj reads the lock file when MSBuild evaluates properties. Run time: the stamp is read in static
initializers of `Build` on first access, which is the `loading` log line in `[StarMapImmediateLoad]`
(`startup:immediate-load`); nothing else is needed from the game, so it could equally run at `AllModsLoaded`.

## Steps

1. **Keep a one-line pin in a lock file** with the build the code was verified against:[ksa-assemblies.lock](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/ksa-assemblies.lock)

   ```text
   # ksa-assemblies.lock L1-L4
   # KSA assemblies this repository is known to build against.
   # Regenerate with ./tools/check-assemblies.sh <dir> --update after a KSA update,
   # and refresh the private ksa-game-assemblies repo to match - see CLAUDE.md.
   build 2026.9.22.5482
   ```

   The remaining lines are `sha256  DllName` pairs that the repo's check script compares with the installed game.
2. **Stamp it in the csproj**, only when the lock file exists:[src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj#L79-L94)

   ```xml
   <!-- src/KSAGolf/KSAGolf.csproj L79-L94 (the KsaBuildLine regex property is elided) -->
     <!--
       The KSA build this was compiled against, taken from ksa-assemblies.lock rather than written
       down a second time. The log compares it to the build actually running.
     -->
     <PropertyGroup>
       <KsaLockPath>$(MSBuildThisFileDirectory)../../ksa-assemblies.lock</KsaLockPath>
       <!-- ... KsaBuildLine: regex match of the `build` line in the lock file ... -->
       <KsaBuild Condition="'$(KsaBuildLine)' != ''">$(KsaBuildLine.Substring(6).Trim())</KsaBuild>
     </PropertyGroup>

     <ItemGroup Condition="'$(KsaBuild)' != ''">
       <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
         <_Parameter1>KsaBuild</_Parameter1>
         <_Parameter2>$(KsaBuild)</_Parameter2>
       </AssemblyAttribute>
     </ItemGroup>
   ```

   (`KsaBuildLine` is a `Regex.Match` property function over the file text, selecting the line that begins with
   `build` followed by a digit; `Substring(6)` drops the word `build` and the space.) KSACars and KSArmory carry
   the same block.[src/KSACars/KSACars.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACars.csproj#L85-L100)
3. **Read both versions back:**[src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs#L48-L77)

   ```csharp
   // src/KSAGolf/Ksa/Build.cs L50-L77
       private static string? ResolveKsaRunning()
       {
           try
           {
               return typeof(KSA.Vehicle).Assembly.GetName().Version?.ToString();
           }
           catch
           {
               return null;
           }
       }

       private static string? ResolveKsaBuild()
       {
           try
           {
               foreach (AssemblyMetadataAttribute meta in typeof(Build).Assembly
                            .GetCustomAttributes<AssemblyMetadataAttribute>())
               {
                   if (meta.Key == "KsaBuild") return meta.Value;
               }

               return null;
           }
           catch
           {
               return null;
           }
       }
   ```

4. **Log them at immediate load** so every bug report starts with both:[src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L32-L37)

   ```csharp
   // src/KSAGolf/Ksa/KSAGolfMod.cs L32-L37
       [StarMapImmediateLoad]
       public void OnImmediateLoad(Mod mod)
       {
           Log.Info($"loading (mod id: {mod.Id})");
           Log.Info($"KSAGolf {Build.Version} built for KSA {Build.KsaBuild ?? "?"}, running {Build.KsaRunning ?? "?"}");
       }
   ```

5. **Use the pair to gate optional features** if you want: KSArmory only offers bug reporting when built and running
   builds match (`ReportDraft.GameIsSupported(Build.KsaBuild, Build.KsaRunning)`), and logs `reporting off` otherwise.
   Core behaviour should not be gated this way ([Runtime VersionInfo compare at AllModsLoaded](./check-the-game-version-1.md), gotchas).
6. **Update the lock when you re-test**, regenerate the hashes, and let CI read the same file
   ([Build a mod in CI when the game DLLs are private](../../build/build-in-ci-without-the-game-installed/overview.md)).

## Complete example

| File | Role |
|---|---|
| [ksa-assemblies.lock](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/ksa-assemblies.lock) | The pinned build |
| [src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj) | Stamp |
| [src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs) | `Version`, `KsaBuild`, `KsaRunning` |
| [src/KSArmory/Ksa/Build.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Build.cs) | Same class, plus the `Developer` marker ([Constants.LogsFolderPath and a developer marker file](../write-a-per-mod-log-file/write-a-per-mod-log-file-2.md)) |

## Variations and gotchas

- **Built for 5482, tested on 5554.** KSAGolf's lock says `2026.9.22.5482`; the build analysed here is
  `2026.10.10.5554`, where the `Mod` and `ModLibrary` members it uses are unchanged in shape. The stamp records what
  the author verified, not what the player runs.
- **The stamp is only as good as the lock.** A forgotten lock update lets the log claim an old build.
- **MSBuild property functions** (`Regex.Match`, `File.ReadAllText`) run at evaluation time; a missing lock file
  leaves no attribute and `KsaBuild` is null, hence the `"?"`.
- **Assembly `Version` versus build string** agree for build 5554 (see Background), but nothing guarantees it for
  future builds; log both raw values rather than compare them blindly.

## See also

- [Runtime VersionInfo compare at AllModsLoaded](./check-the-game-version-1.md), [Warn when the running game build differs from the one a mod was tested on](./overview.md)
- [Mod log file with rotation and a verbose toggle](../../../../mods/ksagolf/features/mod-log-file-with-rotation.md), [Per-mod log file, build stamp and fault limiter](../../../../mods/ksacars/features/mod-log-file.md),
  [Own log file, build stamps and a developer marker file](../../../../mods/ksarmory/features/own-log-file-and-developer-switch.md)
- [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md), [Mixed assets list with csproj content copy and build stamp](../../loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-2.md)
