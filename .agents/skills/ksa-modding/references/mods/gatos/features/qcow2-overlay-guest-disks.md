---
type: Mod Feature
title: qcow2 overlay guest disks
description: Keep a pristine shipped base image read-only and store all guest changes in a per-profile qcow2 overlay in the user data folder, with version-pinned boots, grow-only resize, a PID lock file and a one-click reset
mod: gatos
category: persistence
techniques: [process-hosting, background-thread]
hooks: [thread:background VmHost.BootAsync]
lifecycle: [thread:background]
game_types: []
tags: [qemu, qcow2, disks, persistence, data-folder, versioning]
---

A mod that ships a whole operating system needs to survive mod updates without wiping what the
player installed in the guest, and must never modify files in its install folder. gatOS splits the
disk in two: an immutable compressed base image per guest version, copied once into the data
folder, and a small copy-on-write overlay per profile that holds every change. `DiskManager` drives
`qemu-img` to create, grow, pin and reset these files.

## How it works

**Paths.** All user-writable data lives under
`MyDocuments/My Games/Kitten Space Agency/mods/gatOS/` (`disks/`, `logs/`, `gatos.toml`), centralized
in `GatOsPaths`; the install folder (`GatOsPaths.ModDir`) holds the shipped guest artifacts and QEMU
([gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs)).

**Install once per version.** `EnsureBaseInstalled` copies `base.qcow2`, kernel, initramfs,
`manifest.toml` and the SSH key into `disks/base-v<N>.qcow2` and `disks/guest-v<N>/` only if missing,
never overwriting. The SSH host-key pin is read back from the installed manifest, not the shipped one,
so a re-keyed rebuild of the same version cannot break an existing installation.

**Overlay.** `CreateOverlay` runs `qemu-img create -f qcow2 -b base-v<N>.qcow2 -F qcow2 <profile>.qcow2`
with the disks folder as working directory, so the stored backing reference is a bare relative
filename and the folder stays movable; a sidecar `<profile>.toml` records `guest_version`
([gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs#L92-L111)). `qemu-img commit` is never used because it would write into
the shared base.

**Version-pinned boot.** `GetOrCreateBoot` boots an overlay with the kernel and initramfs of the
guest version it was created on. Mixing a newer kernel with an older root filesystem leaves the
kernel without its module tree, `modprobe 9p` fails and `/sim` never mounts while SSH keeps
working. If the old version's artifacts are gone, the overlay is moved aside as
`<profile>.orphaned-v<N>.qcow2` and a fresh one is created ([gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs#L123-L156)).

**Grow-only resize.** `EnsureOverlaySize` compares `qemu-img info`'s virtual size with
`disk_size_gb` and runs `qemu-img resize` only to grow; the guest's init runs `resize2fs` on boot
([gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs#L209-L230)).

**Lock.** `DiskLock.Acquire` creates `<profile>.lock` with `FileMode.CreateNew` and writes the
process id; if it exists and that process is alive the boot fails, otherwise the stale lock is
reclaimed ([gatOS.Vm/DiskLock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskLock.cs#L25-L51)).

**Reset.** The menu's "Reset Disk..." deletes the overlay (after a confirmation modal), which also
upgrades the profile to the current guest version on next boot.

## Lifecycle and timing

All disk work runs inside `VmHost.BootAsync` on a thread-pool task (`thread:background`) while
holding the disk lock, before QEMU opens the overlay. Nothing touches game state, and the main
thread only ever reads the published VM status. The current GameMod uses a single profile named
`default`; per-save profiles are planned (M10) and not built.

## Key code

| Path | Role |
|---|---|
| [gatOS.Vm/DiskManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskManager.cs) | base install, overlay create/resize/delete, version pinning |
| [gatOS.Vm/DiskLock.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/DiskLock.cs) | PID lock file with stale reclaim |
| [gatOS.Vm/GuestManifest.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GuestManifest.cs) | the host-guest boot contract (`manifest.toml`) |
| [gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs) | data and install folder layout |
| [gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs#L161-L183) | boot-time order: overlay, lock, grow |

## Snippets

```csharp
// gatOS.Vm/DiskManager.cs L123-L146
    public GuestBoot GetOrCreateBoot(string profile)
    {
        ValidateProfileName(profile);
        var current = EnsureBaseInstalled(); // installs the bundled version's artifacts if needed
        var overlayPath = OverlayPath(profile);
        if (!File.Exists(overlayPath))
            return new GuestBoot(CreateOverlay(profile), current);

        // Pair the overlay with the guest version it was created on. Booting the newest kernel
        // over an older rootfs leaves the kernel without its /lib/modules tree — modprobe 9p fails
        // in the guest and /sim never mounts, while SSH (initramfs virtio) keeps working, which is
        // exactly how the mismatch presents in-game.
        var overlayVersion = ReadProfileGuestVersion(profile);
        if (overlayVersion is null || overlayVersion == current.Manifest.GuestVersion)
            return new GuestBoot(overlayPath, current);

        if (TryLoadInstalledVersion(overlayVersion.Value) is { } pinned)
        {
            ModLog.Log.Warn($"Profile '{profile}' was created on guest v{overlayVersion} — booting that "
                            + $"version so the guest stays consistent (v{current.Manifest.GuestVersion} is "
                            + "installed). Use 'Reset Disk...' to upgrade; it wipes data inside the guest.");
            return new GuestBoot(overlayPath, pinned);
        }

```

## Game APIs touched

None. KSA itself is not involved; the only coupling is the conventional KSA mods data folder.

## Pitfalls and version notes

- A qcow2 overlay's virtual size is independent of the base; grow the overlay, never the base.
- Store relative backing paths, or moving the data folder breaks every overlay.
- Never pair a guest kernel with a root filesystem of a different version when the guest loads
  modules (9p here) from the root filesystem.
- On Windows the install folder and data folder are the same directory (gatOS notes), so keep shipped
  files and user files distinguishable by name.

## See also

- [QEMU microVM as a game child process](qemu-microvm-host-process.md)
- [Alpine guest image pipeline](alpine-guest-image.md)
- [TOML config seeded from a shipped template](toml-config-seeded-template.md)
- [ModMenu entry with a fallback menu](modmenu-entry-with-fallback-menu.md)
- [gatOS overview](../overview.md)
- [QEMU microVM with async boot state machine, accelerator ladder, qcow2 overlays and a shutdown ladder](../../../howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-1.md) - how-to article demonstrating this feature
- [Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-3.md) - how-to article demonstrating this feature

