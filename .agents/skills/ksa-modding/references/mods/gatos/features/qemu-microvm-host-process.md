---
type: Mod Feature
title: QEMU microVM as a game child process
description: Boot, supervise and shut down a QEMU Linux VM from inside a KSA mod with an async state machine, per-OS accelerator ladders with TCG fallback, an SSH readiness probe and a QGA-QMP-kill shutdown ladder, all off the main thread
mod: gatos
category: external-io
techniques: [background-thread, process-hosting, async-state-machine]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload, thread:background VmHost.EnsureStartedAsync]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [qemu, vm, process, kvm, whpx, hvf, tcg, async, lifecycle]
---

gatOS runs a real Alpine Linux guest next to the game. `gatOS.Vm` is a game-free library that
locates `qemu-system-x86_64`, builds the command line, starts it as a child process, waits for the
guest's SSH server, reports status for the ImGui window and stops it cleanly at exit. Nothing in it
touches KSA; the mod creates it at load time and boots it lazily the first time a terminal session
needs it, so a player who never opens a gatOS tab pays nothing.

## How it works

**Locate QEMU** ([gatOS.Vm/QemuLocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuLocator.cs)): Windows uses a trimmed portable QEMU bundled in
the mod folder (`qemu/win-x64/`, fetched by `tools/fetch-qemu.ps1`); Linux and macOS require a
system `qemu-system-x86_64` on `PATH` (macOS also probes the Homebrew prefix, which is often missing
from a game process's `PATH`).

**Build the command line** ([gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs#L31-L95)). Arguments are a list
fed to `ProcessStartInfo.ArgumentList`, never a joined string:

- Accelerator ladder per OS as repeated `-accel` flags: Windows `whpx,tcg`, Linux `kvm,tcg`,
  macOS `hvf,tcg`; non-x64 hosts go straight to `tcg`. Foreign accelerator names are never passed
  because QEMU errors on names it was not compiled with.
- CPU model per accelerator: `host` under KVM/HVF, `max` under TCG, and a named model (`Haswell`)
  under WHPX, because WHPX triple-faults on `host`/`max` on real hardware (the repo records this on
  a Raptor Lake i9-13900K).
- Direct kernel boot (`-kernel`, `-initrd`, `-append` with the manifest's cmdline plus
  `gatos.simport=`, `gatos.httpport=`, `gatos.mqttport=`, `gatos.mntport=`), a virtio qcow2 overlay,
  slirp user networking with one loopback `hostfwd` for SSH, a virtio-serial bus carrying the QEMU
  guest agent socket (and optionally the `gatos.serial` port), QMP on a loopback TCP socket,
  `-display none`, serial console to a log file, and `-no-reboot` so a guest poweroff ends the process.

**Start and verify** ([gatOS.Vm/QemuProcess.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuProcess.cs#L85-L125)): launch, then wait a 3 s survival
window. An accelerator-init failure exits almost immediately; if the stderr tail looks
accelerator-related (`AccelFailureClassifier`), relaunch once with `-accel tcg`. The effective
accelerator is published for the status window.

**Boot as a shared task** ([gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs#L73-L97)): `EnsureStartedAsync` takes a
`SemaphoreSlim`, reuses an in-flight or healthy boot task, and otherwise starts `BootAsync`. Callers
can cancel their wait without cancelling the shared boot. `BootAsync`
([gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs#L161-L292)) gets the overlay and its pinned guest artifacts, takes the disk
lock, grows the overlay, asks the host for the current 9p/HTTP/MQTT/mount ports through provider
callbacks, allocates SSH/QGA/QMP (and serial) ports, retries once on a port clash, then races an SSH
readiness probe (it waits for bytes starting with `SSH-`) against process death, with a 60 s
(accelerated) or 300 s (TCG) timeout.

**Stop** ([gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs#L325-L345)): QGA `guest-shutdown` and wait for exit, else QMP
`quit`, else kill.

## Lifecycle and timing

| Moment | Hook | Work |
|---|---|---|
| Load | `[StarMapAllModsLoaded]` `OnFullyLoaded` | build `DiskManager`, `VmHost` (no boot), `VmConnectionBroker`, subscribe `StatusChanged` ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L282-L311)) |
| First session or menu Start VM | purrTTY calls the shell, or a menu click | `EnsureStartedAsync` on thread-pool tasks (`thread:background`) |
| Every frame | ImGui status window | reads the volatile `VmHost.Status` only; never awaits |
| Exit | `[StarMapUnload]` | `broker.DisposeAsync().AsTask().Wait(15 s)`; game exit is never held longer ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L484-L512)) |

StarMap invokes `StarMapUnload` methods from `ModLoader.Dispose`, which runs when the StarMap host
disposes after the game's entry point returns ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L51-L61),
[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L150)). No game state is involved.
`StarMapCore.DeInit` calls `Harmony.UnpatchAll()` without an id before `ModLoader.Dispose`
([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)), so every Harmony patch in the process,
gatOS's included, is already gone when `Unload` runs; its own `Remove*Hook` calls there are no-ops.

## Key code

| Path | Role |
|---|---|
| [gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs) | async state machine, shared boot, readiness race, shutdown ladder |
| [gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs) | argv, accelerator ladder, CPU model choice |
| [gatOS.Vm/QemuProcess.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuProcess.cs) | spawn, log files, stderr ring, survival window, TCG retry |
| [gatOS.Vm/QemuLocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuLocator.cs) | bundled vs system QEMU |
| [gatOS.Vm/AccelFailureClassifier.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/AccelFailureClassifier.cs) | accelerator-failure heuristic |
| [gatOS.Vm/ReadinessProbe.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/ReadinessProbe.cs) | SSH banner probe |
| [gatOS.Vm/QgaClient.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QgaClient.cs) | QEMU guest agent client for graceful shutdown |
| [gatOS.Vm/PortAllocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/PortAllocator.cs) | ephemeral loopback ports |

## Snippets

```csharp
// gatOS.Vm/QemuCommandBuilder.cs L47-L95
    public IReadOnlyList<string> Build(VmLaunchSpec spec)
    {
        var ladder = ResolveAccelLadder(spec);
        var args = new List<string>();

        foreach (var accel in ladder)
            args.AddRange(["-accel", accel]);

        args.AddRange(["-M", "q35"]);
        args.AddRange(["-cpu", ResolveCpuModel(spec, ladder[0])]);
        args.AddRange(["-m", spec.MemoryMb.ToString()]);
        args.AddRange(["-smp", spec.Cpus.ToString()]);

        args.AddRange(["-kernel", spec.KernelPath]);
        args.AddRange(["-initrd", spec.InitrdPath]);
        args.AddRange(["-append",
            $"{spec.KernelCmdlineBase} gatos.simport={spec.SimPort ?? 0} gatos.httpport={spec.HttpPort ?? 0} "
            + $"gatos.mqttport={spec.MqttPort ?? 0} gatos.mntport={spec.MntPort ?? 0}"]);

        args.AddRange(["-drive", $"file={spec.OverlayPath},if=virtio,format=qcow2"]);

        var restrict = spec.RestrictNetwork ? ",restrict=on" : "";
        args.AddRange(["-netdev", $"user,id=n0,hostfwd=tcp:127.0.0.1:{spec.SshHostPort}-:22{restrict}"]);
        args.AddRange(["-device", "virtio-net-pci,netdev=n0"]);

        args.AddRange(["-device", "virtio-serial-pci"]);
        args.AddRange(["-chardev", $"socket,id=qga0,host=127.0.0.1,port={spec.QgaPort},server=on,wait=off"]);
        args.AddRange(["-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0"]);

        // G7: the gatos.serial bus port. QEMU listens (server=on,wait=off) on a loopback socket
        // the host SerialBridge connects to; the guest sees /dev/virtio-ports/gatos.serial (the
        // init script symlinks every virtio-port by name). Shares the virtio-serial-pci bus above.
        if (spec.SerialPort is { } serialPort)
        {
            args.AddRange(["-chardev",
                $"socket,id=gatosserial,host=127.0.0.1,port={serialPort},server=on,wait=off"]);
            args.AddRange(["-device", "virtserialport,chardev=gatosserial,name=gatos.serial"]);
        }

        args.AddRange(["-qmp", $"tcp:127.0.0.1:{spec.QmpPort},server=on,wait=off"]);

        args.AddRange(["-display", "none"]);
        args.AddRange(["-serial", $"file:{spec.SerialLogPath}"]);
        args.AddRange(["-monitor", "none"]);
        // Guest poweroff/reboot exits the process — the clean lifecycle signal for VmHost.
        args.Add("-no-reboot");

        return args;
    }
```

## Game APIs touched

None. The library is game-free by design and unit-tested on a bare host; integration tests boot a
real VM when `GATOS_IT=1` (see [Build, deploy and CI](build-deploy-and-ci.md)).

## Pitfalls and version notes

- QEMU's own multi-`-accel` fallback does not catch every failure (missing Windows Hypervisor
  Platform, `/dev/kvm` permissions, `-cpu host` rejected after an in-QEMU fallback), hence the
  stderr classifier and one explicit TCG retry.
- Never block the frame on boot: menu actions start background tasks and the UI reads cached
  status. Boot takes about 1 s accelerated and about 7 s under TCG per the README.
- The unload log message says QEMU "will die with the game process" if the stop budget runs out.
  No Windows job object or Linux parent-death signal is set in `QemuProcess` (searched), so whether
  QEMU outlives a hard game crash is unverified.
- The port-reuse race between `PortAllocator` and QEMU binding is real but small; one retry with
  fresh ports covers it.

## See also

- [Host-guest transport over slirp TCP](slirp-tcp-host-guest-transport.md)
- [qcow2 overlay guest disks](qcow2-overlay-guest-disks.md)
- [Alpine guest image pipeline](alpine-guest-image.md)
- [SSH shell sessions into the guest](ssh-shell-session-into-guest.md)
- [Serial bus with CCSDS, NMEA and SCPI](serial-bus-ccsds-nmea-scpi.md)
- [gatOS overview](../overview.md)
- [QEMU microVM with async boot state machine, accelerator ladder, qcow2 overlays and a shutdown ladder](../../../howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-1.md) - how-to article demonstrating this feature

