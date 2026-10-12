---
type: How-To
title: QEMU microVM with async boot state machine, accelerator ladder, qcow2 overlays and a shutdown ladder
description: Run a Linux guest next to the game as a QEMU child process, started lazily off the main thread, with per-OS accelerator selection, copy-on-write disks over an immutable versioned base and a bounded shutdown at unload
category: external-io
group: processes
topic: host-and-supervise-an-external-process-from-a-mod
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/gatos/features/qemu-microvm-host-process
  - mods/gatos/features/qcow2-overlay-guest-disks
  - mods/gatos/features/alpine-guest-image
techniques: [process-hosting, background-thread, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: [KSA.Program]
tags: [qemu, vm, qcow2, accelerator, whpx, hvf, kvm, tcg, shutdown, readiness-probe]
---

Outcome: the mod runs a real Linux guest next to the game. A game-free library locates `qemu-system-x86_64`, builds the command line, starts it as a child process, waits for the guest's SSH banner, reports status for the ImGui window and stops it cleanly at exit. The mod creates the host at load time and boots it lazily the first time something needs it, so a player who never opens the feature pays nothing. Requires StarMap and QEMU (bundled on Windows, installed on Linux and macOS). Demonstrated by [QEMU microVM host process](../../../../mods/gatos/features/qemu-microvm-host-process.md), [qcow2 overlay guest disks](../../../../mods/gatos/features/qcow2-overlay-guest-disks.md) and [Alpine guest image pipeline](../../../../mods/gatos/features/alpine-guest-image.md) in gatOS. Everything except the status read happens off the main thread.

## Background: how the game does it

The game offers nothing for child processes. The facts that matter are in the [overview](./overview.md): unload runs from `ModLoader.Dispose` after the game's entry point returns, so a normal exit runs `[StarMapUnload]` while `Environment.Exit` paths skip it, and StarMap has already removed every Harmony patch by then.

## Lifecycle and timing

| Moment | Hook | Work |
|---|---|---|
| Load | `[StarMapAllModsLoaded]` `OnFullyLoaded` | Build `DiskManager`, `VmHost` (no boot), the connection broker; subscribe to status changes ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L282-L311)) |
| First use (a terminal session or a menu click) | pool task | `EnsureStartedAsync` (`thread:background`) |
| Every frame | ImGui status window | Reads the volatile `VmHost.Status` only; never awaits |
| Exit | `[StarMapUnload]` | `broker.DisposeAsync().AsTask().Wait(15 s)`; the game exit is never held longer ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L484-L512)) |

If the 15 s stop budget runs out the mod logs that QEMU will die with the game process. The repository sets no Windows job object or Linux parent-death signal in `QemuProcess` (searched), so whether QEMU outlives a hard crash of the game is unverified. See [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md).

## Steps

1. **Locate the emulator per OS.** Windows resolves only from a trimmed portable QEMU bundled in the mod folder (`qemu/win-x64/`); Linux and macOS require `qemu-system-x86_64` and `qemu-img` on `PATH` (macOS also probes the Homebrew prefix, which a game process's `PATH` often lacks). A missing binary raises a typed error with a per-OS install hint that the UI shows ([gatOS.Vm/QemuLocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuLocator.cs#L35-L68)).

2. **Build the command line as a list.** One argv entry per element, fed to `ProcessStartInfo.ArgumentList`, never string-joined:

   ```csharp
   // gatOS.Vm/QemuCommandBuilder.cs L47-L72
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
   ```

   The rest of the argument list sets direct kernel boot (`-kernel`, `-initrd`, `-append` with the manifest's command line plus the host ports the guest should connect to), a virtio qcow2 overlay, slirp user networking with one loopback `hostfwd` for SSH, a virtio-serial bus (guest agent socket and optionally a bus port), QMP on a loopback TCP socket, `-display none`, the serial console to a log file and `-no-reboot` so a guest poweroff ends the process ([gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs#L73-L95)).

3. **Choose accelerators and CPU model per host** (see the next section).

4. **Start with a survival window and one TCG retry.** Launch, then wait 3 s. An accelerator-init failure exits almost immediately; if the stderr tail looks accelerator-related, relaunch once with `-accel tcg`. Publish the effective accelerator for the UI:

   ```csharp
   // gatOS.Vm/QemuProcess.cs L91-L126
           var attempt = spec;
           for (var retried = false; ; retried = true)
           {
               ct.ThrowIfCancellationRequested();
               var ladder = _builder.ResolveAccelLadder(attempt);
               LaunchOnce(binaries, attempt, ladder[0]);

               // Survival window: an accel-init failure exits almost immediately; surviving it
               // means the launch took. The SSH readiness probe owns the rest of the boot wait.
               var exitTask = _exitTcs!.Task;
               if (await Task.WhenAny(exitTask, Task.Delay(SurvivalWindow, ct)) != exitTask)
               {
                   EffectiveAccel = ladder[0];
                   ModLog.Log.Info($"QEMU running (pid {_process!.Id}, accel {EffectiveAccel}, log {QemuLogPath})");
                   return;
               }

               await DrainPumpsAsync();
               var exitCode = exitTask.Result;
               var tail = StderrTail;

               if (!retried && ladder[0] != "tcg" && AccelFailureClassifier.IsAccelInitFailure(tail))
               {
                   ModLog.Log.Warn(
                       $"QEMU exited immediately (code {exitCode}) with an accelerator-looking error; retrying with TCG. "
                       + $"stderr: {tail}");
                   _logWriter.WriteLine("=== gatOS: accel-init failure, retrying with -accel tcg ===");
                   ResetForRetry();
                   attempt = attempt with { AccelOverride = "tcg" };
                   continue;
               }

               throw new VmStartException(
                   $"The VM failed to start (QEMU exited with code {exitCode}). See log: {QemuLogPath}",
                   detail: tail);
           }
   ```

5. **Pump stdout and stderr to a log and a bounded ring.** Redirect both streams, read lines on tasks into a rotating log file and a 100-line stderr ring, keep only five logs, and drain the pumps (bounded to 1 s) before inspecting the ring ([gatOS.Vm/QemuProcess.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuProcess.cs#L201-L262)).

6. **Make boot a shared task.** `EnsureStartedAsync` takes a `SemaphoreSlim`, reuses an in-flight or healthy boot task and otherwise starts one. A caller's cancellation abandons only its wait, never the shared boot:

   ```csharp
   // gatOS.Vm/VmHost.cs L73-L97
       public async Task<VmEndpoints> EnsureStartedAsync(CancellationToken ct)
       {
           Task<VmEndpoints> boot;
           await _gate.WaitAsync(ct);
           try
           {
               if (_bootTask is { } existing
                   && (!existing.IsCompleted
                       || (existing.IsCompletedSuccessfully && _process is { IsRunning: true })))
               {
                   boot = existing;
               }
               else
               {
                   boot = _bootTask = BootAsync();
               }
           }
           finally
           {
               _gate.Release();
           }

           // The boot itself is shared and never cancelled by one caller; ct only abandons the wait.
           return await boot.WaitAsync(ct);
       }
   ```

7. **Boot in a fixed order:** get or create the overlay and the pinned guest artifacts, take the disk lock, grow the overlay, ask the host for the transport ports through provider callbacks, allocate the SSH, guest-agent and QMP ports, start QEMU (one retry on a port clash), then race a readiness probe against process death with a 60 s (accelerated) or 300 s (TCG) timeout ([gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs#L161-L263)).

8. **Probe readiness by the SSH banner, not by a TCP connect.** slirp accepts on the host side as soon as QEMU starts, so readiness means actually receiving bytes that start with `SSH-`:

   ```csharp
   // gatOS.Vm/ReadinessProbe.cs L23-L38
       public static async Task WaitForSshAsync(int port, TimeSpan timeout, CancellationToken ct)
       {
           var deadline = DateTime.UtcNow + timeout;
           while (true)
           {
               ct.ThrowIfCancellationRequested();
               var remaining = deadline - DateTime.UtcNow;
               if (remaining <= TimeSpan.Zero)
                   throw new TimeoutException($"No SSH banner on 127.0.0.1:{port} within {timeout.TotalSeconds:0} s.");

               if (await TryReadBannerAsync(port, Min(remaining, PerAttemptReadTimeout), ct))
                   return;

               await Task.Delay(AttemptDelay, ct);
           }
       }
   ```

9. **Stop with a ladder.** Ask the guest agent to shut down and wait; else send QMP `quit`; else kill the process tree. Stop is idempotent, waits out an in-flight boot first, and always releases the disk lock:

   ```csharp
   // gatOS.Vm/VmHost.cs L325-L348
       private async Task RunShutdownLadderAsync(IQemuProcess process, int qgaPort, TimeSpan grace)
       {
           process.Exited -= OnProcessExited; // expected exit — not a fault

           string rung;
           await using var qga = _qgaFactory(qgaPort);
           if (await qga.ShutdownAsync() && await process.WaitForExitAsync(grace))
           {
               rung = "QGA guest-shutdown";
           }
           else if (await process.TryQuitViaQmpAsync(QmpQuitTimeout))
           {
               rung = "QMP quit";
           }
           else
           {
               process.Kill();
               await process.WaitForExitAsync(TimeSpan.FromSeconds(5));
               rung = "kill";
           }

           ModLog.Log.Info($"VM stopped via {rung}");
           await process.DisposeAsync();
       }
   ```

## The accelerator ladder and CPU model

QEMU errors on accelerator names it was not compiled with, so only host-appropriate names are ever passed. They are emitted as repeated `-accel` flags and the first available wins:

| Host | Ladder |
|---|---|
| Windows x64 | `whpx`, `tcg` |
| Linux x64 | `kvm`, `tcg` |
| macOS x64 | `hvf`, `tcg` |
| non-x64 host | `tcg` only (hardware acceleration is same-architecture only) |

   ```csharp
   // gatOS.Vm/QemuCommandBuilder.cs L31-L45
       public IReadOnlyList<string> ResolveAccelLadder(VmLaunchSpec spec)
       {
           if (spec.AccelOverride.Length > 0)
               return [spec.AccelOverride];
           if (!facts.IsX64)
               return ["tcg"];
           if (facts.IsWindows)
               return ["whpx", "tcg"];
           if (facts.IsLinux)
               return ["kvm", "tcg"];
           if (facts.IsMacOs)
               return ["hvf", "tcg"];
           return ["tcg"];
       }

   ```

The guest CPU model depends on the accelerator: `host` under KVM and HVF, `max` under TCG, and a named model (`Haswell`) under WHPX, because WHPX triple-faults at boot on `host` and `max` on real hardware (the repository records this on a Raptor Lake i9-13900K); a configured `cpu_model` overrides all of it ([gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs#L97-L118)). QEMU's own fallback does not catch every failure (missing Windows Hypervisor Platform, `/dev/kvm` permissions, `-cpu host` rejected after an in-QEMU fallback), which is why the process wrapper classifies the stderr tail and retries explicitly with TCG ([gatOS.Vm/AccelFailureClassifier.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/AccelFailureClassifier.cs)).

## Disks: copy-on-write overlays over an immutable versioned base

A mod that ships an operating system must survive updates without wiping what the player installed in the guest and must never write into its install folder. gatOS splits the disk: an immutable compressed base image per guest version, copied once into the data folder, and a small overlay per profile that holds every change.

1. **Two roots.** The data folder `MyDocuments/My Games/Kitten Space Agency/mods/gatOS/` (`disks/`, `logs/`, config) is user-writable; the install folder holds the shipped guest artifacts and QEMU and is treated as read-only ([gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs#L21-L62)). On Windows the mod folder and the data folder coincide, so the shipped config is a differently named template that never overwrites the live file.
2. **Install once per version, never overwrite.** Base image, kernel, initramfs, manifest and SSH key are copied with a temp-then-rename so a crash never leaves a plausible-looking artifact; the host-key pin is read back from the installed manifest, not the shipped one ([gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs#L60-L86), [gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs#L296-L308)).
3. **Create the overlay with the disks folder as working directory,** so the stored backing reference is a bare relative name and the folder stays movable. `qemu-img commit` is never used because it would write into the shared base:

   ```csharp
   // gatOS.Vm/DiskManager.cs L100-L110
           // Run with cwd = DisksDir and bare filenames so the stored backing ref is relative
           // (portable: the disks folder survives being moved — OS_ANALYSIS.md §3.7).
           RunQemuImg(GatOsPaths.DisksDir,
               "create", "-f", "qcow2",
               "-b", BaseImageName(installed.Manifest.GuestVersion), "-F", "qcow2",
               Path.GetFileName(overlayPath));

           File.WriteAllText(ProfileTomlPath(profile),
               $"guest_version = {installed.Manifest.GuestVersion}\n");
           ModLog.Log.Info($"Created overlay '{overlayPath}' on base v{installed.Manifest.GuestVersion}");
           return overlayPath;
   ```

4. **Pin boots to the guest version the overlay was created on.** Booting a newer kernel over an older root filesystem leaves the kernel without its module tree: loading the 9p module fails and `/sim` never mounts while SSH keeps working. If the old version's artifacts are gone, the overlay is moved aside as `<profile>.orphaned-v<N>.qcow2` and a fresh one is created ([gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs#L123-L158)).
5. **Grow only.** `EnsureOverlaySize` runs `qemu-img resize` only to grow; the guest's init runs `resize2fs` at boot ([gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs#L209-L230)).
6. **One VM per overlay.** A PID lock file is created with `FileMode.CreateNew`; a lock whose recorded owner is dead is reclaimed ([gatOS.Vm/DiskLock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskLock.cs#L25-L50)).

## The pinned guest image

`guest/build-image.sh` assembles the root filesystem with a static `apk` (pinned `apk-tools-static` and `alpine-keys` verified by SHA-256), bakes in the supervisors and keys, packs a partitionless ext4 image as a zstd-compressed qcow2 with a separate kernel and initramfs for direct kernel boot, smoke-tests the result in QEMU and writes `manifest.toml`, the host-side boot contract (guest version, kernel command line, SSH user and key, host-key pin) ([guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh#L263-L264)). There is no OpenRC: busybox init runs one sysinit script and respawns four supervisors:

```
// guest/rootfs-overlay/etc/inittab L5-L11
::sysinit:/sbin/init-gatos
::respawn:/usr/sbin/sshd -D -e
::respawn:/sbin/qga-gatos
::respawn:/sbin/sim-mount
::respawn:/sbin/mnt-mount
::ctrlaltdel:/sbin/reboot
::shutdown:/sbin/shutdown-gatos
```

CI publishes each guest version as a GitHub release; `guest/fetch-guest.sh` downloads it into `guest/out/`, verifies `sha256sums.txt` and does nothing when the manifest already matches, and the mod build copies `guest/out/**` into the mod folder ([guest/fetch-guest.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/fetch-guest.sh#L34-L37)). The SSH host key and session key are committed under `guest/keys/` so the host-key pin never drifts across rebuilds; access is loopback-only ([guest/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/README.md#L17-L30)). The pin is consumed in [the SSH article of the transports topic](../host-guest-transports-tcp-9p-and-ssh/overview.md).

## Complete example

| File | Role |
|---|---|
| [gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs) | Async state machine, shared boot, readiness race, shutdown ladder |
| [gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs) | Argv, accelerator ladder, CPU model |
| [gatOS.Vm/QemuProcess.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuProcess.cs) | Spawn, log files, stderr ring, survival window, TCG retry |
| [gatOS.Vm/QemuLocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuLocator.cs) | Bundled vs system QEMU |
| [gatOS.Vm/ReadinessProbe.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/ReadinessProbe.cs) | SSH banner probe |
| [gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs) | Base install, overlay create, resize, version pinning |
| [gatOS.Vm/DiskLock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskLock.cs) | PID lock with stale reclaim |
| [gatOS.Vm/GuestManifest.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GuestManifest.cs) | The host-guest boot contract |

## Variations and gotchas

- **Never block the frame on boot.** Menu actions start background tasks; the UI reads cached status. The README gives about 1 s accelerated and about 7 s under TCG.
- **The port-reuse race** between the allocator and QEMU binding is real but small; one retry with fresh ports covers it ([ports are carried on the kernel command line](../host-guest-transports-tcp-9p-and-ssh/overview.md)).
- **Never pair a guest kernel with a different root filesystem version** when the guest loads modules from the root.
- **Reset Disk** deletes the overlay (after a confirmation) and thereby upgrades the profile to the current guest version on next boot.
- **Hard crashes.** An unload that is skipped (restart paths) or times out leaves cleanup to the OS; the overlay is crash-consistent qcow2.

## See also

- [Overview](overview.md)
- [Approach 2: Windows ConPTY](host-and-supervise-an-external-process-from-a-mod-2.md), [Approach 3: POSIX pty](host-and-supervise-an-external-process-from-a-mod-3.md), [Approach 4: shell detection](host-and-supervise-an-external-process-from-a-mod-4.md)
- [QEMU microVM host process](../../../../mods/gatos/features/qemu-microvm-host-process.md), [qcow2 overlay guest disks](../../../../mods/gatos/features/qcow2-overlay-guest-disks.md), [Alpine guest image pipeline](../../../../mods/gatos/features/alpine-guest-image.md)
- [Connect a VM guest to the game with slirp TCP, a 9P filesystem and pinned SSH](../host-guest-transports-tcp-9p-and-ssh/overview.md)
- [Bridge a serial port and bus protocols to the game](../../network/bridge-a-serial-port-and-bus-protocols-to-the-game/overview.md)
- [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
