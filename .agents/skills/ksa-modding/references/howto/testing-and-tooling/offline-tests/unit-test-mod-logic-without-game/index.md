# Game Feature

* [Unit-test mod logic without launching the game](overview.md) - Choose where the game boundary sits so maths, models, parsers and state machines compile with no KSA assembly, then run them as tests on any machine and in CI

# How-To

* [Check a model with a console test executable that returns a verdict as its exit code](unit-test-mod-logic-without-game-2.md) - Source-link a game-free model file into a plain Exe, assert physical invariants against synthetic inputs and stock reference values, and let the process exit code be the test result
* [Check numerics against an independent implementation with console checks and Python reference fixtures](unit-test-mod-logic-without-game-3.md) - Put solver and numerics code in game-free assemblies joined to the mod by project reference, then compare their output with JAX and CVXPY fixtures from console checks that a CI script runs
* [Compile-gate the game-facing half with partial methods and gate slow tests behind an opt-in switch](unit-test-mod-logic-without-game-5.md) - Drop the whole Game folder out of compilation when the KSA assemblies are absent, join it to the game-free half with partial methods, and let integration tests skip themselves unless an environment variable opts in
* [Link a game-free Sim folder wholesale into an xUnit project](unit-test-mod-logic-without-game-1.md) - Keep every KSA-free class under one folder, compile that folder into a test project that references no KSA assembly, and let the compiler enforce the boundary
* [Run NUnit suites with property tests, sentinel flushes and a three-OS CI matrix](unit-test-mod-logic-without-game-4.md) - Keep game types out of the tested layers, drive async output deterministically with a sentinel instead of sleeps, gate platform-specific tests per operating system and run the suite quietly on Windows, Linux and macOS
