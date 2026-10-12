---
type: How-To
title: Nested budget spans with top-level totals, the worst frame and rate-limited overrun warnings
description: Book named spans against the frame, add up only the outermost, report mean and worst per span every ten seconds and rate-limit a warning that would otherwise repeat every frame
category: testing-and-tooling
group: in-game-testing
topic: measure-and-budget-mod-performance
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/starmap-entry-and-fault-containment]
techniques: [logging, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe]
tags: [performance, budget, spans, worst-frame, rate-limit]
---

Outcome: one log line every ten seconds, `mod frame: 1.84 ms mean, 7.10 ms worst, over 600 frames | sim 1.20/5.50 | ui 0.40/1.10 | ...`, listing every span by cost, and a warning about a world step too long to integrate that reports the first occurrence and then one in 120.
Demonstrated by KSArmory ([starmap entry and fault containment](../../../../mods/ksarmory/features/starmap-entry-and-fault-containment.md)). Prerequisites: StarMap; a mod with enough per-frame sections that "where did the frame go" is a real question.

## Background: how the game does it

KSA runs the mod's frame hooks inside its own frame, so the mod's total is part of `Program.OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2275)), and the world is advanced by the step `ApplyVehicleSolvers` applies ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1948)). The cap behind KSArmory's comment is `App.Run`, which clamps `dtPlayer` to `1 / GameSettings.Current.Simulation.MinTargetFrameRate` ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L51-L53); the field defaults to 30, [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1045)), and `Universe.GetJobSimStep` scales that by the simulation speed ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627)). So the engine advances by at most a thirtieth of a second of player time per frame at the default setting and `sim rate = 33.3 ms / frame time` below 30 FPS;
on eight rockets the frame cost 78.7 ms of which the engine's vehicle solver took 13 to 20, leaving about sixty milliseconds belonging to nobody ([src/KSArmory/Sim/FrameBudget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FrameBudget.cs#L8-L12)). Three suspects had been eliminated by switching them off one at a time, which clears only what somebody thought of; measuring every span is the alternative.

## Lifecycle and timing

Spans are opened in the hooks that do the work: the simulation step (`StepSimulation`, run from `StarMapAfterGui` or a fallback on frames without UI), the UI pass (`frame:draw-ui`) and the effects sweeps. `EndFrame` runs once per frame near the end of the step, and the report is logged from there when `Due` ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L860-L862)).
Spans nest (the GUI pass contains the panel; the simulation contains the ballistic computers), so only spans marked `top: true` add to the frame total. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **A span type and a book**: a `readonly struct Span` that books elapsed milliseconds against its name on `Dispose`. Two timestamp reads, a dictionary add, and a string built only every `ReportIntervalSeconds`:

   ```csharp
   // src/KSArmory/Sim/FrameBudget.cs L25-L65
   internal sealed class FrameBudget
   {
       /// <summary>How often it reports, in real seconds. Long enough that the line is rare.</summary>
       public const double ReportIntervalSeconds = 10.0;

       private readonly Dictionary<string, double> _total = [];
       private readonly Dictionary<string, double> _worst = [];
       private readonly Dictionary<string, double> _thisFrame = [];

       private double _wall;
       private int _frames;
       private double _worstFrameMs;
       private double _thisFrameTopMs;
       private double _totalTopMs;

       /// <summary>A span being timed. Disposing it books the time against its name.</summary>
       public readonly struct Span(FrameBudget budget, string name, long from, bool top) : IDisposable
       {
           public void Dispose() =>
               budget.Book(name, Stopwatch.GetElapsedTime(from).TotalMilliseconds, top);
       }

       /// <summary>Time a block of work: <c>using (budget.Measure("icbm")) { ... }</c>.</summary>
       /// <param name="top">
       /// Whether this span is one of the outermost, and so counts toward the frame's total.
       ///
       /// <para><b>Spans nest</b> — the GUI pass contains the panel, the simulation contains the
       /// ballistic computers — so summing all of them counts the inner ones twice and reports a mod
       /// costing more of the frame than it does. Only the outermost are added up; the rest are
       /// detail about where an outer one went.</para>
       /// </param>
       public Span Measure(string name, bool top = false) =>
           new(this, name, Stopwatch.GetTimestamp(), top);

       /// <summary>Book time against a name directly, for a span a `using` cannot bracket.</summary>
       public void Book(string name, double ms, bool top = false)
       {
           _thisFrame[name] = (_thisFrame.TryGetValue(name, out double had) ? had : 0.0) + ms;

           if (top) _thisFrameTopMs += ms;
       }
   ```

2. **Close the frame, then report when due, listing spans dearest first** and including the worst per span and the worst frame:

   ```csharp
   // src/KSArmory/Sim/FrameBudget.cs L68-L119
       public void EndFrame(double wallSeconds)
       {
           if (double.IsFinite(wallSeconds) && wallSeconds > 0.0) _wall += wallSeconds;

           _frames++;

           foreach ((string name, double ms) in _thisFrame)
           {
               _total[name] = (_total.TryGetValue(name, out double sum) ? sum : 0.0) + ms;

               if (!_worst.TryGetValue(name, out double worst) || ms > worst) _worst[name] = ms;
           }

           if (_thisFrameTopMs > _worstFrameMs) _worstFrameMs = _thisFrameTopMs;

           _totalTopMs += _thisFrameTopMs;
           _thisFrameTopMs = 0.0;
           _thisFrame.Clear();
       }

       /// <summary>Whether there is a window worth reporting.</summary>
       public bool Due => _wall >= ReportIntervalSeconds && _frames > 0;

       /// <summary>
       /// One line, and the window is reset. Spans are listed dearest first, because the question
       /// this exists to answer is which one to look at.
       /// </summary>
       public string Take()
       {
           double mean = _totalTopMs / _frames;

           List<string> names = [.. _total.Keys];
           names.Sort((a, b) => _total[b].CompareTo(_total[a]));

           var said = new System.Text.StringBuilder();
           said.Append($"mod frame: {mean:F2} ms mean, {_worstFrameMs:F2} ms worst, over {_frames} frames");

           for (int i = 0; i < names.Count; i++)
           {
               string name = names[i];
               said.Append($" | {name} {_total[name] / _frames:F2}/{_worst[name]:F2}");
           }

           _total.Clear();
           _worst.Clear();
           _wall = 0.0;
           _frames = 0;
           _worstFrameMs = 0.0;
           _totalTopMs = 0.0;

           return said.ToString();
       }
   ```

3. **Mark the outermost spans** `top: true` and measure the rest as detail:

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L341-L351
           if (KsaWorld.InFlightScene)
           {
               using (_budget.Measure("sim", top: true)) StepSimulation(dtPlayer);
           }

           // Here rather than in the GUI hook, and outside the flight gate above so a view is still
           // handed back on the way out. Driving a camera is not drawing: hiding the UI takes the GUI
           // pass away entirely, and a mod that stops restating a view it has borrowed leaves the
           // player riding a frozen offset with no closing, no transition and no aim -- while the
           // world it is pointed at carries on.
           using (_budget.Measure("cameras")) DriveCameras(dtPlayer);
   ```

   Many small spans inside the step name each piece (`consume`, `clock`, `warp`, `fire`, `heads`...) so the line says which one grew ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L718-L795)).

4. **End the frame and print it:**

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L857-L862
               // The other side of that subtraction: SolverLoad says what the frame and the engine's
               // solver cost, this says what of the remainder is ours. Closed here so both windows
               // are the same window.
               _budget.EndFrame(sinceLast);

               if (_budget.Due) Log.Debug(_budget.Take());
   ```

5. **Rate-limit warnings that would repeat.** The world step can be longer than a round can be integrated across (a scene load reports a step tens of seconds long). `OverrunLog` counts overruns, discards the excess, and says something on the first and then one in 120, with two counters so the load frame's idle overrun cannot spend the report the first real lagging overrun needs:

   ```csharp
   // src/KSArmory/Sim/OverrunLog.cs L33-L62
       /// <summary>
       /// Report the first, then one in this many. Sustained warp overruns every frame, and a line
       /// per frame buries the first one — which is the only one that says when it started.
       /// </summary>
       public const int ReportEvery = 120;

       private int _lagging;
       private int _idle;

       /// <summary>Overrun frames this session, both kinds.</summary>
       public int Frames => _lagging + _idle;

       /// <summary>Simulated seconds the clamp has thrown away, both kinds.</summary>
       public double DiscardedSeconds { get; private set; }

       /// <summary>
       /// Counts one overrun frame and answers what to say about it.
       /// </summary>
       /// <param name="stepSeconds">The step the world took, longer than one a round can integrate.</param>
       /// <param name="anyInFlight">Whether anything was in the air to be left behind by the clamp.</param>
       public Notice Observe(double stepSeconds, bool anyInFlight,
                             double maxStep = Interceptor.MaxFaithfulStep)
       {
           DiscardedSeconds += stepSeconds - maxStep;

           int seen = anyInFlight ? ++_lagging : ++_idle;
           if (seen != 1 && seen % ReportEvery != 0) return Notice.Silent;

           return anyInFlight ? Notice.Lagging : Notice.Idle;
       }
   ```

6. **Run and read.** Fly a scenario ([unattended scenarios](../run-unattended-scenarios-and-report-a-verdict/overview.md)) and grep the log for `mod frame:`. The worst-frame column is the one players feel; a mean hides a cost that arrives one frame in thirty.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/FrameBudget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FrameBudget.cs) | Spans, totals, worst, report line |
| [src/KSArmory/Sim/OverrunLog.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/OverrunLog.cs) | Rate-limited overrun notices |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Where spans are opened and closed |

## Variations and gotchas

- **Do not sum nested spans.** Only the outermost count toward the total; counting inner ones too reports a mod costing more than it does.
- **Report the worst beside the mean.** One frame in thirty is invisible in an average.
- **Rate limits need one counter per kind of event.** A shared counter lets a harmless event spend the report a real one needs.
- **Debug level.** The line is logged at `Debug`; scenario runs set the threshold to `Debug` with the `verbose` option ([scenario article 1](../run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-1.md)).

## See also

- [Overview](overview.md), [article 1](measure-and-budget-mod-performance-1.md), [article 2](measure-and-budget-mod-performance-2.md)
- [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)
- [KSArmory starmap entry and fault containment](../../../../mods/ksarmory/features/starmap-entry-and-fault-containment.md)
