---
type: How-To
title: Let two mods edit the same Core file with a shared shadow folder and a named mutex
description: Coordinate two mods that patch the same Core XML by building one shared shadow copy under a named mutex, resetting it once per launch with a process-start token, and compiling the coordinator into both mods
category: assets
group: xml-patching
topic: redirect-game-xml-loading
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/realatmospheres/features/cross-mod-shared-shadow]
techniques: [xml-patch, named-mutex, msbuild]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.XmlLoader, KSA.SystemInfo, KSA.AssetBundle, KSA.SystemTemplate]
tags: [xml, redirect, shared-shadow, mutex, coordination, msbuild, cross-mod]
---

The redirect in [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md) can point the game at only one copy of `Astronomicals.xml` and
of each system template. If two mods both need to change those files (Real Atmospheres edits atmospheres and
clouds, its sibling Real Surfaces edits the surfaces and Venus's dunes), two private copies cannot both win.
Instead both mods write into a single shared copy and both redirect there. The coordinator is one source
file compiled into both mods, so neither depends on the other at runtime. This article covers the whole
coordination topic: file layout, the named mutex, the once-per-launch reset, the edit callback and the build
trick for sharing source between projects.

Demonstrated by [Shared shadow for two cooperating mods](../../../../mods/realatmospheres/features/cross-mod-shared-shadow.md). Real Surfaces is not in the pinned repositories, so
only the Real Atmospheres side is shown.

## Background: how the game does it

The game offers no ordering or merging for a replaced file: the Core `Mod` has one `Assets` entry and one
`SystemTemplates` entry per file name, and the last prefix to swap a name wins
([Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md)). StarMap runs each mod's `[StarMapBeforeMain]` one after another on the main
thread ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L60-L101)), in manifest order (a mod whose required dependency has not loaded yet is delayed until it has), not in mod-name order.
KSA appends newly found mod folders to the manifest in the order `Directory.GetDirectories` returns them
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L328-L371)).

## Lifecycle and timing

`startup:starmap-before-main`. The shared build runs inside `ShadowBuilder.Build` from `ModMain.BeforeMain`, so
the result exists before `ModLibrary.PrepareAll` reads the system headers. Within one game process the two
mods' builds do not overlap, so the mutex is not contended there; it matters if two game processes start at
once (an inference from StarMap's sequential initialization, the code comment only says it serializes the two
mods). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Compute one location both mods agree on.** It is derived from either mod's own folder (its parent is the
   mods directory) and lists which Core files are shared; a file the game does not ship is skipped:

   ```csharp
   // Shared/SharedShadow.cs L36-L42
       public static readonly string[] SharedFiles =
           { "Astronomicals.xml", "SolSystem.xml", "SolSystemDense.xml", "SolSystemInterstellar.xml" };

       /// <summary>&lt;mods&gt;\_RealSharedShadow\Core — derived from either mod's own folder
       /// (its parent is the mods dir), so both mods compute the identical path.</summary>
       public static string CoreDir(string modDir)
           => Path.Combine(Path.GetDirectoryName(modDir.TrimEnd('\\', '/'))!, "_RealSharedShadow", "Core");
   ```

2. **Serialize with a global named mutex, and treat abandonment as acquisition.** If the other holder crashed
   the state is rebuilt from stock anyway:

   ```csharp
   // Shared/SharedShadow.cs L52-L66
           var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
           string sharedCore = CoreDir(modDir);
           string token = LaunchToken();

           Mutex? mutex = null;
           bool held = false;
           try
           {
               mutex = new Mutex(false, @"Global\RealSharedShadow_v1");
               try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
               catch (AbandonedMutexException) { held = true; } // prior holder crashed; the state we rebuild is self-healing

               Directory.CreateDirectory(sharedCore);
               string tokenFile = Path.Combine(sharedCore, ".launch_token");
               bool fresh = ReadAllTextSafe(tokenFile) != token;   // first mod this launch rebuilds the base
           // ...
   ```

3. **Reset the shared copy once per launch.** The launch token is the game process's start time. If the token
   file differs, this mod is the first this launch and copies every file fresh from stock, LF-normalised so both
   mods' anchors match; the second mod finds the token matching and patches on top of the first mod's edits:

   ```csharp
   // Shared/SharedShadow.cs L66-L89
               // ...
               bool fresh = ReadAllTextSafe(tokenFile) != token;   // first mod this launch rebuilds the base

               foreach (string name in SharedFiles)
               {
                   string src = Path.Combine(coreDir, name);
                   if (!File.Exists(src)) continue;
                   string dst = Path.Combine(sharedCore, name);
                   if (fresh || !File.Exists(dst))
                       // fresh stock base, once per launch; LF-normalized so both mods'
                       // (LF) anchors match regardless of the stock file's line endings
                       File.WriteAllText(dst, File.ReadAllText(src).Replace("\r\n", "\n").Replace("\r", "\n"));
                   string content = File.ReadAllText(dst);
                   string patched = applyEdits(name, content);      // this mod's edits (idempotent)
                   if (!ReferenceEquals(patched, content) && patched != content)
                       File.WriteAllText(dst, patched);
                   result[name] = dst;
               }

               if (fresh)
               {
                   File.WriteAllText(tokenFile, token);
                   log("shared shadow: fresh stock base for this launch");
               }
           }
   ```

   ```csharp
   // Shared/SharedShadow.cs L102-L106
       private static string LaunchToken()
       {
           try { return Process.GetCurrentProcess().StartTime.ToBinary().ToString(); }
           catch { return "0"; }
       }
   ```

4. **Pass an edit callback.** Each mod gives `applyEdits(fileName, content)` that returns new content; the
   coordinator writes it back only if it changed. Real Atmospheres dispatches on the file name:

   ```csharp
   // ShadowBuilder.cs L91-L94
           var shared = SharedShadow.Build(coreDir, modDir, (name, sys) =>
           {
               if (name.Equals("Astronomicals.xml", StringComparison.OrdinalIgnoreCase))
                   return ApplyAstronomicalsEdits(sys, assetsDir);
           // ...
   ```

   Every edit is marker-guarded and idempotent ([Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md)), so the order of the two mods
   does not matter for disjoint edits.

5. **Split the result into the two redirects.** The returned map of file name to shared path drives
   `Mod.LoadAssetBundles` for `Astronomicals.xml` and `Mod.LoadSystems` / `PrepareSystems` for the system
   templates:

   ```csharp
   // ShadowBuilder.cs L125-L131
           foreach (var kv in shared)
           {
               if (kv.Key.Equals("Astronomicals.xml", StringComparison.OrdinalIgnoreCase))
                   ShadowAstronomicals = kv.Value;   // -> Mod.LoadAssetBundles redirect
               else
                   ShadowSystemFiles[kv.Key] = kv.Value;   // -> Mod.LoadSystems redirect
           }
   ```

6. **Make dependent edits fail-soft.** Where one mod's edit anchors on the other's output (Real Surfaces inserts
   Triton's surface before the `<Color>` that Real Atmospheres' body upgrade adds), the dependent edit logs and
   skips if its anchor is missing, and each mod still produces a valid shared shadow when the other is not
   installed.

7. **Share one source file between two projects.** The csproj compiles the sibling folder's copy when it exists (the
   author's dev tree) and falls back to the mirror in this repository for a standalone clone:

   ```xml
   <!-- RealAtmospheres.csproj L23-L25 -->
       <Compile Remove="Shared\**" />
       <Compile Include="..\Shared\SharedShadow.cs" Link="SharedShadow.cs" Condition="Exists('..\Shared\SharedShadow.cs')" />
       <Compile Include="Shared\SharedShadow.cs" Link="SharedShadow.cs" Condition="!Exists('..\Shared\SharedShadow.cs')" />
   ```

   The class is `internal` in namespace `RealModShared`, so each mod carries its own copy.

## Complete example

- [Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs) (whole coordinator)
- [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L91-L132) (callback and redirect split)
- [RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L20-L26) (shared source)

## Variations and gotchas

- **Load order is not name order.** The coordinator's comment relies on Real Atmospheres loading before Real
  Surfaces. StarMap runs `BeforeMain` in manifest order, and KSA appends newly found folders in directory
  listing order, so name order holds only when both folders are discovered in the same launch and the listing is
  alphabetical. If Real Atmospheres is installed after Real Surfaces, or the manifest is reordered, Real Surfaces
  runs first, so dependent edits must tolerate either order.
- **Fail-soft I/O.** Any IO or lock error logs and returns what was produced so far; the mod then runs stock for
  those files rather than failing the launch.
- **Folder layout.** The shared copy lives beside the mods (`<mods>/_RealSharedShadow/Core`), not inside either
  mod, so removing one mod does not delete the other's output.
- **Fresh once per process.** A crash and relaunch gets a new process start time, so a stale shared copy is never
  reused across launches.
- **Does not combine with KittenExtensions** for the same reason as approach 1 (the redirected paths were never
  imported into its document).
- **Third-party scope.** Only two cooperating mods by the same author are shown; a general registry of edits by
  unrelated mods does not exist in this pinned code.

## See also

- [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](redirect-game-xml-loading-1.md), [Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md), [Share types and design an inter-mod API between StarMap mods](../../../getting-started/dependencies/share-types-between-mods/overview.md)
- [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md), [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md)
- [Shared shadow for two cooperating mods](../../../../mods/realatmospheres/features/cross-mod-shared-shadow.md)
