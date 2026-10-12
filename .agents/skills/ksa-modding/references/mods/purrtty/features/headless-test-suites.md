---
type: Mod Feature
title: Headless test suites for a KSA mod
description: Four NUnit projects that test a game mod without the game, against the real KSA reference assemblies where cheap, with property tests, real-PTY OS-gated tests, and enforced rules that tests are silent on success and never use fixed sleeps
mod: purrtty
category: testing-and-tooling
techniques: []
hooks: [dotnet test purrtty.slnx]
lifecycle: [event:test]
game_types: [Brutal.ImGuiApi.Abstractions.ConsoleWindow, Brutal.ImGuiApi.ImColor8]
tags: [testing, nunit, fscheck, ci, property-based, determinism]
---

Most of purrTTY is tested without launching KSA. The architecture makes that possible (the
terminal backend has no game types), and the tests that do touch game types compile against the
same reference DLLs the mod uses and only exercise members that work outside the game. CI runs the
whole suite on Windows, Linux and macOS. The repository also writes down two rules that keep the
suite fast and readable for both humans and AI assistants.

## How it works

| Project | Covers | Notes |
|---|---|---|
| `purrTTY.Terminal.Tests` | Frame production, theming, selection, OSC sidecar, key/mouse/paste encoding, DSR replies, scrollback, DEC 2026, inbox backpressure, session wiring through a headless custom shell, PTY input contracts (quoting, argv, input queue), kitty graphics with real gatOS frames under `Assets/`, `RawCellLayout` | Loads the vendored native per OS; `UnixProcessManagerTests` drive real `/bin/sh` (skip on Windows), `ConPtyProcessManagerTests` drive real `cmd.exe` (skip elsewhere) |
| `purrTTY.CustomShellContract.Tests` | Base shell classes, registry | NUnit plus FsCheck property tests for the registry and interface |
| `purrTTY.CustomShells.Tests` | `GameConsoleShell`, registry discovery | References `Brutal.ImGui` and `Brutal.ImGui.Abstractions` so `OnConsolePrint` is tested against the real `ConsoleWindow.ErrorColor` / `InfoColor` fields |
| `purrTTY.Display.Tests` | Theme TOML round-trip, all 18 bundled themes parse, override clamping, `AtomicFile`, layouts, startup-command history, target registry | Pure logic, no ImGui calls |

**Quiet by default.** A passing or skipped test produces no output: no `Assert.Pass("msg")`, skips
are `Assert.Ignore()` without a message (reason in a code comment), no console writes on success.
Diagnostics go into assertion failure messages only.[^build-and-test]

**No fixed sleeps.** Tests synchronize on completion signals. For channel-output shells a test calls
`FlushOutputAsync()`, which queues a unique zero-length sentinel and awaits its `OutputReceived`
event; the pump is single-reader FIFO, so the sentinel arriving proves all earlier output was
delivered. Real-process tests poll a condition every 25 ms against a 10 s deadline. A 2026-06 cleanup
removed about 400 sleeps (about 26 s per run) without changing assertions.[^line-shell-tests]

**Pin-bump gates.** `RawCellLayout_MatchesNativeAccessors` and the kitty zlib ground-truth test
must pass after any native library update (gotcha 34).[^gotchas]

**Tooling.** `scripts/dotnet-test.ps1` runs tests to a TRX file with minimal output and
`scripts/test-errors.ts` (Bun) prints only failures; CI publishes TRX results as per-OS check runs.[^release-yml]

## Lifecycle and timing

Not applicable at runtime. The design choice that matters for timing is that every per-frame code
path in the backend is reachable from tests by calling `BuildFrame()` directly, without a KSA frame.

## Key code

| Path | Role |
|---|---|
| [docs/build-and-test.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/build-and-test.md) | The quiet and no-sleep rules with examples |
| [purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs#L77-L108) | Sentinel flush helper |
| [purrTTY.CustomShells.Tests/Unit/GameConsoleShellTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShells.Tests/Unit/GameConsoleShellTests.cs#L439-L480) | Tests against real Brutal color fields |
| [purrTTY.CustomShells.Tests/purrTTY.CustomShells.Tests.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShells.Tests/purrTTY.CustomShells.Tests.csproj) | Game reference DLLs in a test project |
| [purrTTY.Terminal.Tests/UnixProcessManagerTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/UnixProcessManagerTests.cs) | OS-gated real-PTY tests |
| [purrTTY.Terminal.Tests/KittyScreenStreamAssetTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal.Tests/KittyScreenStreamAssetTests.cs) | Regression tests on captured gatOS frames |
| [purrTTY.CustomShellContract.Tests/Property/CustomShellRegistryProperties.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract.Tests/Property/CustomShellRegistryProperties.cs) | FsCheck properties |
| [.github/workflows/release.yml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.github/workflows/release.yml) | Three-OS test matrix |

## Snippets

```csharp
// purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs L85-L107
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
```

## Game APIs touched

`ConsoleWindow.ErrorColor`, `ConsoleWindow.InfoColor` and `ImColor8` from the Brutal reference
assemblies, used as plain static values in tests.

## Pitfalls and version notes

- Some game-runtime dependencies are absent from the reference set: Brutal logging's interpolated
  handler and Tomlyn's POCO serializer need `Microsoft.Extensions.ObjectPool`, which exists in the
  running game but not in the test host, so code under test must avoid them or isolate them
  (gotcha 30).
- Keep game types out of the layers you want to test; purrTTY's backend/frontend seam is what makes
  most of its logic testable.
- OS-specific tests should self-skip silently and run on the matching CI leg.

## See also

- [Cross-platform build and release](./cross-platform-build-and-release.md)
- [Headless terminal surface and frame snapshot](./headless-terminal-surface-and-frame-snapshot.md)
- [Custom shell registry and lifecycle](./custom-shell-registry-and-lifecycle.md)
- [Brutal logging category](./brutal-logging-category.md)
- [purrTTY overview](../overview.md)
- [Run NUnit suites with property tests, sentinel flushes and a three-OS CI matrix](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-4.md) - how-to article demonstrating this feature

[^build-and-test]: purrTTY docs/build-and-test.md
[^line-shell-tests]: purrTTY.CustomShellContract.Tests/Unit/BaseLineBufferedShellTests.cs
[^gotchas]: purrTTY docs/gotchas.md
[^release-yml]: .github/workflows/release.yml
