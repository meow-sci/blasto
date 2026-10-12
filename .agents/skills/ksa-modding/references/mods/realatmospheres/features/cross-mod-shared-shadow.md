---
type: Mod Feature
title: Shared shadow for two cooperating mods
description: Two sibling mods that edit the same Core XML files write into one shared copy, serialized by a global named mutex and reset to stock exactly once per game process by a launch token
mod: realatmospheres
category: assets
techniques: [xml-patch, inter-mod-api, named-mutex]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.ModLibrary]
tags: [xml, assets, inter-mod, mutex, file-coordination, shared-source]
---

The [Core XML entry redirect](core-xml-entry-redirect.md) can point the game at only one copy of
`Astronomicals.xml` and of each system template. The author's second mod, Real Surfaces (not part of
this repository), edits the same files (Venus is a library body, so its surface can only be changed
in `Astronomicals.xml`; Pluto, Triton and Titan are inline in the system files). Instead of two private
copies, both mods build one shared copy and both redirect to it. The coordinator is a single source
file compiled into both mods.[^ra-shared-sharedshadow]

## How it works

- **One location both mods compute.** `SharedShadow.CoreDir(modDir)` is the mods folder (the parent
  of either mod's own folder) plus `_RealSharedShadow/Core` ([Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L39-L42)).
- **Files in scope.** `Astronomicals.xml`, `SolSystem.xml`, `SolSystemDense.xml`,
  `SolSystemInterstellar.xml`; a file the game does not ship is skipped
  ([Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L31-L37)).
- **Serialize.** `Build` opens the named mutex `Global\RealSharedShadow_v1` and waits up to 30 s. An
  `AbandonedMutexException` (the other holder crashed) counts as acquired, because the state is rebuilt
  from stock anyway ([Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L56-L62)).
- **Reset once per launch.** The launch token is the game process's start time. If
  `.launch_token` in the shared folder differs, this mod is the first one this launch and copies every
  file fresh from stock (LF-normalized); the second mod finds the token matching and patches on top of
  the first mod's edits instead of clobbering them ([Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L64-L88),
  [Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L102-L106)).
- **Edit callback.** Each mod passes `applyEdits(fileName, content)`; Real Atmospheres dispatches on the
  file name to its `Astronomicals.xml` edits or its system-template edits
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L90-L124)). Every edit is marker-guarded, so the order of the
  two mods does not matter for disjoint edits.
- **Order where it matters.** Where one mod's edit anchors on the other's output (Real Surfaces inserts
  Triton's surface before the `<Color>` that Real Atmospheres' body upgrade adds), the dependent edit is
  fail-soft and the coordinator's comment relies on the mods loading in name order, Real Atmospheres first ([Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L16-L27),
  [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1781-L1785)).
  StarMap actually runs `BeforeMain` in `manifest.toml` order, not name order
  ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L54-L84)).
  KSA appends newly found mod folders to the manifest in the order `Directory.GetDirectories` returns
  them ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L328-L370)).[^ksa-modlibrary]
  Name order therefore holds only when both folders were found in the same launch and the listing is
  alphabetical. If Real Atmospheres is installed after Real Surfaces, or the manifest is reordered,
  Real Surfaces runs first.
- **One source, two projects.** The `.csproj` compiles `..\Shared\SharedShadow.cs` when the sibling
  folder exists (the author's dev tree) and falls back to the mirror in this repository for a standalone
  clone ([RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L19-L26)). The class is `internal` in
  namespace `RealModShared`, so each mod carries its own copy and neither depends on the other at
  runtime.

## Lifecycle and timing

- **`startup:starmap-before-main`.** Called from `ShadowBuilder.Build` inside `ModMain.BeforeMain`
  ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L83-L133)). StarMap runs each mod's `BeforeMain` as it
  initializes that mod, one after another on the main thread
  ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L104)),
  so within one game process the two mods' `Build` calls do not overlap and the mutex is not contended
  there. It would matter if two game processes started at once (an inference from StarMap's sequential
  initialization; the code comment only says it serializes the two mods).
- The result must exist before `ModLibrary.PrepareAll` reads the system headers, which the
  `BeforeMain` timing guarantees.

## Key code

| Path | Role |
|---|---|
| [Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L49-L100) | `Build`: mutex, launch token, fresh copy, edit callback, result map |
| [Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L102-L112) | `LaunchToken`, `ReadAllTextSafe` |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L83-L133) | Real Atmospheres' callback and the split into the two redirects |
| [RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L19-L26) | Shared source file with a local fallback |

## Snippets

```csharp
// Shared/SharedShadow.cs L56-L89
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

```xml
<!-- RealAtmospheres.csproj L23-L25 -->
    <Compile Remove="Shared\**" />
    <Compile Include="..\Shared\SharedShadow.cs" Link="SharedShadow.cs" Condition="Exists('..\Shared\SharedShadow.cs')" />
    <Compile Include="Shared\SharedShadow.cs" Link="SharedShadow.cs" Condition="!Exists('..\Shared\SharedShadow.cs')" />
```

## Game APIs touched

None. The files it copies are `current/Content/Core/Astronomicals.xml` and the Sol system templates
such as [current/Content/Core/SolSystem.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/SolSystem.xml#L36-L43), where
the library bodies are pulled in with `<LoadFromLibrary>` and Titan, Pluto, Triton and Neptune are
defined inline.

## Pitfalls and version notes

- **Uninstalling needs two folders.** The README tells players to delete both `mods\RealAtmospheres`
  and `mods\_RealSharedShadow` ([README.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/README.md#L104)). The shared folder has no
  `mod.toml`, and `ModLibrary.AddMods` only lists folders that contain one, so KSA never shows it as a
  mod ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L328-L333)).
- **A failure leaves stock files.** Any IO or lock error is logged and `Build` returns what it had,
  so the redirect falls back to stock for the missing files ([Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs#L90-L99)).
- **The mirror can drift.** The comment in the `.csproj` asks to keep `Shared\SharedShadow.cs` in sync
  with the sibling folder by hand.
- **Name order is a convention, not enforced.** Nothing in StarMap's `mod.toml` here declares a
  dependency between the two mods, and StarMap follows the manifest order (see above). The
  coordinator's comment names Triton as the edit that depends on the order and says it fails soft,
  so with Real Surfaces first its Triton surface would be skipped (Real Surfaces' code is not in this
  repository).

## See also

- [Core XML entry redirect](core-xml-entry-redirect.md)
- [Anchor-verified text patching of the shadow tree](shadow-tree-anchor-patching.md)
- [Per-body atmosphere optics in the system XML](atmosphere-optics-in-system-xml.md)
- [Let two mods edit the same Core file with a shared shadow folder and a named mutex](../../../howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-4.md) - how-to article demonstrating this feature

[^ra-shared-sharedshadow]: Shared/SharedShadow.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
