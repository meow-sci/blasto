---
type: Mod Feature
title: Alpine guest image pipeline
description: A reproducible script builds a tiny Alpine Linux root filesystem with busybox init supervisors, static committed SSH keys and a manifest boot contract, smoke-boots it in QEMU, and publishes it as a pinned GitHub release that the mod build fetches
mod: gatos
category: testing-and-tooling
techniques: [process-hosting, ci-pipeline]
hooks: []
lifecycle: [thread:background]
game_types: []
tags: [alpine, linux, guest-image, build, ci, qemu, packaging]
---

gatOS ships an entire Linux userland inside a KSA mod. Rather than installing Alpine
interactively, `guest/build-image.sh` assembles the root filesystem directly with a static `apk`,
bakes in the gatOS supervisors and keys, packs it as a compressed qcow2 with a separate kernel and
initramfs for direct kernel boot, smoke-tests the result in QEMU, and writes a manifest the host
uses as its boot contract. CI publishes each guest version as a GitHub release that mod builds
download and verify.

## How it works

**Build steps** ([guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh)), run as root on Linux:

1. `fetch_bootstrap`: download the pinned `apk-tools-static` and `alpine-keys` packages and
   verify their SHA-256 (Alpine 3.24, kernel 6.18, past the 6.11 v9fs netfslib change).
2. `build_rootfs`: `apk --root $ROOTFS --initdb add $GUEST_PACKAGES` (busybox, OpenSSH,
   `qemu-guest-agent`, `linux-virt`, `e2fsprogs-extra`, coreutils and others).
3. `configure_rootfs`, `apply_branding`, `install_keys`: copy `rootfs-overlay/`, set the
   hostname, write the login banner and `/etc/os-release`, install the committed SSH host key and
   the session public key.
4. `build_initramfs`: regenerate the initramfs with a trimmed `mkinitfs.conf` (`base virtio ext4`).
5. `extract_boot_and_make_image`: `mkfs.ext4 -d $ROOTFS` into a partitionless raw image, then
   `qemu-img convert -O qcow2 -c -o compression_type=zstd` to `base.qcow2`.
6. `write_manifest_and_sums`: `manifest.toml` (guest version, kernel cmdline, SSH user, host key
   fingerprint) and `sha256sums.txt`.
7. `smoke_test`: boot the artifacts under KVM or TCG and wait for the SSH banner.

**Init.** No OpenRC: busybox init runs `init-gatos` once (mounts, cgroup2 with delegated
controllers so `apk add podman` works, static slirp network, `resize2fs /dev/vda`, transport
environment variables) and respawns `sshd -D -e`, `qga-gatos`, `sim-mount` and `mnt-mount`
([guest/rootfs-overlay/etc/inittab](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/etc/inittab)).

**Static keys, by design.** The SSH host key and the session key pair are committed under
`guest/keys/`, so the host-key pin never drifts across rebuilds; access is only ever via the
loopback port forward ([guest/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/README.md#L17-L30)).

**Distribution.** `.github/workflows/guest-image.yml` builds on `ubuntu-latest` and publishes
release `guest-v<N>` where N comes from `guest/GUEST_VERSION` (currently 17).
`guest/fetch-guest.sh` / `.ps1` download that release into `guest/out/`, verify the checksums and
no-op when the manifest already matches; the mod build copies `guest/out/**` into the mod folder.

## Lifecycle and timing

Build-time only. At runtime the host reads the installed `manifest.toml` in `DiskManager` and
`VmHost` on a background task to construct the kernel command line and SSH pin (see
[qcow2 overlay guest disks](qcow2-overlay-guest-disks.md)).

## Key code

| Path | Role |
|---|---|
| [guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh) | the image build and smoke test |
| [guest/rootfs-overlay/etc/inittab](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/etc/inittab) | busybox init: one sysinit, four respawned supervisors |
| [guest/rootfs-overlay/sbin/init-gatos](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/init-gatos) | boot-time setup in the guest |
| [guest/rootfs-overlay/sbin/qga-gatos](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/qga-gatos) | starts the QEMU guest agent once its port appears |
| [guest/fetch-guest.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/fetch-guest.sh) | pinned release download and checksum verification |
| [guest/GUEST_VERSION](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/GUEST_VERSION) | guest version pin |
| [.github/workflows/guest-image.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/guest-image.yml) | CI build and release |

## Snippets

```sh
# guest/rootfs-overlay/etc/inittab L1-L11
# gatOS guest inittab (busybox init — no openrc; see OS_PLAN.md T2.2).
# No getty: the serial console is a log file; access is SSH (or QGA for control).
# sshd: -D keep in foreground (busybox respawn owns the lifecycle), -e log to stderr
# (captured into the QEMU serial log). Config is the default /etc/ssh/sshd_config.
::sysinit:/sbin/init-gatos
::respawn:/usr/sbin/sshd -D -e
::respawn:/sbin/qga-gatos
::respawn:/sbin/sim-mount
::respawn:/sbin/mnt-mount
::ctrlaltdel:/sbin/reboot
::shutdown:/sbin/shutdown-gatos
```

## Game APIs touched

None.

## Pitfalls and version notes

- The netboot initramfs lacks ext4; direct kernel boot needs an initramfs generated for the disk
  image (repo `spike/NOTES.md`).
- Coreutils (added in guest v10) shadows busybox `tail`, which broke `tail -f` on `/sim` until the
  poll-mode shim landed in v14.
- A new guest version requires the version-pinned boot logic on the host; old overlays keep booting
  their own kernel.
- Bundled QEMU exists only for Windows; Linux uses the system QEMU.

## See also

- [QEMU microVM as a game child process](qemu-microvm-host-process.md)
- [qcow2 overlay guest disks](qcow2-overlay-guest-disks.md)
- [Host-guest transport over slirp TCP](slirp-tcp-host-guest-transport.md)
- [Build, deploy and CI](build-deploy-and-ci.md)
- [gatOS overview](../overview.md)
- [QEMU microVM with async boot state machine, accelerator ladder, qcow2 overlays and a shutdown ladder](../../../howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-1.md) - how-to article demonstrating this feature
- [Pin the guest SSH host key to a manifest value](../../../howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-4.md) - how-to article demonstrating this feature
- [Build a guest image with a smoke boot in CI and fetch it by pinned release with checksums](../../../howto/testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/run-repo-checks-from-a-pre-push-hook-and-ci-4.md) - how-to article demonstrating this feature

