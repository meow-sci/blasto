---
type: How-To
title: "Check shaders offline: keep GLSL constants in step with C# using a regex text test"
description: Read const floats and kind-tag thresholds out of the .comp source in unit tests and assert them against the C# values, catching drift that no compiler can
category: shaders
group: glsl-workflow
topic: check-mod-shaders-offline-with-glslang
approach: 3
difficulty: intermediate
prerequisites: [dotnet, xunit]
demonstrated_by: [mods/ksarmory/features/raymarched-mushroom-cloud]
techniques: [glsl-shader]
hooks: [xunit tests]
lifecycle: [thread:background]
game_types: [KSA.ShaderReference]
tags: [glsl, unit-test, regex, constants, push-constants, kind-tag, drift, ksarmory]
---
A compiler checks that a shader is valid, not that it agrees with your C#. When the same number lives in both (a time constant, a speed, a threshold that selects a branch), a change on one side is silent. KSArmory solves this with xunit tests that read the `.comp` source, extract the numbers with a regex and assert them equal to the C# values. This article shows the three flavours it uses:
constants, kind-tag thresholds in the dispatch, and specialization-constant tunables. No game is needed; the tests read the file from the source tree.

## Background: how the game does it

Nothing in the game participates; this is a development-time safety net for shaders the mod owns. The cloud compute shader has no room left in its 128-byte push constants to be told values like the cloud's age or stand time, so it restates them as GLSL `const float`s (`StandBeginsSeconds`, `StandLastsSeconds`, `AgedShear`, `BoundMargin`, `DustFrontSpeed`) that must equal the C# model's. A stand the shader thinks ends at another time
shears the cloud on a clock the CPU-side bound does not know, and the sheared part is cut off at the bounding sphere.

## Lifecycle and timing

Test time only (`thread:background` from the game's point of view; the tests run in CI or locally, not in the game). They read `src/KSArmory/Shaders/KSArmoryCloud.comp` relative to the test assembly, so they need the repository, not an install.

## Steps

1. **Read a named constant out of the source with a regex.** Fail loudly if it is missing, so a rename breaks the test instead of silently skipping it:

   ```csharp
   // tests/KSArmory.Tests/MushroomCloudShaderTests.cs L15-L23
       private static double Constant(string name)
       {
           string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KSArmory", "Shaders");
           string source = File.ReadAllText(Path.Combine(dir, "KSArmoryCloud.comp"));
           Match m = Regex.Match(source, @"const float " + name + @" = ([0-9.]+);");

           Assert.True(m.Success, $"no 'const float {name}' in the shader");
           return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
       }
   ```

   Source: [tests/KSArmory.Tests/MushroomCloudShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/MushroomCloudShaderTests.cs#L15-L23). Parse with the invariant culture.

2. **Assert the pairs.** One test per contract, so a failure names the contract:

   ```csharp
   // tests/KSArmory.Tests/MushroomCloudShaderTests.cs L25-L39
       [Fact]
       public void TheShaderKnowsTheStandAsTheLawDoes()
       {
           Assert.Equal(MushroomCloud.RiseSeconds, Constant("StandBeginsSeconds"), 9);
           Assert.Equal(MushroomCloud.StandSeconds, Constant("StandLastsSeconds"), 9);
           Assert.Equal(MushroomCloud.AgedShear, Constant("AgedShear"), 9);
           Assert.Equal(MushroomCloud.BoundMargin, Constant("BoundMargin"), 9);
       }

       /// <summary>The dust ring runs out at the blast front's speed of sound, not one of its own.</summary>
       [Fact]
       public void TheDustRingRunsAtTheFrontsSpeedOfSound()
       {
           Assert.Equal(BlastWave.SoundMetresPerSecond, Constant("DustFrontSpeed"), 9);
       }
   ```

   Source: [tests/KSArmory.Tests/MushroomCloudShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/MushroomCloudShaderTests.cs#L25-L39). The shader side is plain: `const float StandBeginsSeconds = 57.0;` ([src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L607-L612)).

3. **Protect the push-constant kind-tag contract.** One compute pipeline draws several effects, selected by a float in the push constant. A test parses the branch chain `if (pc.FireSun.z > T) DrawX(at, size);` and checks every threshold is under its kind and over the next, tested from the highest down, with the fall-through being the lowest kind:

   ```csharp
   // tests/KSArmory.Tests/SkyDispatchShaderTests.cs L28-L49
       [Fact]
       public void EachKindsBranchCatchesItAndNoOther()
       {
           string source = Shader();
           MatchCollection tests = Regex.Matches(source, @"if \(pc\.FireSun\.z > ([0-9.]+)\) (Draw\w+)\(at, size\);");
           Assert.NotEmpty(tests);

           double above = double.PositiveInfinity;
           foreach (Match m in tests)
           {
               double threshold = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
               float kind = Kinds[m.Groups[2].Value];

               // Tested from the highest down: this one's threshold is under its kind and over the next.
               Assert.True(threshold < kind && threshold > kind - 1.0, $"{m.Groups[2].Value} caught at > {threshold}");
               Assert.True(threshold < above, "the kinds are tested from the highest down");
               above = threshold;
           }

           // And what none of the thresholds catch falls through to the lowest kind.
           Assert.Matches(@"else DrawXRayGlow\(at, size\);", source);
       }
   ```

   Source: [tests/KSArmory.Tests/SkyDispatchShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/SkyDispatchShaderTests.cs#L28-L49). The dictionary `Kinds` maps each function name to the C# `SkyDispatch` constant sent in the push constant.

4. **Compare tunables by id and default.** Parse every `layout (constant_id = N) const float|int name = value;` declaration from all `.comp` files, and assert each C# tunable is declared in a shader with the same specialization id and default:

   ```csharp
   // tests/KSArmory.Tests/ShaderTunablesTests.cs L14-L32
       private static readonly Regex Declared = new(
           @"layout \(constant_id = (\d+)\) const (float|int) (\w+) = ([-0-9.]+);", RegexOptions.Compiled);

       private static Dictionary<string, (int Id, double Value)> Shaders()
       {
           string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KSArmory", "Shaders");
           Dictionary<string, (int, double)> found = [];

           foreach (string file in Directory.GetFiles(dir, "*.comp"))
           {
               foreach (Match m in Declared.Matches(File.ReadAllText(file)))
               {
                   found[m.Groups[3].Value] = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                               double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
               }
           }

           return found;
       }
   ```

   Source: [tests/KSArmory.Tests/ShaderTunablesTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ShaderTunablesTests.cs#L14-L32). This is the only place that compares the two lists; without it a default that disagrees is a value the bridge reports and the picture does not use.

5. **Run these tests with the rest** (`dotnet test`, from the suite) so a shader edit that breaks a contract fails the push.

## Complete example

| File | Role |
|---|---|
| [tests/KSArmory.Tests/MushroomCloudShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/MushroomCloudShaderTests.cs) | Constants and bounds |
| [tests/KSArmory.Tests/SkyDispatchShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/SkyDispatchShaderTests.cs) | Kind-tag thresholds and mark-offset guard |
| [tests/KSArmory.Tests/ShaderTunablesTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ShaderTunablesTests.cs) | Specialization ids and defaults |

## Variations and gotchas

- **Regexes are tied to formatting.** Reformatting a declaration (a different spacing or a macro) breaks the match; the `Assert.True(m.Success, ...)` message says which constant.
- **Restate rarely.** The better design is to send the value in a push constant or uniform ([Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md)); the text test exists because the 128-byte push constant block was full.
- **Pair with the compile check** ([first article](check-mod-shaders-offline-with-glslang-1.md)): the compiler finds syntax errors, the tests find semantic drift.
- **Culture.** Always parse with `CultureInfo.InvariantCulture`; a decimal comma otherwise breaks the parse.

## See also

- Siblings: [KSArmory script](check-mod-shaders-offline-with-glslang-1.md), [KSAStructures suite](check-mod-shaders-offline-with-glslang-2.md), [overview](overview.md)
- [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md)
- [Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md), [Run one check script from the pre-push hook and from CI](../../../testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md)
