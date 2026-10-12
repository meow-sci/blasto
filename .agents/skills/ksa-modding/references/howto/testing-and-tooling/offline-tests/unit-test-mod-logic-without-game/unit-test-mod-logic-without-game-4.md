---
type: How-To
title: Run NUnit suites with property tests, sentinel flushes and a three-OS CI matrix
description: Keep game types out of the tested layers, drive async output deterministically with a sentinel instead of sleeps, gate platform-specific tests per operating system and run the suite quietly on Windows, Linux and macOS
category: testing-and-tooling
group: offline-tests
topic: unit-test-mod-logic-without-game
approach: 4
difficulty: intermediate
prerequisites: [dotnet-sdk, nunit]
demonstrated_by: [mods/purrtty/features/headless-test-suites]
techniques: [nunit, property-tests, deterministic-tests, ci-matrix]
hooks: []
lifecycle: [event:headless-test-run, thread:background]
game_types: [Brutal.ImGuiApi.Abstractions.ConsoleWindow]
tags: [testing, nunit, fscheck, determinism, ci, cross-platform]
---

Outcome: a four-project NUnit suite that runs without launching KSA, produces no output when it passes, never sleeps to wait for async work and runs the same on three operating systems in CI.
Demonstrated by purrTTY ([purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs#L77-L108)), whose terminal backend has no game types at all. Prerequisites: a layered mod whose backend does not name KSA types.

## Background: how the game does it

The layers that do touch game types are tested against the same reference DLLs the mod uses, but only through members that work outside the game. purrTTY's game-console shell captures the stock console's output through `GameConsoleShell.OnConsolePrint(string, ImColor8)` and classifies it by colour; its test project references `Brutal.ImGui.Abstractions`, so the classification is tested against the real `ConsoleWindow.ErrorColor` and `InfoColor` static fields ([current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs#L276-L282)). If a Brutal update changes the sink signature or the colour fields, the test fails at build or test time instead of in the game.

## Lifecycle and timing

Test time only. Every per-frame path in purrTTY's backend is reachable from tests by calling `BuildFrame()` directly, without a KSA frame; output delivery is asynchronous (a channel pumped to an event on a background task), which is what the sentinel flush below makes deterministic. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for what a real frame wraps.

## Steps

1. **Split tests by layer.** purrTTY's four projects: `Terminal.Tests` (frame production, theming, selection, key and mouse encoding, scrollback, session wiring through a headless custom shell), `CustomShellContract.Tests` (base shell classes; NUnit plus FsCheck properties for the registry), `CustomShells.Tests` (real Brutal colour fields) and `Display.Tests` (theme TOML round trip, layouts; pure logic, no ImGui calls).

2. **Reference game assemblies only where it is cheap**, and only the ones needed, resolved conditionally and not copied as private assets:

   ```xml
   <!-- purrTTY.CustomShells.Tests/purrTTY.CustomShells.Tests.csproj L40-L56 -->
       <ItemGroup>
           <!-- KSA game DLLs for Harmony coordination and ConsoleWindow types.
                Brutal.ImGui carries ImColor8, which the OnConsolePrint capture
                tests construct directly. -->
           <Reference Include="Brutal.ImGui.Abstractions" Condition="Exists('$(KSAFolder)/Brutal.ImGui.Abstractions.dll')">
               <HintPath>$(KSAFolder)/Brutal.ImGui.Abstractions.dll</HintPath>
               <Private>false</Private>
           </Reference>
           <Reference Include="Brutal.ImGui" Condition="Exists('$(KSAFolder)/Brutal.ImGui.dll')">
               <HintPath>$(KSAFolder)/Brutal.ImGui.dll</HintPath>
               <Private>false</Private>
           </Reference>
           <Reference Include="Brutal.Core.Logging" Condition="Exists('$(KSAFolder)/Brutal.Core.Logging.dll')">
               <HintPath>$(KSAFolder)/Brutal.Core.Logging.dll</HintPath>
               <Private>false</Private>
           </Reference>
       </ItemGroup>
   ```

3. **Pin a version-sensitive seam with a live test.** The one piece of logic flagged as Brutal-version-sensitive gets coverage against the real fields:

   ```csharp
   // purrTTY.CustomShells.Tests/Unit/GameConsoleShellTests.cs L439-L463
       // OnConsolePrint(string, ImColor8) — against the real ConsoleWindow color
       // fields (plain constant statics in Brutal.ImGui.Abstractions, which the
       // test csproj copies beside the tests). This is the one piece of logic
       // CLAUDE.md flags as Brutal-version-sensitive, so it must have live
       // coverage: a Brutal bump that changes the sink signature or the color
       // fields should fail here, not in-game.

       [Test]
       public async Task OnConsolePrint_ErrorColor_EmitsStderr()
       {
           // Arrange
           var options = CustomShellStartOptions.CreateWithDimensions(80, 24);
           await _shell!.StartAsync(options);
           _shell.SetActiveForConsoleOutput(true);
           _shell.LastOutputType = ShellOutputType.Stdout;

           // Act
           GameConsoleShell.OnConsolePrint("boom", ConsoleWindow.ErrorColor);
           await _shell!.FlushOutputAsync();

           // Assert
           Assert.That(_shell.LastOutputType, Is.EqualTo(ShellOutputType.Stderr));
           Assert.That(_outputBuffer.ToString(), Does.Contain("boom"));
       }
   ```

4. **Never sleep; flush with a sentinel.** Input handling in the base shell is synchronous and only output delivery is async, so a unique zero-length sentinel queued through the same single-reader FIFO pump proves every earlier output was delivered:

   ```csharp
   // purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs L85-L108
           public async Task FlushOutputAsync()
           {
               var sentinel = new byte[0];
               ReadOnlyMemory<byte> sentinelMemory = sentinel;
               var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

               void Handler(object? sender, ShellOutputEventArgs args)
               {
                   if (args.Data.Equals(sentinelMemory))
                   {
                       delivered.TrySetResult();
                   }
               }

               OutputReceived += Handler;
               try
               {
                   QueueOutput(sentinel, ShellOutputType.Stdout);
                   await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
               }
               finally
               {
                   OutputReceived -= Handler;
               }
   ```

   Real-process tests, where no completion signal exists, poll a condition every 25 ms against a 10 s deadline. The timeout is a failure bound, not a wait. A 2026-06 cleanup removed about 400 sleeps (about 26 s per run) without changing assertions ([docs/build-and-test.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/build-and-test.md#L87-L117)).

5. **Be silent on success.** No `Assert.Pass("msg")`, skips use `Assert.Ignore()` with the reason in a code comment (the recorded message would print on every skipped run), and no console output on success paths; diagnostics go in assertion failure messages ([docs/build-and-test.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/build-and-test.md#L68-L85)).

6. **Gate platform-specific tests silently.** POSIX pty tests self-skip on Windows, and the ConPTY twin skips elsewhere:

   ```csharp
   // purrTTY.Terminal.Tests/UnixProcessManagerTests.cs L14-L25
   [NonParallelizable]
   public sealed class UnixProcessManagerTests
   {
       [SetUp]
       public void SkipOnWindows()
       {
           if (OperatingSystem.IsWindows())
           {
               // POSIX pty backend is Linux/macOS only - skip silently
               Assert.Ignore();
           }
       }
   ```

7. **Run a three-OS matrix.** Each runner loads its own vendored native library, so platform layers get real coverage on every OS; the test step is quiet and writes a TRX file for a reporter action:

   ```yaml
   # .github/workflows/release.yml L48-L53
     test:
       strategy:
         fail-fast: false
         matrix:
           os: [ubuntu-latest, windows-latest, macos-14]
       runs-on: ${{ matrix.os }}
   ```
   ```yaml
   # .github/workflows/release.yml L76-L78
         - name: Test
           id: test
           run: dotnet test purrtty.slnx -c Release --nologo -v quiet --logger trx --results-directory .tmp/test-results
   ```

8. **Add pin-bump gates.** Tests such as `RawCellLayout_MatchesNativeAccessors` must pass after any native-library update; name them in the contributor notes so a bump cannot skip them.

## Complete example

| File | Role |
|---|---|
| [docs/build-and-test.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/build-and-test.md) | The quiet and no-sleep rules with examples |
| [purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs) | Sentinel flush helper |
| [purrTTY.CustomShells.Tests/Unit/GameConsoleShellTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShells.Tests/Unit/GameConsoleShellTests.cs) | Tests against real Brutal colour fields |
| [purrTTY.Terminal.Tests/UnixProcessManagerTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/UnixProcessManagerTests.cs) | OS-gated real-PTY tests |
| [purrTTY.CustomShellContract.Tests/Property/CustomShellRegistryProperties.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract.Tests/Property/CustomShellRegistryProperties.cs) | FsCheck properties |
| [.github/workflows/release.yml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.github/workflows/release.yml) | Three-OS test matrix |

## Variations and gotchas

- **Do not flush after stopping.** Once the channel is completed the sentinel never arrives; `StopAsync` already drains the pump, so no wait is needed there.
- **Collectors must skip zero-length events**, or the sentinel pollutes captured output.
- **Private game assemblies in CI.** The workflow checks out the assemblies repo with a read-only token and points `KSA_DLL_DIR` at it; forks without the secret cannot run the game-referencing projects (see [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md)).
- **Game updates.** Tests that touch real Brutal fields are the early warning for UI-library changes; the rest of the suite is unaffected.

## See also

- [Overview](overview.md); [approach 3](unit-test-mod-logic-without-game-3.md), [approach 5](unit-test-mod-logic-without-game-5.md)
- [Run one check script from the pre-push hook and from CI](../run-repo-checks-from-a-pre-push-hook-and-ci/overview.md)
- Mod feature: [purrTTY headless test suites](../../../../mods/purrtty/features/headless-test-suites.md)
