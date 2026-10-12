---
type: Mod Feature
title: One shipped mod folder from thirty-plus projects, with dist validation and rolling GitHub releases
description: MSBuild resolves the game DLLs once for every project, only the unscience project deploys, copying exactly the MeowSci assemblies of its reference closure plus Tomlyn, and CI stamps the version, validates the folder and publishes rolling releases
mod: unscience
submod: unscience
category: testing-and-tooling
techniques: [msbuild-deploy-target, ci-release, assembly-load-context, toml-config]
hooks: ["starmap mod.toml [StarMap] EntryAssembly = MeowSci.Unscience"]
lifecycle: [startup:starmap-before-main]
game_types: []
tags: [build, msbuild, deploy, ci, github-actions, release, packaging, assembly-loading]
---

The repository contains one `.lib` project per feature, a development host per feature, 18 test
projects and the `unscience` supermod, yet players install a single folder. This concept covers how
the build gets there: one shared property file that finds the proprietary game DLLs on any machine, a
deploy target on exactly one project that copies only the assemblies it actually references, a script
that proves the folder is right, and a GitHub Actions workflow that versions and publishes it. It also
covers why the copied assemblies load at run time: StarMap gives the mod its own
`AssemblyLoadContext` that resolves dependencies through the entry assembly's `deps.json`.[^root-readme][^repo-index]

Mechanism in one line: `Directory.Build.props` sets `KSAFolder` and `IsPublishable=false` for every
project except `unscience`, whose `CopyCustomContent` target filters `@(ReferenceCopyLocalPaths)` to
`MeowSci.*` and copies it with `mod.toml`, `Tomlyn.dll` and licenses into `<mods>/unscience/`.

## How it works

**Game DLL resolution** ([Directory.Build.props](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/Directory.Build.props#L32-L80)). Every project
compiles against `KSA.dll`, `Brutal.*.dll` and friends through `$(KSAFolder)` HintPaths with
`<Private>false</Private>`, so they are never copied. `KSAFolder` is resolved first-match-wins from the
`KSA_DLL_DIR` variable (CI), a `ksa-game-assemblies` checkout next to the repo, the Windows install
path, or a per-user default. A `ValidateKSAAssemblies` target runs before reference resolution and
stops the build with one actionable error if `KSA.dll` is missing, instead of hundreds of missing-type
errors; native-free test projects opt out with `RequiresKSAAssemblies=false`. The same file sets
`net10.0`, C# 13, nullable reference types and `TreatWarningsAsErrors` repo-wide.[^build-props]

**Only one publishable project.** `IsPublishable` is false for every project whose name is not
`unscience`, and only `unscience/unscience.csproj` has a deploy target; the per-feature hosts still
compile but go nowhere.[^build-props][^unscience-csproj]

**Reference closure, not a folder sweep** ([unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj#L94-L120)).
The supermod project references 31 feature libraries and `ksa-abstractions.lib`; each library has its
own `AssemblyName` (`MeowSci.ZippoLib`, ...).[^zippo-lib-csproj] After build, `CopyCustomContent` copies
`mod.toml`, the entry assembly with its `.pdb` and `.deps.json`, and `LICENSE` into
`$(SelectedDistModDir)unscience\`, then copies only the `MeowSci.*` items of `ReferenceCopyLocalPaths`
(so a stale DLL left in `bin` by an older build is never shipped), `Tomlyn.dll`, and the
`third-party-licenses` folder. `SelectedDistModDir` defaults to the user's KSA mods folder and is
overridden by `UNSCIENCE_DIST_DIR` in CI.[^unscience-csproj][^build-props]

**What is not shipped and why.** `StarMap.API` and `Lib.Harmony` are package references with
`PrivateAssets="all"` and are not in the copy list. At run time StarMap creates a
`ModAssemblyLoadContext` per mod from `<mod folder>/<EntryAssembly>.dll`; its `Load` override first
returns any assembly of that name already in the default context (StarMap loads `0Harmony.dll` there
before the game starts) or in the loader's game context, then assemblies exported by declared mod
dependencies, and only then asks an `AssemblyDependencyResolver` built on the entry assembly, which reads
`MeowSci.Unscience.deps.json` to find `MeowSci.*.dll` and `Tomlyn.dll` beside it.[^starmap-mod-alc][^starmap-runtime-mod][^starmap-program]
That is why the `.deps.json` file is part of the deploy set.

**Dist validation** ([scripts/check-dist.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/check-dist.py)). After `dotnet build` of the
solution, the script asserts the dist root contains only `unscience/`, exactly one `mod.toml` naming
`EntryAssembly = "MeowSci.Unscience"`, the runtime files, and that the set of `MeowSci.*.dll` files equals
the transitive `ProjectReference` closure of `unscience.csproj` (walked by parsing the project XML), and
that no `KSA.dll` was copied.[^check-dist]

**Release workflow** ([.github/workflows/release.yml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/.github/workflows/release.yml)). On a push the
job checks out the private `ksa-game-assemblies` repository with a read-only token into `.tmp/`, points
`KSA_DLL_DIR` at it, runs the offline release-policy tests, computes metadata, stamps
`version = "1.<run number>.0"` (with `-beta` on `feature/*` branches) into `unscience/mod.toml` with
`sed`, builds the whole solution, validates the dist, zips `unscience/` and creates the release unless
the tag already exists (reruns keep their release). A second job deletes older releases per channel:
10 for `main`, 5 shared by all beta branches.[^release-yml][^release-metadata][^publish-release][^release-retention]
Branch names are validated in `release-metadata.sh` and passed through `env:` rather than interpolated
into scripts, because the job has write permission.

## Lifecycle and timing

| Stage | When | What matters |
|---|---|---|
| Build | Developer machine or CI | `KSAFolder` must point at DLLs from the game build being targeted; the decomp in `ksa-game-assemblies` and the reference DLLs come from the same checkout |
| Install | Player unzips `unscience/` into the KSA mods folder and enables it in the manifest | The release notes tell players to delete the old folder first, because unzipping over it leaves removed files behind[^publish-release] |
| Assembly load | `startup:starmap-before-main`: StarMap's `ModLoader` walks enabled manifest entries and creates each mod's load context before the game's `Main` | Only the entry assembly is loaded eagerly; the feature assemblies resolve on first use through the context described above |
| Mod start | `[StarMapAllModsLoaded]` | See the [supermod host](./unscience-supermod-host.md) |

## Key code

| Path | Role |
|---|---|
| [Directory.Build.props](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/Directory.Build.props) | `KSAFolder` tiers, dist dir, `IsPublishable`, early missing-DLL error |
| [unscience/unscience.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/unscience.csproj) | Feature references and the `CopyCustomContent` deploy target |
| [unscience/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/mod.toml) | Manifest; `version` is stamped by CI |
| [zippo.lib/zippo.lib.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/zippo.lib.csproj) | Typical feature library: own `AssemblyName`, game DLLs not copied |
| [.github/workflows/release.yml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/.github/workflows/release.yml) | Build, stamp, validate, package, publish, prune |
| [scripts/check-dist.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/check-dist.py) | Single-mod distribution check against the reference closure |
| [scripts/release-metadata.sh](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/release-metadata.sh) | Branch to channel, version and tag |
| [scripts/publish-release.sh](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/publish-release.sh) | Idempotent `gh release create` |
| [scripts/release-retention.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/release-retention.py) | Selects stale releases per channel |
| [scripts/check-release-policy.py](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scripts/check-release-policy.py) | Offline unit tests for the two scripts above |
| [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs) | StarMap: per-mod dependency resolution order |

## Snippets

Deploy only the referenced feature assemblies:

```xml
<!-- unscience/unscience.csproj L94-L120 -->
  <Target Name="CopyCustomContent" AfterTargets="AfterBuild">
    <MakeDir Directories="$(DistDir)" />
    <ItemGroup>
      <FilesToCopy Include="$(OutputPath)mod.toml" />
      <FilesToCopy Include="$(OutputPath)$(AssemblyName).dll" />
      <FilesToCopy Include="$(OutputPath)$(AssemblyName).pdb" />
      <FilesToCopy Include="$(OutputPath)$(AssemblyName).deps.json" />
      <FilesToCopy Include="$(MSBuildThisFileDirectory)..\LICENSE" />
    </ItemGroup>
    <Copy SourceFiles="@(FilesToCopy)" DestinationFolder="$(DistDir)" />
    <ItemGroup>
      <!-- Copy only resolved project dependencies, never stale DLLs left in bin by old builds. -->
      <MeowSciAssemblies Include="@(ReferenceCopyLocalPaths)"
          Condition="$([System.String]::Copy('%(Filename)').StartsWith('MeowSci.'))" />
    </ItemGroup>
    <Copy SourceFiles="@(MeowSciAssemblies)" DestinationFolder="$(DistDir)" Condition="'@(MeowSciAssemblies)' != ''" />
    <Copy SourceFiles="$(TargetDir)Tomlyn.dll" DestinationFolder="$(DistDir)" Condition="Exists('$(TargetDir)Tomlyn.dll')" />

    <!-- Copy third-party licenses -->
    <MakeDir Directories="$(DistDir)third-party-licenses" />
    <ItemGroup>
      <ThirdPartyLicenses Include="$(MSBuildThisFileDirectory)..\third-party-licenses\*" />
    </ItemGroup>
    <Copy SourceFiles="@(ThirdPartyLicenses)" DestinationFolder="$(DistDir)third-party-licenses" Condition="'@(ThirdPartyLicenses)' != ''" />

    <Message Text="Copied mod files to $(DistDir)" Importance="high" />
  </Target>
```

Prove the folder contains exactly that closure:

```python
# scripts/check-dist.py L6-L30
root = Path(sys.argv[1])
repo = Path(__file__).resolve().parent.parent
package = root / "unscience"
assert sorted(p.name for p in root.iterdir()) == ["unscience"], "Expected only unscience/ in dist"
assert list(root.rglob("mod.toml")) == [package / "mod.toml"], "Expected one StarMap manifest"
assert 'EntryAssembly = "MeowSci.Unscience"' in (package / "mod.toml").read_text()
for name in ("MeowSci.Unscience.dll", "MeowSci.Unscience.deps.json", "Tomlyn.dll", "LICENSE"):
    assert (package / name).is_file(), f"Missing runtime file: {name}"
expected = set()
pending = [repo / "unscience/unscience.csproj"]
visited = set()
while pending:
    path = pending.pop().resolve()
    if path in visited:
        continue
    visited.add(path)
    project = ET.parse(path).getroot()
    assembly = project.findtext(".//AssemblyName") or path.stem
    expected.add(f"{assembly}.dll")
    pending.extend(path.parent / reference.attrib["Include"].replace("\\", "/")
                   for reference in project.findall(".//ProjectReference"))
actual = {p.name for p in package.glob("MeowSci.*.dll")}
assert actual == expected, f"Feature assemblies: missing {expected - actual}; unexpected {actual - expected}"
assert not list(package.glob("KSA.dll")), "Do not distribute proprietary game assemblies"
print(f"PASS: one Unscience mod with {len(expected) - 1} explicitly referenced feature assemblies")
```

How StarMap resolves the copied assemblies (StarMap repository):

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L24-L72
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var existingInDefault = Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (existingInDefault != null)
                return existingInDefault;

            var existingInGameContext = _coreAssemblyLoadContext?.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (existingInGameContext != null)
                return existingInGameContext;

            if (_coreAssemblyLoadContext != null)
            {
                try
                {
                    var asm = _coreAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
                    if (asm != null)
                        return asm;
                }
                catch (FileNotFoundException)
                {
                }
            }
// ...

            var foundPath = _modDependencyResolver.ResolveAssemblyToPath(assemblyName);
            if (foundPath is null)
                return null;

            return LoadFromAssemblyPath(Path.GetFullPath(foundPath));
```

## Game APIs touched

None. The build references the game assemblies only at compile time, with `Private=false`; the
runtime contract is the StarMap manifest entry in `mod.toml`.

## Pitfalls and version notes

- Shipping `0Harmony.dll` in a mod folder is unnecessary with StarMap: the loader's copy in the default
  context wins by name before the mod's resolver is consulted.
- If a feature library is referenced only by a development host and not by `unscience.csproj`, it is not
  in the closure, so it is neither deployed nor allowed by `check-dist.py`. Adding a sub-mod means adding a
  `ProjectReference` here and constructing its `ISubmod` in `Mod.cs`.
- `TreatWarningsAsErrors` is on repo-wide (with `CS1591` exempted), so a game update that only adds
  obsolete warnings still breaks the build.
- The CI checks out `ksa-game-assemblies` at `main`, so a release always compiles against the newest
  reference DLLs pushed there, not a pinned build.
- The root README states the checkout compiles against KSA 2026.10.7.5541 with the last full integration
  audit on 2026.9.22.5482; this bundle's engine claims were checked against 2026.10.10.5554.[^root-readme]

## See also

- [Managed test harnesses](./unscience-managed-test-harnesses.md)
- [Standalone mod template](./fixme-mod-name-standalone-mod-template.md) (per-feature host project shape)
- [Supermod host](./unscience-supermod-host.md) and [consolidated patcher](./unscience-consolidated-patcher.md)
- [unscience sub-mod](../submods/unscience.md) and [mod overview](../overview.md)
- [Publish rolling releases from a multi-assembly mod with a stamped version and a validated folder](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-5.md) - how-to article demonstrating this feature
- [Package NuGet dependencies with deps.json and ship exactly the reference closure](../../../howto/getting-started/dependencies/ship-third-party-dlls-with-a-mod/ship-third-party-dlls-with-a-mod-2.md) - how-to article demonstrating this feature

[^root-readme]: README.md
[^repo-index]: REPOSITORY_INDEX.md
[^build-props]: Directory.Build.props
[^unscience-csproj]: unscience/unscience.csproj
[^zippo-lib-csproj]: zippo.lib/zippo.lib.csproj
[^starmap-mod-alc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^starmap-runtime-mod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-program]: StarMap/Program.cs
[^check-dist]: scripts/check-dist.py
[^release-yml]: .github/workflows/release.yml
[^release-metadata]: scripts/release-metadata.sh
[^publish-release]: scripts/publish-release.sh
[^release-retention]: scripts/release-retention.py
