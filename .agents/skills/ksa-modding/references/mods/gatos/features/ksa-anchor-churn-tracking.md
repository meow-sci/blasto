---
type: Mod Feature
title: KSA anchor churn tracking
description: Every method that touches a KSA member carries a documentary KsaAnchor attribute with the member, source file, verification date, game version and churn risk, backed by runtime health latches and a scope catalog plus an upgrade playbook that turn a new game build into a bounded work list
mod: gatos
category: testing-and-tooling
techniques: [custom-attribute, health-latch, upgrade-playbook]
hooks: []
lifecycle: [frame:viewports, frame:draw-ui]
game_types: []
tags: [maintenance, game-updates, attributes, health, documentation, upgrade]
---

KSA ships builds often, and gatOS binds to well over a thousand game members across telemetry,
controls, rendering and physics. Its answer to "will this update break us, and where" is a
discipline more than a library: annotate every binding site, degrade per accessor at runtime instead
of crashing, and keep a human-readable impact catalog in lockstep with the code. This is the most
transferable maintenance technique in the repo.

## How it works

**`[KsaAnchor]`** ([gatOS.GameMod/Game/Ksa/KsaAnchor.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaAnchor.cs#L7-L49)): a documentary attribute on
methods and properties with `Member` (the KSA member bound, for example
`"Vehicle.SetEnum(VehicleEngine.MainIgnite)"`), `SourceFile`, `Verified` (ISO date), `GameVersion`,
`Risk` (`Low` for core vehicle/orbit/time state, `Medium` for flight computer, input events and
per-module controllers, `High` for FX internals, template internals and anything reached by
reflection) and free-form `Notes`. The notes accumulate per-build findings ("5348: ...",
"5482: ..."), so a reader sees exactly what changed and why the code looks the way it does. It has no
runtime behavior; the point is that a compile break lands on an annotated site, and a grep finds
every reflection target.

**Runtime health latches** ([gatOS.GameMod/Game/Ksa/KsaHealth.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaHealth.cs)): when an actuator throws,
`KsaCatalog` calls `KsaHealth.Fault(accessor, ut, error)`, which logs once and marks that accessor
degraded; later commands for it return `EOPNOTSUPP` instead of retrying a broken binding every
frame. A later success clears it. The list is published in every snapshot as
`/sim/status/accessors`, so a script or player can see which part of the integration is down.
Readers use the same idea: an enrich-pass failure falls back to core telemetry and logs once.

**Scope catalog and playbook.** `scope/FULL_SCOPE.md` inventories every feature and its KSA
coupling, with per-area pages for the read surface, write surface, runtime coupling (threads, hooks,
render injection) and assets/versions; `docs/KSA_INTEGRATION_MATRIX.md` mirrors the anchors as
tables ([scope/FULL_SCOPE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/scope/FULL_SCOPE.md#L22-L60)). The upgrade playbook, also packaged as an agent skill
([.agents/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.agents/skills/upgrade-ksa/SKILL.md)), is: scan the new build's `version.json` changelog,
build against the new DLLs (compile errors at anchor sites are the work list; iterate to green),
diff the decomp and Content for silent semantic drift, check reflection accessors by hand, re-verify
render internals, then fix, re-anchor and re-document. The recorded 5482 pass also checked that all
referenced game types and member references of the compiled mod resolve in the shipping binaries.

## Lifecycle and timing

The attributes are compile-time metadata. Health latches act on the game thread inside the command
drain and the sampler, and are read by transports through the published snapshot.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/KsaAnchor.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaAnchor.cs) | `KsaAnchorAttribute` and `ChurnRisk` |
| [gatOS.GameMod/Game/Ksa/KsaHealth.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaHealth.cs) | per-accessor degraded latches |
| [gatOS.GameMod/Game/Ksa/KsaCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaCatalog.cs#L30-L35) | refuse latched accessors, fault on exception |
| [docs/KSA_INTEGRATION_MATRIX.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/KSA_INTEGRATION_MATRIX.md) | the per-path binding table |
| [scope/FULL_SCOPE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/scope/FULL_SCOPE.md) | feature inventory and break-check playbook |
| [scope/ksa-assets-and-versions.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/scope/ksa-assets-and-versions.md) | per-build pass records |
| [.agents/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.agents/skills/upgrade-ksa/SKILL.md) | the upgrade procedure as an agent skill |

## Snippets

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

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs L20-L23
    [KsaAnchor("Vehicle._manualControlInputs.EngineThrottle (private; set by reflection)",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
        Notes = "No public throttle setter; GetManualThrottle() reads this same field.")]
    internal static CommandResult Set(Vehicle vehicle, double fraction)
```

## Game APIs touched

None directly; it documents the bindings used elsewhere.

## Pitfalls and version notes

- The compiler cannot see reflection targets or semantic changes (renamed meaning, new sentinel
  values, changed call order); the notes record several such silent drifts, for example
  `UniverseTime.EndOfTime` converting to a finite number and `NavBallData.DeltaV` changing meaning.
- Decompiled sources can lag the shipping binary; the playbook says to confirm against runtime
  behavior when they disagree.
- gatOS's anchors were last verified at 2026.9.22.5482; this knowledge base checked the members it
  cites against 2026.10.10.5554.

## See also

- [Compile-gated game seam](compile-gated-game-seam.md)
- [Build, deploy and CI](build-deploy-and-ci.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [gatOS overview](../overview.md)
- [Annotate every game binding, degrade one accessor on failure and keep an impact catalog in lockstep](../../../howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-3.md) - how-to article demonstrating this feature

