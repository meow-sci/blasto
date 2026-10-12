# Getting started

Build, deploy and understand a StarMap code mod end to end - project and mod.toml setup, how KSA and StarMap discover and load mods, startup hook choice, dependencies and shared assemblies, logging, version guards, fault containment and release packaging.

## Loader

How StarMap finds, loads and binds a mod: the minimal project, the launch and discovery sequence, mod.toml and the mod folder layout.

### [Create a minimal StarMap mod](howto/getting-started/loader/create-a-minimal-starmap-mod/overview.md)

What KSA and StarMap require of a code mod folder, and two ways to stand one up - by hand from the discovery rules, or from a repo template that grows into a library plus entry layout

- [Build a minimal StarMap mod by hand from the discovery rules](howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-1.md) — beginner
- [Scaffold a mod with mkmod.ts and grow it into a library hosted by a supermod](howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-2.md) — intermediate

### [How StarMap launches KSA and loads your mod](howto/getting-started/loader/how-starmap-launches-ksa/overview.md)

End-to-end reference for StarMap.exe - config, load contexts, working-directory trick, manifest discovery, dependency ordering and hook binding - with the instance-path and lazy-assembly sub-topics

- [Follow the boot sequence: host process, load contexts and hook binding](howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md) — intermediate
- [Understand the mod folder, manifest and enable/ordering rules](howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md) — beginner
- [Run separate KSA instances by redirecting the documents folder](howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md) — intermediate
- [Preload a lazily loaded game assembly into the core load context before patching it](howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-4.md) — advanced

### [Write mod.toml - KSA keys and the StarMap table](howto/getting-started/loader/mod-toml-reference/overview.md)

How a mod.toml is read twice, by KSA into KSA.Mod and by StarMap into its entry and dependency model, and where the defaults and dead keys are

- [Write mod.toml: KSA keys and the StarMap table](howto/getting-started/loader/mod-toml-reference/mod-toml-reference.md) — beginner

### [Ship assets and code in one mod folder and find it at runtime](howto/getting-started/loader/ship-content-and-code-in-one-mod/overview.md)

How a single folder serves KSA as a content mod and StarMap as a code mod, how asset paths resolve against the mod root, and how code locates that folder at runtime

- [Lay out a flat mod folder with an explicit assets list](howto/getting-started/loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-1.md) — beginner
- [Ship mixed content with a csproj content copy, an assets guard and a build stamp](howto/getting-started/loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-2.md) — intermediate
- [Find your mod's install folder at runtime: Assembly.Location, ModLibrary.Find, Documents](howto/getting-started/loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md) — beginner

## Lifecycle

When mod code runs and how it stays well behaved: startup hooks, fault containment, shutdown, logging and version checks; the existing game-loop-and-lifecycle reference also lives here.

### [Warn when the running game build differs from the one a mod was tested on](howto/getting-started/lifecycle/check-the-game-version/overview.md)

How to read the running KSA version, compare it with a tested-version constant or a build stamp, and log both so bug reports carry their context

- [Compare VersionInfo.Current with a tested-version constant at AllModsLoaded](howto/getting-started/lifecycle/check-the-game-version/check-the-game-version-1.md) — beginner
- [Stamp the targeted KSA build into assembly metadata and compare it at run time](howto/getting-started/lifecycle/check-the-game-version/check-the-game-version-2.md) — intermediate

### [Choose and use the right StarMap lifecycle hook](howto/getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)

What game state exists at StarMapBeforeMain, StarMapImmediateLoad and StarMapAllModsLoaded, how to pick between them, and how to verify a hook is bound and firing

- [StarMapBeforeMain: install patches on code that runs during startup](howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-1.md) — intermediate
- [StarMapImmediateLoad: register data before assets deserialize](howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-2.md) — intermediate
- [StarMapAllModsLoaded: work that needs the full mod set](howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md) — beginner
- [Diagnose a StarMap hook that never fires with a signature checklist](howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-4.md) — beginner

### [Clean up on game exit and relaunch the game from a mod](howto/getting-started/lifecycle/clean-up-on-game-exit/overview.md)

What runs, and what is already gone, when KSA shuts down under StarMap, which exit paths skip StarMapUnload, and how a mod relaunches the game process

- [Clean up with StarMapUnload and ProcessExit, and reset static state](howto/getting-started/lifecycle/clean-up-on-game-exit/clean-up-on-game-exit-1.md) — intermediate
- [Relaunch the game from a mod with a PrepareAll prefix and Process.Start](howto/getting-started/lifecycle/clean-up-on-game-exit/clean-up-on-game-exit-2.md) — advanced

### [Keep mod exceptions out of KSA's frame loop](howto/getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)

Why an exception in a StarMap hook ends the game, and two fault-budget designs that log, disable the mod and undo its side effects

- [Wrap every frame hook in try/catch and disable the mod after ten faults](howto/getting-started/lifecycle/contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-1.md) — intermediate
- [Count consecutive failures and restore the stock HUD when the mod disables itself](howto/getting-started/lifecycle/contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-2.md) — intermediate

### [Game loop and mod lifecycle](howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

How KSA starts, loads mods and runs each frame, and where StarMap hooks, Harmony patches and worker-thread code fit relative to the physics step

- [Hook the frame with StarMap lifecycle attributes](howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) — beginner
- [Hook any frame point with Harmony patches on Program and Universe](howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) — intermediate
- [Mutate simulation state safely relative to the physics step](howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) — advanced
- [Run code inside the physics step on worker threads](howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) — advanced

### [Log from a mod: per-mod log file, game log category and log-once](howto/getting-started/lifecycle/write-a-per-mod-log-file/overview.md)

The ways a StarMap mod can log - its own rotated file, the game's Brutal log system, a game-free interface - and how to keep per-frame problems from flooding the output

- [Write a batched session log with .prev.log rotation, timer flush and a verbose toggle](howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-1.md) — intermediate
- [Anchor the log on the game's logs folder and gate developer tools with a marker file](howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-2.md) — intermediate
- [Find a writable log folder by probing Documents, XDG and fallback locations](howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-3.md) — intermediate
- [Log through the game's logging system with one LogCategory in a tiny shared assembly](howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-4.md) — beginner
- [Keep library code game-free with a logging interface swapped for a Brutal sink at load](howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-5.md) — intermediate
- [Log each problem once with a thread-safe warn-once set reset on unload](howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-6.md) — intermediate
- [Log with HashSet-keyed WarnOnce and ErrorOnce that include the exception type](howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-7.md) — beginner

## Dependencies

Depending on other mods and shipping what a mod needs at run time: dependency declarations, shared contracts and inter-mod APIs, third-party and native libraries.

### [Declare a dependency on another StarMap mod, required or optional](howto/getting-started/dependencies/declare-a-mod-dependency/overview.md)

How StarMap orders and parks mods by their ModDependencies, what Optional and ImportedAssemblies change, and how to integrate with or stand down from a mod you do not declare

- [Declare a required dependency with ImportedAssemblies](howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-1.md) — intermediate
- [Make a dependency optional and ship a vendored fallback copy of the contract](howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-2.md) — advanced
- [Probe for another mod at AllModsLoaded and stand down when a conflicting one is present](howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-3.md) — advanced

### [Share types and design an inter-mod API between StarMap mods](howto/getting-started/dependencies/share-types-between-mods/overview.md)

Why mods that load their own copy of a type cannot talk to each other, and five ways to share identity or avoid needing it - contract imports, API design, duck typing, name-based binding and stable fork identity

- [Share a contract assembly: the importer lists it in ImportedAssemblies](howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-1.md) — advanced
- [Design a text-only registration API with fault results and a CI-checked surface record](howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-2.md) — advanced
- [Duck-type an API: copy look-alike attributes and bind by full name across load contexts](howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-3.md) — advanced
- [Bind plugins by attribute type name the way StarMap does](howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-4.md) — intermediate
- [Fork or rename a library mod without breaking its dependents: new mod id, unchanged assembly identity](howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-5.md) — advanced

### [Ship and load native libraries from a mod folder](howto/getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md)

Why StarMap's per-mod load context leaves native libraries to default probing, and two ways to ship and load them with an explicit resolver - flat per-OS files and a per-RID payload with an ABI check

- [Load flat per-OS native libraries with a DllImportResolver and a libc mapping](howto/getting-started/dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-1.md) — advanced
- [Ship a per-RID native payload from the deploy targets and bind it with layout-exact structs](howto/getting-started/dependencies/ship-native-libraries-with-a-mod/ship-native-libraries-with-a-mod-2.md) — advanced

### [Ship third-party DLLs next to your mod](howto/getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)

Which assemblies belong in a StarMap mod folder, which never do, and how the per-mod load context finds the ones that do

- [Copy a vendored DLL beside the mod and let AssemblyDependencyResolver find it](howto/getting-started/dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-1.md) — beginner
- [Package NuGet dependencies with deps.json and ship exactly the reference closure](howto/getting-started/dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md) — intermediate

## Build

Project setup, build, deploy and release: game references, deploy on every build, generated mod.toml, reference-free and CI builds, and packaging.

### [Build a KSA mod that references only StarMap.API and Harmony](howto/getting-started/build/build-a-mod-without-game-references/overview.md)

Four ways to limit how much of the game a mod's build and code depend on - pure reflection, a compile-gated Game half, a provider library and a per-frame census

- [Build a reflection-only mod that references just StarMap.API and Harmony](howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-1.md) — advanced
- [Split a mod into a game-free half and a compile-gated Game half](howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-2.md) — advanced
- [Centralize game-state access behind a static provider library](howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-3.md) — intermediate
- [Rebuild the vehicle set each frame from Universe.CurrentSystem.All and sync a registry](howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-4.md) — intermediate

### [Build a mod in CI when the game DLLs are private](howto/getting-started/build/build-in-ci-without-the-game-installed/overview.md)

How to compile and test mods on public CI without redistributing KSA - a private DLL repository with an App token, stripped reference assemblies per build, or a compile-gated game seam

- [Fetch a private DLL repository in CI with a GitHub App token](howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-1.md) — advanced
- [Build against Refasmer reference assemblies stored per game build](howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-2.md) — advanced
- [Build and test in CI with no game at all using a compile-gated game seam](howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-3.md) — advanced

### [Deploy a mod on every build and debug it from an IDE](howto/getting-started/build/deploy-a-mod-on-every-build/overview.md)

How a build leaves a runnable mod in the KSA mods folder, five deploy designs from OutputPath to a shared payload list, and the launch profile that starts StarMap.exe under a debugger

- [Import a shared ModDeploy.targets that diagnoses a locked DLL](howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-1.md) — intermediate
- [Point OutputPath straight at the mods folder](howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-2.md) — beginner
- [Share one payload list between the deploy copy and the release zip](howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md) — advanced
- [Add a plain post-build copy with an overridable KSAGameDir](howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-4.md) — beginner
- [Generate a launchSettings profile so Rider or Visual Studio runs StarMap and hits mod breakpoints](howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-5.md) — intermediate

### [Generate mod.toml and StarMap dependencies from the project file](howto/getting-started/build/generate-mod-toml-from-msbuild/overview.md)

Why generating mod.toml from MSBuild keeps version, entry assembly, asset list and dependencies in step with the code, and the shared targets that do it

- [Generate mod.toml and StarMap dependencies from the project file](howto/getting-started/build/generate-mod-toml-from-msbuild/generate-mod-toml-from-msbuild.md) — advanced

### [Package and publish a mod release (zip, CI publishing, installer)](howto/getting-started/build/package-and-publish-a-mod-release/overview.md)

What a mod release must contain, six ways the corpus produces and verifies it - Release targets, payload verification, data and licences, CI publishing, rolling channels and an installer

- [Zip the deployed folder with a Release MSBuild target](howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-1.md) — beginner
- [Verify the release zip against the declared payload file by file](howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-2.md) — advanced
- [Ship data files and third-party licences with the mod](howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-3.md) — beginner
- [Publish from CI with a workflow that reads the tested game version from Mod.cs](howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-4.md) — intermediate
- [Publish rolling releases from a multi-assembly mod with a stamped version and a validated folder](howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-5.md) — advanced
- [Release RC builds, a NuGet API package and a Windows installer for a launcher-style tool](howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-6.md) — advanced

### [Reference the game's assemblies from a mod project without shipping them](howto/getting-started/build/reference-game-assemblies-from-a-mod-project/overview.md)

How a mod project compiles against the installed KSA DLLs with Private=false, and three ways to locate them - a KSADir property, tiered discovery and a shared props file

- [Reference game DLLs with a KSADir property and wildcard Reference items](howto/getting-started/build/reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-1.md) — beginner
- [Resolve the game folder in tiers from Directory.Build.props](howto/getting-started/build/reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-2.md) — intermediate
- [Share one KSADLLs.props across a family of mods and point CI at a private DLL checkout](howto/getting-started/build/reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-3.md) — intermediate
