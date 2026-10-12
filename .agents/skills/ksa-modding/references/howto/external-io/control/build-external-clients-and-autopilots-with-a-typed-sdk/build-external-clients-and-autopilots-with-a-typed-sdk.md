---
type: How-To
title: Typed SDK with a Transport interface, plus example autopilot programs
description: Build a client where a single Transport interface hides whether the game is reached through files or HTTP, then write a landing or guidance program that paces by sim time, gates on telemetry freshness and batches related writes
category: external-io
group: control
topic: build-external-clients-and-autopilots-with-a-typed-sdk
difficulty: intermediate
prerequisites: [gatos-api]
demonstrated_by: [mods/gatos/features/external-api-clients]
techniques: [external-client, background-thread]
hooks: []
lifecycle: [thread:background]
game_types: []
tags: [sdk, transport, errno, autopilot, sim-time, stale-telemetry, batch, typescript, rust]
---

Outcome: a small typed client (`GatosClient`) over one `Transport` interface with a file-system implementation and an HTTP implementation, so a script written once runs inside the VM guest, on the host or on another machine; and a pattern for companion programs that fly a vessel safely. Requires a mod that publishes a game API; the worked example is gatOS, whose client lives in `examples/sdk-ts` and whose autopilots are `land-o-matic`, `agc` and `photog-rs`. Demonstrated by [External API clients and SDK](../../../../mods/gatos/features/external-api-clients.md).

## Background: how the game does it

The game has no scripting API. gatOS publishes it twice: as a 9p file tree (`/sim`, mounted in the guest) and as an HTTP `/v1` API on the host. The per-vessel `telemetry` document is byte-identical on both, and a command has one shape (`vessel_id`, `action`, `ordinal`, `value`, `values`, `token`, `aux`) that the file transport maps to a path and the HTTP transport posts as JSON. The server side of those two surfaces is in [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md) and [Connect a VM guest to the game](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md); both read the immutable snapshot of [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md) and write through the queue of [Marshal commands from background threads to the game thread](../marshal-commands-to-the-game-thread/overview.md).

## Lifecycle and timing

The client is outside the game process. On the mod side, its reads are `thread:background` snapshot reads and its writes are queue submissions that resolve at the next game-thread drain (see [the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

- Telemetry is published at `sample_rate_hz` (default 10, clamped 1 to 120); the sampler idles while no transport client is connected, so the first read after a long idle may carry an old `seq` until sampling resumes.
- Frame-phase writes land at the next frame drain; flight-computer setpoints land at the solver-phase drain and become visible after the following step is applied. Read back before assuming.
- A game thread that is not reaching its drain (loading, a stalled frame) leaves writes unexecuted: they return `ETIMEDOUT`. A simulation pause (`sim_dt == 0`) does not stop the drain, because `Program.OnFrame` and the main-viewport hook keep running; frame-phase writes still execute, they just have no time to act on.

## Steps

### Step group A: the Transport abstraction

1. **Define one interface for everything a client needs**, with typed reads and one `command` method. Keep it free of transport detail:

   ```typescript
   // examples/sdk-ts/src/transport.ts L10-L25
   export interface Transport {
     vesselIds(): Promise<string[]>;
     vesselTelemetry(id: string): Promise<VesselTelemetry>;
     time(): Promise<TimeInfo>;
     bodies(): Promise<Body[]>;
     command(c: Command): Promise<void>;
     events(): AsyncIterable<SimEvent>;
     /** Blocks until sim time reaches `ut`; returns the reached ut. */
     waitUntilUt(ut: number): Promise<number>;
   }

   /** The default transport: HTTP when `$GATOS_HTTP` is set, else the `/sim` filesystem. */
   export function defaultTransport(): Transport {
     const http = process.env.GATOS_HTTP;
     return http ? new HttpTransport(http) : new FsTransport();
   }
   ```

2. **Implement the file transport.** Reads are file reads and writes are `writeFile`; a failed `write(2)` surfaces its errno code, so a rejected command fails like any Unix write:

   ```typescript
   // examples/sdk-ts/src/transport.ts L70-L78
     async command(c: Command): Promise<void> {
       const { path, payload } = fileCommand(c);
       try {
         await writeFile(`${this.root}/${path}`, `${payload}\n`);
       } catch (err) {
         const code = (err as NodeJS.ErrnoException).code ?? "EIO";
         throw new GatosError(code, `write ${path} failed`);
       }
     }
   ```

3. **Implement the HTTP transport.** Reads are `GET`s, writes are `POST /command` with the same command JSON, and error bodies map back to the same errno vocabulary through a status table:

   ```typescript
   // examples/sdk-ts/src/transport.ts L124-L134
     async command(c: Command): Promise<void> {
       const response = await fetch(`${this.base}/command`, {
         method: "POST",
         headers: { "content-type": "application/json" },
         body: JSON.stringify(c),
       });
       if (!response.ok) {
         const body = (await response.json().catch(() => ({}))) as { errno?: string; message?: string };
         throw new GatosError(errnoForStatus(response.status, body.errno), body.message ?? "command failed");
       }
     }
   ```

   ```typescript
   // examples/sdk-ts/src/errors.ts L17-L36
   /** Maps an HTTP status (with an optional `{errno}` body) to the frozen vocabulary. */
   export function errnoForStatus(status: number, bodyErrno?: string): Errno | string {
     if (bodyErrno) return bodyErrno;
     switch (status) {
       case 400:
         return "EINVAL";
       case 403:
         return "EACCES";
       case 404:
         return "ENOENT";
       case 409:
         return "EBUSY";
       case 501:
         return "EOPNOTSUPP";
       case 504:
         return "ETIMEDOUT";
       default:
         return "EIO";
     }
   }
   ```

4. **Map a command to a path in exactly one place** (the file transport's `fileCommand`), and throw `EINVAL` for an action with no mapping rather than writing to a guessed path ([examples/sdk-ts/src/transport.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/src/transport.ts#L170-L244)).

5. **Layer the typed client on top.** `GatosClient` and `VesselHandle` offer `ignite()`, `setThrottle()`, `setAttitudeTarget()`, `burn()` and so on, each building a `Command`, and the client picks its transport from `$GATOS_HTTP`, defaulting to the file system (`defaultTransport()`).

6. **Make time explicit.** The client's own sleeping primitive waits for sim time, never wall time, and a wrapper holds the warp factor while a hand-flown loop runs (`debug.warp` needs the mod's debug namespace enabled):

   ```typescript
   // examples/sdk-ts/src/client.ts L34-L62
     /** Blocks until sim time reaches `ut` (the alarm device / long-poll); returns the reached ut. */
     waitUntilUt(ut: number): Promise<number> {
       return this.transport.waitUntilUt(ut);
     }

     /** Sleeps `seconds` of <b>sim</b> time (warp-correct) — never `setTimeout` against wall time. */
     async sleepSim(seconds: number): Promise<number> {
       const now = (await this.transport.time()).ut;
       return this.transport.waitUntilUt(now + seconds);
     }

     /** Sets the time-warp factor (debug namespace; throws EACCES if debug is disabled). */
     setWarp(factor: number): Promise<void> {
       return this.transport.command({ vessel_id: "", action: "debug.warp", value: factor });
     }

     /**
      * Runs `fn` at 1× warp, restoring the previous factor afterwards — the safe wrapper for a
      * hand-flown closed-loop maneuver (control loops are only sound near realtime).
      */
     async atWarp<T>(factor: number, fn: () => Promise<T>): Promise<T> {
       const before = (await this.time()).warp;
       await this.setWarp(factor);
       try {
         return await fn();
       } finally {
         await this.setWarp(before);
       }
     }
   ```

The small example programs show the shapes: [examples/sdk-ts/examples/launch-monitor.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/examples/launch-monitor.ts) (reactive: follow the event stream, shut down on `flameout`), [examples/sdk-ts/examples/solar-keeper.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/examples/solar-keeper.ts) (a sensor-to-actuator loop that paces with `sdk.sleepSim(5)`) and an orbit watcher. The HTTP surface also has an OpenAPI document ([sim_openapi.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/sim_openapi.yml#L1-L5)) for generating clients in other languages.

### Step group B: writing a companion program (landing guidance and the like)

7. **Pace by sim time.** The tutorial reference states the rule: do not `sleep(dt)`; block until a sim time with the alarm file (in the guest) or `GET /v1/time/wait?until=<ut>` (host), and in a tight loop integrate off the change in `ut` between ticks, whose ceiling is the sample rate ([docs/TUTORIAL_DATA_REFERENCE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/TUTORIAL_DATA_REFERENCE.md#L271-L300)).

8. **Gate every tick on freshness.** Hold control (stop writing, keep the last command, show a banner) when the game is paused (`time/sim_dt == 0`), time-warping (`warp > 1`, closed-loop control is only sound near 1x) or the telemetry `seq` has not advanced for three ticks. Paused ticks keep the same `seq` but must not count as stale. The AGC bridge isolates the logic in one small type:

   ```rust
   // examples/agc/src/clockpolicy.rs L32-L64
   impl ClockPolicy {
       pub fn new() -> Self {
           Self::default()
       }

       /// Evaluates one telemetry tick. `sim_dt == Some(0.0)` ⇒ paused.
       pub fn gate(&mut self, seq: u64, ut: f64, warp: f64, sim_dt: Option<f64>) -> Verdict {
           let paused = sim_dt == Some(0.0);
           let stalled = self.prev_seq == Some(seq) && !paused;
           self.stale_count = if stalled { self.stale_count + 1 } else { 0 };
           self.prev_seq = Some(seq);

           let hold = if paused {
               Some(Hold::Paused)
           } else if warp > 1.0 {
               Some(Hold::Warp)
           } else if self.stale_count >= 3 {
               Some(Hold::Stale)
           } else {
               None
           };

           let resync = match (self.holding, hold) {
               (None, Some(_)) => {
                   self.hold_started_ut = ut;
                   None
               }
               (Some(_), None) => Some(ut - self.hold_started_ut),
               _ => None,
           };
           self.holding = hold;
           Verdict { hold, resync }
       }
   ```

   land-o-matic applies the same rules inside its worker loop, suspending guidance and writing no controls on a held tick ([examples/land-o-matic/src/main.rs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/land-o-matic/src/main.rs#L258-L281)).

9. **Write related controls atomically.** Controls that must take effect on the same game tick go in one batch write. The Apollo Guidance Computer bridge turns each tick's RCS demodulation into one rotate plus one translate write, which the mod executes inside a single drain in the Frame phase:

   ```rust
   // examples/agc/src/sim/mod.rs L188-L195
   /// The one-batch rotate+translate write the RCS demodulator issues each tick: both commands
   /// execute atomically in the same game tick (SPEC §3.10). Lines are `<action> <args>`.
   pub fn write_batch(src: &dyn Source, lines: &[String]) -> Result<(), CmdError> {
       if lines.is_empty() {
           return Ok(());
       }
       src.write("vessels/active/ctl/batch", &lines.join("\n"))
   }
   ```

   ```rust
   // examples/agc/src/rcs.rs L145-L152
   /// Renders the batch lines for a signs write (KSA body = LM body under the identity body_map:
   /// +x thrust/nose, +y right, +z down ≡ LM forward).
   pub fn batch_lines(s: Signs) -> Vec<String> {
       vec![
           format!("rotate {} {} {}", s.rot[0], s.rot[1], s.rot[2]),
           format!("translate {} {} {}", s.tra[0], s.tra[1], s.tra[2]),
       ]
   }
   ```

   A batch is single-phase: do not mix a flight-computer setpoint (Solver phase) with manual controls (Frame phase) in one batch; send the flight-computer fields as their own write. `land-o-matic` writes `attitude_target`, `throttle` and `ignite` as separate file writes; `attitude_target` is a Solver-phase action and `throttle` and `ignite` are Frame-phase, so they could not share one batch anyway.

10. **Surface errno names to the operator.** Every write returns `EINVAL`, `ENOENT`, `EACCES`, `EBUSY`, `EIO`, `ETIMEDOUT` or `EOPNOTSUPP`; show them (land-o-matic prints `attitude: <errno>: <message>` in its status line) and treat `ETIMEDOUT` and `EBUSY` as retryable only for state controls, never for one-shot triggers such as ignite or stage.

11. **Keep the control law host-testable.** land-o-matic's guidance core is pure Rust with no `/sim` dependency, so it is unit-tested and run in closed loop against a point-mass simulation on the host, then pointed at a fixture `/sim` tree or the live HTTP mirror (`--root ./fixture`, `--url $GATOS_HTTP`).

12. **For cinematics, compile ahead of time.** `photog-rs` compiles a shot list into a camera track plus a `timed_batch` cue sidecar and lets the mod play it on a shared clock, instead of streaming per-frame writes from the client; see [Schedule timed command sequences](../schedule-timed-commands/overview.md).

## Complete example

| File | Role |
|---|---|
| [examples/sdk-ts/src/transport.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/src/transport.ts) | Dual transport implementation |
| [examples/sdk-ts/src/client.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/src/client.ts) | Typed client surface |
| [examples/sdk-ts/src/errors.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/src/errors.ts) | `GatosError` and the status-to-errno table |
| [examples/agc/src/clockpolicy.rs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/agc/src/clockpolicy.rs) | Pause, warp and stale gate with resync |
| [examples/land-o-matic/src/main.rs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/land-o-matic/src/main.rs) | Worker loop with holds and control writes |
| [examples/land-o-matic/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/land-o-matic/README.md) | Landing guidance design, run in the guest |
| [examples/agc/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/agc/README.md) | The AGC bridge design |

## Variations and gotchas

- **Never fly blind.** Gate actions on telemetry (controllable, RCS enabled) before writing.
- **Named modes and scheduled burns survive warp**; only per-tick computed control must hold at 1x.
- **Attitude and burn writes take effect at the next solver step**, not instantly; wait for read-back.
- **Example programs pin their own toolchains** (Cargo, Bun) and are not part of the .NET solution.
- **Other languages.** Generate a client from the OpenAPI document or write a thin shell client over the file tree (`examples/sample-shellscripts`); the Transport seam is the part worth copying.
- **Do not accumulate dt from the wall clock.** At 10 Hz sampling a wall-clock dt is wrong whenever the sim is warped or paused.

## See also

- [Overview](overview.md)
- [External API clients and SDK](../../../../mods/gatos/features/external-api-clients.md), [HTTP /v1 API server](../../../../mods/gatos/features/http-v1-api-server.md)
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md)
- [Marshal commands from background threads to the game thread](../marshal-commands-to-the-game-thread/overview.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
