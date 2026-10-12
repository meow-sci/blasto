---
type: Mod Feature
title: External API clients and SDK
description: A typed TypeScript SDK that runs over either the /sim filesystem or HTTP, plus Rust, TypeScript and shell example programs that fly, film and decorate KSA from inside or outside the guest, demonstrating the external API surface
mod: gatos
category: external-io
techniques: [external-client, command-queue]
hooks: []
lifecycle: [thread:background]
game_types: []
tags: [sdk, typescript, rust, examples, clients, autopilot, openapi]
---

Because gatOS publishes its game interface as files and HTTP, mods' consumers do not have to be
C#. The repo ships one small SDK and a collection of example programs. They are not KSA code, but
they show what the API surface makes possible and how to structure a client: pick a transport,
read an atomic telemetry document, pace by sim time, and write commands with real error results.

## How it works

**TypeScript SDK** (`examples/sdk-ts`): one `Transport` interface with two implementations
([examples/sdk-ts/src/transport.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/src/transport.ts#L10-L25)):

- `FsTransport` reads and writes `/sim` paths (zero dependencies), the default inside the guest when
  `$GATOS_HTTP` is unset.
- `HttpTransport` uses `GET /v1/...`, SSE and `POST /v1/command` at `$GATOS_HTTP`.

Reads are uniform because the per-vessel `telemetry` document is byte-identical on both transports;
writes are uniform because both carry the same command shape. Errors map back to errno names.
Examples: `orbit-watch.ts` (live orbit view), `launch-monitor.ts` (events with flameout abort),
`solar-keeper.ts` (sensor-to-actuator loop). The HTTP surface also has an OpenAPI document for
generating clients in other languages ([sim_openapi.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/sim_openapi.yml)).

**Example programs grouped by technique** (each under `examples/<name>/`):

| Technique | Examples |
|---|---|
| Plain shell over `/sim` | `sample-shellscripts/hi_hunter.sh` (jq over `parts/json`, then a weld), `welds/`, `thug-life/` |
| Rust TUIs over `/sim` files | `dashboard-rs` (dashboard and flight console), `simfs-dashboard` (DIY dashboard builder), `gogogo-rs`, `pressme-rs`, `kecho` (tee to many `/sim` paths at once) |
| Guidance and autopilots in the guest | `land-o-matic` (G-FOLD powered-descent planning plus UPFG steering, with explicit CCI/ENU/CCF frame contracts), `skycaptain` (skywriting autopilot) |
| Flight software emulation | `agc` (Apollo Guidance Computer Luminary099/Comanche055 on yaAGC inside the guest, converting telemetry into IMU, PIPA and radar inputs and AGC jet/engine outputs back into `/sim` writes, with a ratatui DSKY) |
| Cinematics | `photog-rs` (shot editor compiling to `/sim/camera/track` plus a `timed_batch` cue sidecar), `shotbuilder`, `starwars` |
| Effects, audio, display | `fx`, `nyan-fire-rs` (plume colours), `dancy-party-rs` (lights), `the90s` (soundboard over `/sim/audio`), `simscreen` (renders `/sim/display`) |
| Cheats | `nudge` (shift a vessel's CCI position via teleport) |

## Lifecycle and timing

Client programs run in the guest or on the host, entirely outside the game process; from KSA's
point of view they are transport traffic (`thread:background` on the mod side). The SDK and
tutorials stress pacing by sim time (`/sim/time/alarm`, `GET /v1/time/wait`), never by guest wall
clock, because warp makes the two unrelated ([docs/TUTORIAL_DATA_REFERENCE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/TUTORIAL_DATA_REFERENCE.md#L271-L300)).

## Key code

| Path | Role |
|---|---|
| [examples/sdk-ts/src/transport.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/src/transport.ts) | dual transport implementation |
| [examples/sdk-ts/src/client.ts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/src/client.ts) | typed client surface |
| [examples/sdk-ts/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sdk-ts/README.md) | SDK usage inside the guest |
| [examples/agc/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/agc/README.md) | the AGC bridge design |
| [examples/land-o-matic/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/land-o-matic/README.md) | landing guidance over `/sim` |
| [examples/sample-shellscripts/hi_hunter.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/examples/sample-shellscripts/hi_hunter.sh) | shell plus jq client |
| [EXAMPLE_COMMANDS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/EXAMPLE_COMMANDS.md) | ready-to-paste shell commands |

## Snippets

```ts
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

## Game APIs touched

None directly; all game access goes through gatOS's transports.

## Pitfalls and version notes

- Always gate actions on telemetry (is the vessel controllable, is RCS enabled) before writing; the
  tutorials call this "never fly blind".
- Attitude and burn writes take effect at the next solver step, not instantly; scripts must wait for
  read-back.
- Example programs pin their own toolchains (Cargo, Bun); they are not part of the .NET solution.

## See also

- [HTTP /v1 API server](http-v1-api-server.md)
- [The /sim file models](sim-filesystem-file-models.md)
- [MCP server for AI agents](mcp-server-for-agents.md)
- [Timed command scheduler](timed-command-scheduler.md)
- [Programmable camera director](programmable-camera-director.md)
- [gatOS overview](../overview.md)
- [Typed SDK with a Transport interface, plus example autopilot programs](../../../howto/external-io/control/build-external-clients-and-autopilots-with-a-typed-sdk/build-external-clients-and-autopilots-with-a-typed-sdk.md) - how-to article demonstrating this feature

