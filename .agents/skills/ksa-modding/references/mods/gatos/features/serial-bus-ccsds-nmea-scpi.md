---
type: Mod Feature
title: Serial bus with CCSDS, NMEA and SCPI
description: A virtual serial port into the guest, backed by a QEMU virtio-serial chardev socket, streams active-vessel telemetry as NDJSON, NMEA-0183 sentences or CCSDS space packets and accepts SCPI-style command lines that answer OK or ERR with an errno
mod: gatos
category: external-io
techniques: [background-thread, protocol-server, command-queue, immutable-snapshot]
hooks: [thread:background SerialBridgeConnector]
lifecycle: [thread:background]
game_types: []
tags: [serial, virtio-serial, ccsds, nmea, scpi, telemetry, protocols]
---

For players who want mission-control cosplay (or to test real ground-segment code), gatOS can
expose `/dev/virtio-ports/gatos.serial` inside the guest. Telemetry frames stream out at a fixed
cadence in one of three wire formats, and instrument-style command lines stream in. Off by default
(`serial_telemetry_port`, `serial_command_port`, `serial_mode`, `serial_interval_ms`). The codecs
are tiny, game-free and unit-tested, which makes them a good template for any "speak a real
aerospace protocol" mod.

## How it works

**Wiring.** When a serial direction is enabled, `VmHost` allocates a fourth port and
`QemuCommandBuilder` adds `-chardev socket,id=gatosserial,host=127.0.0.1,port=<p>,server=on,wait=off`
and `-device virtserialport,chardev=gatosserial,name=gatos.serial` on the shared
`virtio-serial-pci` bus ([gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs#L76-L84)). The guest init symlinks the
port by name. When the VM reaches Running, the mod creates a `SerialBridge` and a
`SerialBridgeConnector` that dials the chardev socket as a client and retries every second while
the VM runs ([gatOS.Bus/SerialBridgeConnector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialBridgeConnector.cs), [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L818-L840)).

**Telemetry frames** ([gatOS.Bus/SerialTelemetry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialTelemetry.cs#L28-L55)), always for the active vessel:

| `serial_mode` | Frame |
|---|---|
| `ndjson` | the same NDJSON line as `/sim/vessels/<id>/stream` |
| `nmea` | `$KSSTA,<id>,<situation>,<ut>,<alt>,<speed>*CS` plus `$KSORB,<ap>,<pe>,<ecc>,<inc>*CS` with XOR checksums, talker `KS` ([gatOS.Bus/Nmea.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/Nmea.cs#L21-L28)) |
| `ccsds` | a CCSDS 133.0-B telemetry space packet: 6-byte big-endian primary header (version 0, type TM, 11-bit APID `Nav = 0x010`, sequence flags 3, 14-bit wrapping count, length minus one) with the NDJSON line as payload ([gatOS.Bus/Ccsds.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/Ccsds.cs#L41-L60)) |

NDJSON and NMEA are line-delimited and CCSDS is length-self-delimited, so a reader can frame the
stream without out-of-band signalling.

**Commands** ([gatOS.Bus/ScpiCommandPort.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/ScpiCommandPort.cs)): one case-insensitive line per command,
`CTL:IGNITE`, `CTL:SHUTDOWN`, `CTL:STAGE`, `CTL:THROTTLE <0..1>`, `CTL:LIGHTS <0|1>`, `CTL:RCS <0|1>`,
`CTL:ENG<n>:ACT <0|1>`, `CTL:LIGHT<n>:ON <0|1>`, `CTL:DECOUP<n>:FIRE`, parsed into a `SimCommand`,
submitted through the shared `ICommandSink`, and answered `OK` or `ERR <errno>`.

## Lifecycle and timing

- The bridge follows the VM lifecycle through `VmHost.StatusChanged`; it is stopped first at
  `[StarMapUnload]`.
- Pumps run on thread-pool tasks (`thread:background`): telemetry at `serial_interval_ms`
  (default 500 ms) reading `SnapshotStore.Current`, commands awaiting the game-thread drain. Writes
  from both directions share one gate on the stream.

## Key code

| Path | Role |
|---|---|
| [gatOS.Bus/SerialBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialBridge.cs) | duplex pump: telemetry out, SCPI in |
| [gatOS.Bus/SerialBridgeConnector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialBridgeConnector.cs) | connect to the QEMU chardev socket, retry while running |
| [gatOS.Bus/SerialTelemetry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialTelemetry.cs) | per-mode frame builders |
| [gatOS.Bus/Ccsds.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/Ccsds.cs) | CCSDS space packet encoder and header decoder |
| [gatOS.Bus/Nmea.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/Nmea.cs) | NMEA sentence builder and checksum-validating parser |
| [gatOS.Bus/ScpiCommandPort.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/ScpiCommandPort.cs) | SCPI-style grammar to `SimCommand` |

## Snippets

```csharp
// gatOS.Bus/Ccsds.cs L41-L60
    public static byte[] EncodeTm(int apid, int sequenceCount, ReadOnlySpan<byte> payload)
    {
        if (apid is < 0 or > MaxApid)
            throw new ArgumentOutOfRangeException(nameof(apid), apid, "APID is an 11-bit field");
        if (payload.Length == 0)
            throw new ArgumentException("CCSDS packets carry at least one data octet", nameof(payload));

        var packet = new byte[6 + payload.Length];

        // Word 1: version(0) | type(0=TM) | sec-hdr-flag(0) | APID(11).
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)(apid & MaxApid));
        // Word 2: sequence-flags(3=unsegmented) | packet-sequence-count(14).
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2),
            (ushort)(0xC000 | (sequenceCount & 0x3FFF)));
        // Word 3: packet-data-length = payload length − 1.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(payload.Length - 1));

        payload.CopyTo(packet.AsSpan(6));
        return packet;
    }
```

## Game APIs touched

None.

## Pitfalls and version notes

- virtio-serial chardevs with `server=on,wait=off` let QEMU start without the host connected; the
  host must reconnect after every VM restart.
- Low-rate streams through `cat` can appear stalled because the guest's read returns only when its
  buffer fills; use line-oriented readers.
- The serial transport is intentionally narrower than `/sim` (active vessel, a few actions); the
  repo documents it as a cosplay channel, not a full mirror.

## See also

- [Host-guest transport over slirp TCP](slirp-tcp-host-guest-transport.md)
- [QEMU microVM as a game child process](qemu-microvm-host-process.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [External API clients and SDK](external-api-clients.md)
- [gatOS overview](../overview.md)
- [SerialBridge with a connector per boot, protocol encoders and a SCPI command port](../../../howto/external-io/network/bridge-a-serial-port-and-bus-protocols-to-the-game/bridge-a-serial-port-and-bus-protocols-to-the-game.md) - how-to article demonstrating this feature

