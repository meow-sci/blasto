---
type: Mod Feature
title: Host-guest transport over slirp TCP
description: Every channel between the game process and the guest is plain TCP over QEMU user-mode networking, with host ports advertised on the kernel command line, because virtio-9p, virtiofs and vsock have no host backend on Windows QEMU
mod: gatos
category: external-io
techniques: [process-hosting, background-thread]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: []
tags: [qemu, slirp, tcp, 9p, ports, kernel-cmdline, cross-platform]
---

The question every "embed a VM in a game" mod has to answer is how data crosses the VM boundary.
gatOS's answer is deliberately boring: TCP over QEMU's user-mode (slirp) network for everything.
The guest mounts `/sim` from a 9P2000.L server running inside the game process, reaches HTTP and
MQTT at the slirp gateway address, and the host reaches the guest's SSH server through one
loopback port forward. The same code path works on Windows, Linux and macOS.

## How it works

**Why not virtio-9p, virtiofs or vsock.** The repo's analysis records that QEMU's virtio-9p host
backend is compile-gated to Linux/macOS/FreeBSD, virtiofs relies on vhost-user (Linux-only
mechanism), and vhost-vsock needs a Linux host kernel module; and even where virtio-9p exists it
exports a host directory, not a synthetic in-process tree. The Linux kernel's `trans=tcp` 9p client
is the one stock path to an out-of-QEMU 9p server ([OS_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/OS_ANALYSIS.md#L119-L141)). virtio
devices themselves (net, blk, serial) are fine on Windows and are used.

**Channels.**

| Channel | Direction | Mechanism |
|---|---|---|
| SSH (terminal sessions) | host to guest | `-netdev user,hostfwd=tcp:127.0.0.1:<p>-:22` |
| `/sim` 9p | guest to host | guest dials `10.0.2.2:<simport>` (slirp gateway is the host loopback) |
| `/mnt` 9p (host folders) | guest to host | `10.0.2.2:<mntport>` |
| HTTP, MQTT | guest to host | `$GATOS_HTTP` = `http://sim:<port>/v1`, `$GATOS_MQTT` = `sim:<port>` |
| QGA, QMP, `gatos.serial` | host to QEMU | loopback TCP chardev sockets (`server=on,wait=off`) |

**Port discovery through the kernel command line.** The host binds its servers first (9p on an
ephemeral loopback port at mod load, HTTP/MQTT on preferred ports with ephemeral fallback), and
`VmHost` passes them to the guest as `gatos.simport=`, `gatos.httpport=`, `gatos.mqttport=`,
`gatos.mntport=` on `-append` (0 means disabled) ([gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs#L60-L70)).
Guest scripts parse `/proc/cmdline`:

- `sim-mount` (respawned by busybox init) loops forever, remounting `/sim` with
  `mount -t 9p -o trans=tcp,port=$PORT,version=9p2000.L,msize=524288,cache=none 10.0.2.2 /sim`
  whenever it is not a mountpoint, so a host-side server restart heals itself
  ([guest/rootfs-overlay/sbin/sim-mount](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/sim-mount#L11-L24)).
- `init-gatos` writes `/etc/profile.d/gatos.sh` exporting `GATOS_HTTP` and `GATOS_MQTT`, and
  `/etc/hosts` aliases `10.0.2.2` as `sim` ([guest/rootfs-overlay/sbin/init-gatos](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/init-gatos#L58-L80),
  [guest/rootfs-overlay/etc/hosts](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/etc/hosts)).

**Port allocation.** `PortAllocator.AllocatePorts(n)` binds n `TcpListener`s on port 0 at once (so
the ports are distinct), reads them, and closes them before QEMU binds; the residual race is covered
by one retry in `VmHost` ([gatOS.Vm/PortAllocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/PortAllocator.cs#L26-L47)).

## Lifecycle and timing

- `[StarMapAllModsLoaded]`: the host servers start before any VM exists, so a VM booted later
  always finds valid ports through the `*PortProvider` callbacks evaluated at boot time
  ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L282-L306)).
- All sockets are served on thread-pool threads; the game thread is never involved in transport.
- `cache=none` on the mount is essential: every read must reach the server so values stay live.

## Key code

| Path | Role |
|---|---|
| [gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs#L60-L86) | cmdline ports, hostfwd, virtio-serial chardevs, QMP |
| [gatOS.Vm/PortAllocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/PortAllocator.cs) | distinct ephemeral loopback ports |
| [guest/rootfs-overlay/sbin/sim-mount](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/sim-mount) | guest 9p remount supervisor |
| [guest/rootfs-overlay/sbin/mnt-mount](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/mnt-mount) | guest supervisor for host folder mounts |
| [guest/rootfs-overlay/sbin/init-gatos](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/init-gatos) | network setup and transport environment variables |
| [OS_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/OS_ANALYSIS.md) | the transport decision record |

## Snippets

```sh
# guest/rootfs-overlay/sbin/sim-mount L11-L24
PORT="$(sed -n 's/.*gatos\.simport=\([0-9][0-9]*\).*/\1/p' /proc/cmdline)"
if [ -z "$PORT" ] || [ "$PORT" = 0 ]; then
    # 9p disabled: idle forever (a fast exit would make init throttle the respawn).
    exec sleep 2147483647
fi

modprobe 9p 2>/dev/null
mkdir -p /sim
while :; do
    if ! mountpoint -q /sim; then
        mount -t 9p -o "trans=tcp,port=$PORT,version=9p2000.L,msize=524288,cache=none" 10.0.2.2 /sim 2>/dev/null
    fi
    sleep 2
done
```

## Game APIs touched

None.

## Pitfalls and version notes

- Bind host services to `127.0.0.1`; the slirp gateway still reaches them, and nothing is exposed
  to the LAN. HTTP, MQTT and MCP accept a configurable bind host; the guest-only 9p listeners stay
  on loopback.
- Do not raise `msize` above what the server negotiates; gatOS's server ceiling is 512 KiB (guest
  v15 raised the client request to match).
- slirp is TCP-only in this design (no UDP needed).
- A guest supervisor should idle with a long `sleep` rather than exit when disabled, so busybox
  init does not throttle respawns.

## See also

- [9P2000.L server in C#](ninep-server-in-csharp.md)
- [QEMU microVM as a game child process](qemu-microvm-host-process.md)
- [HTTP /v1 API server](http-v1-api-server.md)
- [Embedded MQTT broker](embedded-mqtt-broker.md)
- [Alpine guest image pipeline](alpine-guest-image.md)
- [gatOS overview](../overview.md)
- [slirp hostfwd and gateway TCP with a PortAllocator and guest-side supervisors](../../../howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-1.md) - how-to article demonstrating this feature

