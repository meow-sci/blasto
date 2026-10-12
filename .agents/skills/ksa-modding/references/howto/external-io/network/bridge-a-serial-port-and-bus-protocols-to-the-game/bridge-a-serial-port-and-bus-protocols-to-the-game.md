---
type: How-To
title: SerialBridge with a connector per boot, protocol encoders and a SCPI command port
description: Bridge a QEMU virtio-serial chardev to the game by streaming NDJSON, NMEA or CCSDS telemetry and answering SCPI-style command lines with OK or ERR plus an errno name
category: external-io
group: network
topic: bridge-a-serial-port-and-bus-protocols-to-the-game
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/serial-bus-ccsds-nmea-scpi]
techniques: [protocol-server, background-thread, command-queue]
hooks: [starmap StarMapUnload]
lifecycle: [thread:background, event:unload]
game_types: [KSA.Program]
tags: [serial, virtio-serial, ndjson, nmea, ccsds, scpi, qemu, telemetry]
---

Outcome: a program inside a guest VM, or any host-side tool that can open a socket, reads game telemetry as NDJSON lines, NMEA sentences or CCSDS space packets from a serial device, and sends instrument-style command lines that are answered `OK` or `ERR <errno>`. The encoders are small pure functions over the published snapshot, which makes them reusable for any mod that wants to speak a real aerospace protocol. Prerequisites: StarMap lifecycle hooks, an immutable snapshot store ([Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md)), a game-thread command queue ([Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)) and a QEMU guest hosted as a child process ([Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md)). Demonstrated by [Serial bus: CCSDS, NMEA and SCPI](../../../../mods/gatos/features/serial-bus-ccsds-nmea-scpi.md) in gatOS.

## Background: how the game does it

The game has no serial or bus concept. The bridge only needs two things from KSA, and both arrive through the snapshot: the vessels, and which one the player controls. The sampler reads `Program.ControlledVehicle?.Id` on the game thread and stores it as `ActiveVesselId`; the bridge resolves the active vessel from the latest snapshot on every frame and every command line, so switching vessels silently redirects both directions. The decompiled `Program.ControlledVehicle` is a static property in [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L516).

The serial device itself is a QEMU `virtio-serial` port. QEMU listens on a loopback socket (`server=on,wait=off`) so it never blocks its own boot waiting for the game, and the game connects as a client and reconnects when the connection drops.

## Lifecycle and timing

| Work | Where |
|---|---|
| Add the chardev to the QEMU command line | the VM launch spec, built when the VM starts |
| Create the bridge and connector | `VmHost.StatusChanged`, fired on a VM thread when the VM reaches `Running`; torn down on every other state and first at `[StarMapUnload]` |
| Telemetry pump, SCPI reader, connect/retry loop | thread-pool tasks (`thread:background`) |
| Snapshot reads | `SnapshotStore.Current`, any thread |
| Command execution | game thread through the shared queue; `SubmitAsync` completes after the drain |

Nothing here runs in a frame hook. The sampler that feeds the snapshot is covered by [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md); frame structure is in the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the serial port on the QEMU command line.** The chardev shares the existing `virtio-serial-pci` bus. QEMU is the socket server and does not wait for a client, so the guest boots whether or not the game is connected:

   ```csharp
   // gatOS.Vm/QemuCommandBuilder.cs L76-L84
           // G7: the gatos.serial bus port. QEMU listens (server=on,wait=off) on a loopback socket
           // the host SerialBridge connects to; the guest sees /dev/virtio-ports/gatos.serial (the
           // init script symlinks every virtio-port by name). Shares the virtio-serial-pci bus above.
           if (spec.SerialPort is { } serialPort)
           {
               args.AddRange(["-chardev",
                   $"socket,id=gatosserial,host=127.0.0.1,port={serialPort},server=on,wait=off"]);
               args.AddRange(["-device", "virtserialport,chardev=gatosserial,name=gatos.serial"]);
           }
   ```

   The guest init symlinks every virtio port by name, so the program in the guest opens `/dev/virtio-ports/gatos.serial`.

2. **Start the bridge when the VM reaches `Running`.** `VmStatus` carries the allocated `SerialPort`. The handler creates a `SerialBridge` with the configured wire mode, cadence and which directions are enabled, wraps it in a connector and starts it; any other state tears the connector down. A lock makes it idempotent for one run:

   ```csharp
   // gatOS.GameMod/Mod.cs L818-L842
       private void OnVmStatusChanged(object? sender, VmStatus status)
       {
           if (status is { State: VmState.Running, SerialPort: { } port }
               && SerialEnabled() && _simStore is { } store)
           {
               lock (_serialLock)
               {
                   if (_serialConnector is not null)
                       return; // already connected for this run
                   var bridge = new SerialBridge(store, ParseSerialMode(_config.SerialMode),
                       TimeSpan.FromMilliseconds(_config.SerialIntervalMs),
                       emitTelemetry: _config.SerialTelemetryPort,
                       commands: _config.SerialCommandPort ? _commandQueue : null);
                   var connector = new SerialBridgeConnector(port, bridge);
                   _serialConnector = connector;
                   connector.Start();
                   ModLog.Log.Info($"gatOS serial bridge connecting to gatos.serial on 127.0.0.1:{port} "
                                   + "(guest: /dev/virtio-ports/gatos.serial).");
               }
           }
           else
           {
               StopSerialConnector();
           }
       }
   ```

3. **Dial the chardev and reconnect forever.** The connector is a client loop. A connect or run failure is logged at debug level and retried every second until disposed, so a guest that closes and reopens the port, or a VM restart, re-establishes the feed without intervention:

   ```csharp
   // gatOS.Bus/SerialBridgeConnector.cs L39-L70
       private async Task RunAsync(CancellationToken ct)
       {
           while (!ct.IsCancellationRequested)
           {
               try
               {
                   using var client = new TcpClient();
                   await client.ConnectAsync(IPAddress.Loopback, _port, ct).ConfigureAwait(false);
                   client.NoDelay = true;
                   await using var stream = client.GetStream();
                   ModLog.Log.Debug($"serial bridge: connected to gatos.serial on 127.0.0.1:{_port}");
                   await _bridge.RunAsync(stream, ct).ConfigureAwait(false);
               }
               catch (OperationCanceledException)
               {
                   return;
               }
               catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
               {
                   ModLog.Log.Debug($"serial bridge: connect/run failed (will retry): {ex.Message}");
               }

               try
               {
                   await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
               }
               catch (OperationCanceledException)
               {
                   return;
               }
           }
       }
   ```

4. **Pump telemetry at a fixed cadence.** One loop reads the newest snapshot, picks the active vessel, builds a frame for the configured mode and writes it. A UART is not a firehose, so the default interval is 500 ms. All writes from both directions go through one `SemaphoreSlim` so a reply never interleaves with a telemetry frame:

   ```csharp
   // gatOS.Bus/SerialBridge.cs L75-L89, L134-L146
       private async Task PumpTelemetryAsync(Stream stream, CancellationToken ct)
       {
           while (!ct.IsCancellationRequested)
           {
               var snapshot = _store.Current;
               if (ActiveVessel(snapshot) is { } vessel)
               {
                   var frame = SerialTelemetry.Frame(_mode, snapshot, vessel, Interlocked.Increment(ref _sequence));
                   if (frame.Length > 0)
                       await WriteAsync(stream, frame, ct).ConfigureAwait(false);
               }

               await Task.Delay(_interval, ct).ConfigureAwait(false);
           }
       }
   // ...
       private async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken ct)
       {
           await _writeGate.WaitAsync(ct).ConfigureAwait(false);
           try
           {
               await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
               await stream.FlushAsync(ct).ConfigureAwait(false);
           }
           finally
           {
               _writeGate.Release();
           }
       }
   ```

5. **Encode NMEA sentences.** A sentence is `$` plus the talker, type and comma-joined fields, then `*` and the XOR of every byte between them as two hex digits. The talker is `KS`. `Parse` is the checksum-validating reference decoder:

   ```csharp
   // gatOS.Bus/Nmea.cs L21-L28
       public static string Sentence(string type, params string[] fields)
       {
           var body = fields.Length == 0 ? Talker + type : $"{Talker}{type},{string.Join(',', fields)}";
           var checksum = 0;
           foreach (var c in body)
               checksum ^= (byte)c;
           return $"${body}*{checksum:X2}\r\n";
       }
   ```

   `SerialTelemetry.NmeaFrame` emits one `STA` sentence (id, situation, UT, barometric altitude, orbital speed) and, when the vessel has an orbit, one `ORB` sentence (apoapsis and periapsis altitude, eccentricity, inclination):

   ```csharp
   // gatOS.Bus/SerialTelemetry.cs L37-L47
       private static string NmeaFrame(SimSnapshot snapshot, VesselSnapshot v)
       {
           // One time/state sentence + one orbit sentence (when in orbit). Talker "KS".
           var sb = new StringBuilder();
           sb.Append(Nmea.Sentence("STA", v.Id, v.Situation, Nmea.Field(snapshot.UtSeconds),
               Nmea.Field(v.AltitudeOrZero()), Nmea.Field(v.OrbitalSpeed)));
           if (v.Orbit is { } o)
               sb.Append(Nmea.Sentence("ORB", Nmea.Field(o.ApoapsisAltitude), Nmea.Field(o.PeriapsisAltitude),
                   Nmea.Field(o.Eccentricity), Nmea.Field(o.InclinationDeg)));
           return sb.ToString();
       }
   ```

6. **Encode CCSDS space packets.** A telemetry packet is a 6-byte big-endian primary header followed by the payload: version 0, type TM, an 11-bit APID, sequence flags 3 (unsegmented), a 14-bit wrapping sequence count and the payload length minus one. gatOS puts the NDJSON line in the payload under APID `Nav` (0x010):

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

   `Ccsds.DecodeHeader` is the matching decoder, and `SerialTelemetry.Frame` dispatches on `SerialMode` (`Ndjson`, `Nmea`, `Ccsds`). NDJSON and NMEA frames are line-delimited and CCSDS frames carry their own length, so a reader can frame the stream without a side channel.

7. **Read SCPI-style command lines.** The reader splits the byte stream on CR or LF, bounds an unterminated line at 1024 bytes, and for each line resolves the active vessel from the latest snapshot, then answers with a trailing LF (not CRLF, so a guest `read` does not keep a stray CR):

   ```csharp
   // gatOS.Bus/SerialBridge.cs L91-L119
       private async Task ServeCommandsAsync(Stream stream, ICommandSink sink, CancellationToken ct)
       {
           var buffer = new byte[4096];
           var line = new List<byte>(128);
           while (!ct.IsCancellationRequested)
           {
               var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
               if (read == 0)
                   return; // guest closed the port

               for (var i = 0; i < read; i++)
               {
                   var b = buffer[i];
                   if (b is (byte)'\n' or (byte)'\r')
                   {
                       if (line.Count > 0)
                       {
                           await HandleLineAsync(stream, sink, Encoding.UTF8.GetString(line.ToArray()), ct)
                               .ConfigureAwait(false);
                           line.Clear();
                       }
                   }
                   else if (line.Count < 1024) // bound a runaway unterminated line
                   {
                       line.Add(b);
                   }
               }
           }
       }
   ```

   With no active vessel the reply is `ERR ENOENT`.

8. **Parse the line into a `SimCommand` and submit it.** `ScpiCommandPort` is case-insensitive and accepts `CTL:IGNITE`, `CTL:SHUTDOWN`, `CTL:STAGE`, `CTL:THROTTLE <0..1>`, `CTL:LIGHTS <0|1>`, `CTL:RCS <0|1>`, `CTL:ENG<n>:ACT <0|1>`, `CTL:LIGHT<n>:ON <0|1>` and `CTL:DECOUP<n>:FIRE`. It returns `OK` or `ERR` plus the errno name for the outcome, through the same queue every other transport uses:

   ```csharp
   // gatOS.Bus/ScpiCommandPort.cs L23-L29, L105-L114
       public async Task<string> HandleAsync(string line, CancellationToken ct)
       {
           if (!TryParse(line, vesselId, out var command))
               return "ERR EINVAL";
           var result = await sink.SubmitAsync(command, ct).ConfigureAwait(false);
           return result.IsSuccess ? "OK" : $"ERR {Errno(result.Outcome)}";
       }
   // ...
       private static string Errno(CommandOutcome outcome) => outcome switch
       {
           CommandOutcome.Invalid => "EINVAL",
           CommandOutcome.NotFound => "ENOENT",
           CommandOutcome.Denied => "EACCES",
           CommandOutcome.Busy => "EBUSY",
           CommandOutcome.TimedOut => "ETIMEDOUT",
           CommandOutcome.Unsupported => "EOPNOTSUPP",
           _ => "EIO",
       };
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.Bus/SerialBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialBridge.cs) | Duplex pump: telemetry out, SCPI in, one write gate |
| [gatOS.Bus/SerialBridgeConnector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialBridgeConnector.cs) | Connect to the QEMU chardev socket and retry while the VM runs |
| [gatOS.Bus/SerialTelemetry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/SerialTelemetry.cs) | Frame builders for NDJSON, NMEA and CCSDS |
| [gatOS.Bus/Ccsds.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/Ccsds.cs) | Space packet encoder and header decoder |
| [gatOS.Bus/Nmea.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/Nmea.cs) | Sentence builder and checksum-validating parser |
| [gatOS.Bus/ScpiCommandPort.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Bus/ScpiCommandPort.cs) | SCPI-style grammar to `SimCommand` |
| [gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs) | The chardev and virtserialport arguments |

## Variations and gotchas

- **Let QEMU listen.** With `server=on,wait=off` QEMU starts without the game connected and the game reconnects after every VM restart. The reverse (QEMU as client) would fail the boot when the game is not ready.
- **Active vessel only.** Both directions target the vessel the player controls, resolved per frame or line. The serial transport is deliberately narrower than the file and HTTP transports.
- **Low-rate streams look stalled through `cat`.** A guest read returns when its buffer fills; use a line-oriented reader for NDJSON or NMEA.
- **Bound the input line.** A runaway unterminated line is cut at 1024 bytes rather than growing without limit.
- **Keep encoders game-free.** The codecs reference only snapshot types, so they run in unit tests without the game.

## See also

- [Overview](overview.md)
- [Serial bus: CCSDS, NMEA and SCPI](../../../../mods/gatos/features/serial-bus-ccsds-nmea-scpi.md)
- [Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md), [Connect a VM guest with slirp TCP, a 9P filesystem and pinned SSH](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md)
- [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md), [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)
