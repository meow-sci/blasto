# Game Feature

* [Fly scripted scenarios unattended, report PASS or FAIL and A/B test variants](overview.md) - Run a developer-only state machine from a request file at launch, load a save and play a scripted engagement, log tagged lines that a shell harness turns into an exit code, and compare variants with rank statistics

# How-To

* [A request file, a state machine, wall-clock and sim-time budgets and tagged SCENARIO lines](run-unattended-scenarios-and-report-a-verdict-1.md) - Build a developer-only scenario runner that reads a request file once at launch, loads a save, closes engine popups, enforces budgets and writes a PASS, FAIL or TIMEOUT line that a shell harness maps to an exit code
* [A seeded, interleaved overnight batch with rank-sum verdicts, and within-run variant splits](run-unattended-scenarios-and-report-a-verdict-3.md) - Fly many unattended runs of competing mod variants in randomised order with every log kept, then decide with an exact rank test and a Hodges-Lehmann interval, or split variants inside one run
* [Bridge commands that load a save and step an exact number of simulated seconds](run-unattended-scenarios-and-report-a-verdict-2.md) - Script a deterministic in-game check from outside by loading a save through a closure that waits for a flown craft and stepping the world by summing the engine's last simulation step
