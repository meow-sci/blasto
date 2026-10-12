---
type: How-To
title: Annotate every game binding, degrade one accessor on failure and keep an impact catalog in lockstep
description: Mark each method that binds to a KSA member with a documentary anchor attribute, latch a failing accessor as degraded instead of crashing, and keep a scope catalog and an upgrade playbook that are updated with the code
category: testing-and-tooling
group: game-updates
topic: upgrade-a-mod-to-a-new-ksa-build
approach: 3
difficulty: advanced
prerequisites: [csharp, agent-skill]
demonstrated_by: [mods/gatos/features/ksa-anchor-churn-tracking]
techniques: [annotation, health-latch, agent-skill]
hooks: []
lifecycle: [event:game-update, frame:after-on-frame]
game_types: [KSA.Program, KSA.Vehicle]
tags: [game-update, upgrade, annotations, graceful-degradation, impact-catalog, health-latch]
---

Outcome: after a game update, a compile break lands on an annotated binding site, a grep finds every reflection target, and a binding that still fails at run time disables only its own feature, once, with the failure visible to scripts and players, instead of retrying a broken call every frame or crashing the mod.
Demonstrated by gatOS, which binds to well over a thousand game members across telemetry, controls, rendering and physics ([gatOS.GameMod/Game/Ksa/KsaAnchor.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaAnchor.cs#L26-L49)). Prerequisites: all game-touching code confined to one project and folder.

## Background: how the game does it

The discipline is a response to how the game changes: members get renamed (compile errors), keep names and change meaning (units, new sentinel values, changed call order), or are reached by reflection (never a compile error). gatOS confines all game-state access to `gatOS.GameMod/Game/Ksa/**` plus two Harmony hook targets, with two immutable records (`SimSnapshot` for reads, `SimCommand` for writes) as the firewall: game types never cross them. That keeps the blast radius small and discoverable and leaves the transports, the `/sim` tree and the VM stack unaffected by an update.
Typical silent drift it records: a time constant converting to a finite number, or a navball value changing meaning. The engine facts live in the decompiled sources ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396) for the vehicle step) and the changelog is `current/version.json` ([current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json#L1-L5)).

## Lifecycle and timing

The anchor attributes are compile-time metadata. The health latches act on the game thread: both the sampler (reads) and the command drain (actuations) run on the game thread in the per-frame hooks (`frame:after-on-frame`), so the latch table needs no locking, and the published list is what crosses to other threads. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define a documentary anchor attribute.** It carries the KSA member bound, the source file it lives in, the date it was last verified, the game version at verification time, a churn risk (low for core vehicle, orbit and time state; medium for the flight computer, input events and per-module controllers; high for FX internals, template internals and anything reached by reflection) and free-form notes. It has no run-time behaviour:

   ```csharp
   // gatOS.GameMod/Game/Ksa/KsaAnchor.cs L26-L49
   [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true)]
   public sealed class KsaAnchorAttribute : Attribute
   {
       /// <param name="member">The KSA member this wraps, e.g. <c>"Vehicle.SetEnum(VehicleEngine)"</c>.</param>
       public KsaAnchorAttribute(string member) => Member = member;

       /// <summary>The KSA member this code binds to.</summary>
       public string Member { get; }

       /// <summary>Source file under <c>thirdparty/ksa</c> where the member lives.</summary>
       public string SourceFile { get; init; } = "";

       /// <summary>ISO date the binding was last verified against the sources.</summary>
       public string Verified { get; init; } = "";

       /// <summary>Game version string at verification time.</summary>
       public string GameVersion { get; init; } = "";

       /// <summary>The member's churn risk (drives re-verification priority).</summary>
       public ChurnRisk Risk { get; init; } = ChurnRisk.Medium;

       /// <summary>Free-form notes (units, NaN behavior, gotchas).</summary>
       public string Notes { get; init; } = "";
   }
   ```

2. **Put one on every method that binds to the game.** The notes accumulate per-build findings, so a reader sees what changed and why the code looks the way it does:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs L20-L23
       [KsaAnchor("Vehicle._manualControlInputs.EngineThrottle (private; set by reflection)",
           SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
           Notes = "No public throttle setter; GetManualThrottle() reads this same field.")]
       internal static CommandResult Set(Vehicle vehicle, double fraction)
   ```

   The churn playbook becomes a grep: when a new build breaks compilation, the failing anchor sites are the work list, and `docs/KSA_INTEGRATION_MATRIX.md` mirrors them as tables.

3. **Latch failures per accessor.** When a reader or actuator throws (typically because the decompiled sources lag the shipping binary), mark that accessor degraded, log once, publish the list, and let a later success clear it:

   ```csharp
   // gatOS.GameMod/Game/Ksa/KsaHealth.cs L18-L45
   internal sealed class KsaHealth
   {
       private readonly Dictionary<string, AccessorHealthSnapshot> _degraded = new();

       /// <summary>True while the named accessor is latched degraded (callers may skip it → EOPNOTSUPP).</summary>
       internal bool IsDegraded(string accessor) => _degraded.ContainsKey(accessor);

       /// <summary>Latches <paramref name="accessor"/> degraded on first fault; logs once.</summary>
       internal void Fault(string accessor, double utSeconds, string error)
       {
           if (_degraded.ContainsKey(accessor))
               return;
           _degraded[accessor] = new AccessorHealthSnapshot(accessor, utSeconds, error);
           ModLog.Log.Warn($"KSA accessor '{accessor}' degraded (logged once): {error}");
       }

       /// <summary>Clears the latch when an accessor succeeds again (logs the recovery once).</summary>
       internal void Clear(string accessor)
       {
           if (_degraded.Remove(accessor))
               ModLog.Log.Info($"KSA accessor '{accessor}' recovered.");
       }

       /// <summary>Snapshot of the currently degraded accessors for <c>/sim/status/accessors</c>.</summary>
       internal IReadOnlyList<AccessorHealthSnapshot> Snapshot()
           => _degraded.Count == 0 ? [] : _degraded.Values.ToArray();
   }
   ```

4. **Refuse latched accessors, fault on exceptions, clear on success.** The dispatch point checks the latch before calling the binding, catches anything the binding throws, and clears the latch when a call succeeds. A degraded command returns a documented "unsupported" result:

   ```csharp
   // gatOS.GameMod/Game/Ksa/KsaCatalog.cs L30-L36
       public CommandResult Execute(SimCommand command)
       {
           var accessor = $"actuator.{command.Action}";
           try
           {
               if (health.IsDegraded(accessor))
                   return new CommandResult(CommandOutcome.Unsupported, $"'{command.Action}' is latched degraded");
   ```

   ```csharp
   // gatOS.GameMod/Game/Ksa/KsaCatalog.cs L129-L151
           }
           catch (Exception ex)
           {
               health.Fault(accessor, SafeUt(), ex.Message);
               return new CommandResult(CommandOutcome.Fault, ex.Message);
           }
       }

       /// <summary>
       ///     The first-class per-vessel controls exempt from the active-vessel authority gate: each is a
       ///     deliberate by-id operation on an arbitrary vessel, placed under the regular vessel area
       ///     rather than <c>/sim/debug</c>. This is a GameMod authority policy — the SimFs layer never
       ///     sees it.
       /// </summary>
       private static readonly HashSet<string> AnyVesselActions =
           new(StringComparer.Ordinal) { SimActions.VesselScale, SimActions.VesselAlwaysRender };

       private CommandResult Finish(string accessor, CommandResult result)
       {
           if (result.IsSuccess)
               health.Clear(accessor);
           return result;
       }
   ```

5. **Publish the degraded list.** It is included in every snapshot as `/sim/status/accessors`, so a script or player can see which part of the integration is down; readers use the same idea (an enrichment pass that fails falls back to core telemetry and logs once).

6. **Keep an impact catalog next to the code.** `scope/FULL_SCOPE.md` inventories every feature and its game coupling, with pages for the read surface, write surface, runtime coupling (threads, hooks, render injection) and assets and versions; a contributor rule makes updating it mandatory with any change to a binding ([scope/FULL_SCOPE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/scope/FULL_SCOPE.md#L1-L22)).

7. **Run the playbook after each update** (also packaged as an agent skill): scan the new build's `version.json` changelog, build against the new DLLs (compile errors at anchor sites are the work list; iterate to green), diff the decompiled sources and content for silent semantic drift, check reflection accessors by hand, re-verify render internals, then fix, re-anchor and re-document. A clean changelog does not clear render internals, which churn faster than gameplay APIs and are not reliably changelog-covered ([.agents/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.agents/skills/upgrade-ksa/SKILL.md#L60-L100)).

8. **Check the compiled mod resolves in the shipping binaries.** The recorded pass for build 5482 also checked that all referenced game types and member references of the compiled mod resolve in the shipping binaries (the metadata approach in [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md) automates that).

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/KsaAnchor.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaAnchor.cs) | `KsaAnchorAttribute` and `ChurnRisk` |
| [gatOS.GameMod/Game/Ksa/KsaHealth.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaHealth.cs) | Per-accessor degraded latches |
| [gatOS.GameMod/Game/Ksa/KsaCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaCatalog.cs) | Refuse latched accessors, fault on exception |
| [scope/FULL_SCOPE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/scope/FULL_SCOPE.md) | Feature inventory and break-check playbook |
| [.agents/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.agents/skills/upgrade-ksa/SKILL.md) | The upgrade procedure as an agent skill |

## Variations and gotchas

- **Annotations rot unless enforced.** The attribute is documentation; the repository's rule that a binding change must update the catalog is what keeps it true.
- **Latches belong to the game thread.** The table is not thread-safe; only the published snapshot list crosses threads.
- **Degrading hides failures from players.** Log once with the accessor name and expose the degraded list so "it does nothing" has an answer.
- **Combine with pins and surfaces.** Anchors tell a human where to look; a generated API surface and compile-time pins ([Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md)) tell a machine.
- **Game updates.** Re-verify the High-risk anchors first; update `Verified` and `GameVersion` only when you actually re-read the member.

## See also

- [Overview](overview.md); [approach 1](upgrade-a-mod-to-a-new-ksa-build-1.md), [approach 2](upgrade-a-mod-to-a-new-ksa-build-2.md)
- [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md), [Isolate patch failures](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)
- Mod feature: [gatOS KSA anchor churn tracking](../../../../mods/gatos/features/ksa-anchor-churn-tracking.md)
