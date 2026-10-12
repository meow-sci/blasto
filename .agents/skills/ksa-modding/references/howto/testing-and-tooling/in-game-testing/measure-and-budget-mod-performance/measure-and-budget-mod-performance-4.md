---
type: How-To
title: Read a mod pass's GPU cost from KSA's profiler while a pinned camera keeps the runs comparable
description: Switch on the game's GPU profiler, tag the mod's dispatch, sample the newest complete frame and report the pass against the whole frame, with a control run and a pinned view
category: testing-and-tooling
group: in-game-testing
topic: measure-and-budget-mod-performance
approach: 4
difficulty: advanced
prerequisites: [starmap, vulkan]
demonstrated_by: [mods/ksarmory/features/raymarched-mushroom-cloud]
techniques: [reflection, logging]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:render, frame:after-on-frame, frame:draw-ui]
game_types: [KSA.GpuProfiler, KSA.Profiler, KSA.FixedController]
tags: [performance, gpu, profiler, compute, camera-pin, control-run]
---

Outcome: after sending the bridge command `cost {"reset":true}` and waiting a few seconds, `cost` returns one line, `gpu: pass 1.84 ms a frame over 212, peak 2.6; whole frame 11.2 ms, peak 14.1, over 240 frames; stages raymarch 1.6, flash 0.2 over 40`, so you know what a custom compute or draw pass costs the GPU and how that compares with the whole frame.
Demonstrated by KSArmory's raymarched cloud pass ([feature](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md)); borrowed from the shaders and rendering topics. Prerequisites: a mod pass that records GPU work and tags it with a profiler tag; a way to hold the camera still ([take screenshots, article 4](../take-screenshots-from-code/take-screenshots-from-code-4.md)).

## Background: how the game does it

A compute dispatch is asynchronous, so timing the C# call measures recording, not work. The engine already writes GPU timestamps around every region tagged with a profiler tag and keeps them per frame:

- `Profiler.Gpu` is a static `GpuProfiler` ([current/decomp/KSA/Profiler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Profiler.cs#L574)) with `IsActive` and `Paused` settable, `Frames` (an array of `ProfilerFrame`) and `LastCompleteFrameIndex` ([current/decomp/KSA/GpuProfiler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuProfiler.cs#L73-L83)).
- A `ProfilerFrame` is valid when it has samples and is not too small, and exposes `AsSpan()` of `Sample`s ([current/decomp/KSA/ProfilerFrame.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProfilerFrame.cs#L30-L39)); a `Sample` has `Ticks`, `ParentIndex` and an `Id` name ([current/decomp/KSA/Sample.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Sample.cs#L9-L23)); `ProfilerWindowBase.TicksToMs` converts ([current/decomp/KSA/ProfilerWindowBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ProfilerWindowBase.cs#L124)).
- The GPU profiler is off by default and records only when active and not paused ([current/decomp/KSA/GpuProfiler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuProfiler.cs#L123)).

## Lifecycle and timing

| Step | Hook |
|---|---|
| Tag the dispatch | Inside the mod's pass recording, in the render phase (`frame:render`), with a tag name the profiler stores |
| `Sample()` every frame | Early in the mod's per-frame step (`frame:draw-ui` / `frame:after-on-frame`), "read before anything else this frame" ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L918-L922)) |
| Pin the view | The bridge camera claimant or the scenario's linger phase holds one pose ([take screenshots, article 4](../take-screenshots-from-code/take-screenshots-from-code-4.md)) |

It takes the **last complete** frame index, so a frame still being written is never half-read, and it remembers the index so one frame is not counted twice when rendering outruns the simulation. See the [render phases](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Switch the profiler on and start clean** (`Begin`), catching failures and warning once:

   ```csharp
   // src/KSArmory/Ksa/CloudPassCost.cs L43-L70
       /// <summary>Turns KSA's GPU profiler on, which it need not be.</summary>
       public static void Begin()
       {
           try
           {
               Profiler.Gpu.IsActive = true;
               Profiler.Gpu.Paused = false;
           }
           catch (Exception e)
           {
               Warn($"could not start the GPU profiler: {e.Message}");
           }

           Reset();
       }

       /// <summary>Forgets what has been measured, so a run starts clean.</summary>
       public static void Reset()
       {
           _frames = 0;
           _passFrames = 0;
           _framePeakMs = 0.0;
           _passTotalMs = 0.0;
           _passPeakMs = 0.0;
           _frameTotalMs = 0.0;
           _lastFrameIndex = -1;
           _stages.Clear();
       }
   ```

2. **Sample the newest complete frame each step.** Sum root samples for the whole frame (a nested region is already inside its parent), pick out the pass by its tag name and its stages by prefix, and count the frame whether or not the pass ran, so a control exists:

   ```csharp
   // src/KSArmory/Ksa/CloudPassCost.cs L77-L138
       public static void Sample()
       {
           try
           {
               if (!Profiler.Gpu.IsActive) return;

               int index = Profiler.Gpu.LastCompleteFrameIndex;
               if (index < 0 || index == _lastFrameIndex) return;

               ProfilerFrame[] frames = Profiler.Gpu.Frames;
               if (index >= frames.Length) return;

               ProfilerFrame frame = frames[index];
               if (!frame.IsValid) return;

               _lastFrameIndex = index;

               double pass = 0.0;
               double whole = 0.0;
               _frameStages.Clear();

               foreach (Sample sample in frame.AsSpan())
               {
                   double ms = ProfilerWindowBase.TicksToMs(sample.Ticks);

                   // Root samples only for the frame's own total: a nested region is already inside
                   // its parent, so summing everything counts the same microsecond several times.
                   if (sample.ParentIndex == ushort.MaxValue) whole += ms;

                   string name = sample.Id.ToString();
                   if (name == TagName) pass += ms;
                   else if (name.StartsWith(StagePrefix, StringComparison.Ordinal))
                   {
                       _frameStages[name] = _frameStages.GetValueOrDefault(name) + ms;
                   }
               }

               foreach ((string name, double ms) in _frameStages)
               {
                   (double total, int count) = _stages.GetValueOrDefault(name);
                   _stages[name] = (total + ms, count + 1);
               }

               // The frame is counted whether or not the pass ran. Without that there is no baseline:
               // a 46 ms GPU frame means nothing until the same scene has been measured with the pass
               // off, and a number with no control is what made this instrument look decisive when it
               // had not yet said anything.
               _frames++;
               _frameTotalMs += whole;
               _framePeakMs = Math.Max(_framePeakMs, whole);

               if (pass <= 0.0) return;

               _passFrames++;
               _passTotalMs += pass;
               _passPeakMs = Math.Max(_passPeakMs, pass);
           }
           catch (Exception e)
           {
               Warn($"could not read the GPU profiler: {e.Message}");
           }
       }
   ```

3. **Report the pass against the frame**, including a distinct message for a pass that failed to build (a shader error that the C# build cannot catch looks identical to a deliberate control run otherwise):

   ```csharp
   // src/KSArmory/Ksa/CloudPassCost.cs L140-L176
       /// <summary>What it cost, as a line somebody reads. Empty when nothing was measured.</summary>
       public static string Report()
       {
           if (_frames <= 0) return string.Empty;

           double frame = _frameTotalMs / _frames;

           // Said even when the pass never ran, because that is the baseline the other run is read
           // against and a run that reports nothing looks like a run that measured nothing.
           //
           // And a pass that FAILED to build says so rather than reading as one switched off: as one
           // line, a shader that will not compile -- which the C# build cannot catch, because KSA
           // compiles it at load -- is indistinguishable from the deliberate noshader control.
           //
           // Asked of a build that RAN and failed, not of there being no pipeline. A pass switched
           // off is never asked to build, so reading the absence of one as a failure puts "FAILED TO
           // BUILD" on every control run -- the same confusion with the sign flipped, and worse, because a control is the run that is supposed to report nothing.
           string had = _passFrames > 0
                            ? $"pass {_passTotalMs / _passFrames:F2} ms a frame over {_passFrames}, "
                              + $"peak {_passPeakMs:F2}"
                            : CloudPass.BuildFailed ? "pass FAILED TO BUILD" : "pass off";

           // What a ground mark dispatches over, beside what the pass costs: a mark is permanent, so
           // the share of the screen it covers is what it costs for the rest of the session.
           string marks = CloudPass.LastMarkTile < 1.0
                              ? $", a mark covers {CloudPass.LastMarkTile:P1} of the screen"
                              : string.Empty;

           // Each stage for the frames it ran in: the flash runs only while a glare lingers, and its mean
           // over frames it never ran in would hide what it costs when it does.
           string stages = string.Join(", ", _stages.Select(
               s => $"{s.Key[StagePrefix.Length..]} {s.Value.TotalMs / s.Value.Frames:F2}"
                    + (s.Value.Frames < _passFrames ? $" over {s.Value.Frames}" : "")));

           return $"gpu: {had}; whole frame {frame:F2} ms, peak {_framePeakMs:F2}, over {_frames} frames"
                  + marks + (stages.Length > 0 ? $"; stages {stages}" : "");
       }
   ```

4. **Expose it on the bridge** as `cost`; `reset` starts the profiler and clears:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L1109-L1123
       // What the pass costs the GPU since the last reset, against the whole frame beside it. A reset
       // turns KSA's profiler on, which it is not by default; paused, the frames still render, so a
       // reset and a read a few seconds later measure one frozen instant.
       private static Reply Cost(BridgeCommand command)
       {
           if (command.Flag("reset", false))
           {
               CloudPassCost.Begin();
               return Done();
           }

           return CloudPassCost.HasSamples
                      ? Done(new Dictionary<string, object?> { ["cost"] = CloudPassCost.Report() })
                      : Failed("nothing measured -- send cost with reset first");
       }
   ```

5. **Call `Sample()` from the frame step** before other work, then run: pause the world (frames still render, so a reset and a read a few seconds later measure one frozen instant), pin the camera with `camera`, send `cost {"reset":true}`, wait, send `cost`.

6. **Do the control.** Run the same scene with the pass off (the scenario option `noshader`) and compare `whole frame`; "a number with no control looks decisive before it has said anything", in the mod's words. The scenario runner calls `CloudPassCost.Begin()` itself at start ([src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs#L558)), so each capture of a scripted run can report the cost.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPassCost.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassCost.cs) | Begin, Reset, Sample, Report |
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs) | `cost` command |
| [src/KSArmory/Ksa/CloudWatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudWatch.cs) | The pinned pose used for the measurement |
| [src/KSArmory/Ksa/ScenarioRunner.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ScenarioRunner.cs) | Starts the profiler for scripted runs |

## Variations and gotchas

- **Tag names are matched as strings.** The profiler stores the name; a tag object of the mod's own is not what comes back. Keep the constants in step with the tag you create.
- **Everything is reflection-light but internal-adjacent.** `Profiler.Gpu` and `ProfilerFrame` are public here, but the profiler is a debug facility; guard with try/catch and warn once, as the class does.
- **Pause for a fair measurement.** The mod found that paused frames still render, so a paused, pinned run measures a frozen instant. Moving scenes need longer windows and a control.
- **Pinned pose.** The same pinned pose is used by the dev bridge camera and by the drop scenario's linger phase, so numbers from the two are comparable.
- **Profiler overhead.** The GPU profiler is off by default for a reason; leave it off outside measurements.

## See also

- [Overview](overview.md), [article 1](measure-and-budget-mod-performance-1.md)
- [Modify the HDR scene with a compute pass](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md), [Take clean screenshots from code](../take-screenshots-from-code/overview.md)
- [KSArmory raymarched mushroom cloud](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md)
