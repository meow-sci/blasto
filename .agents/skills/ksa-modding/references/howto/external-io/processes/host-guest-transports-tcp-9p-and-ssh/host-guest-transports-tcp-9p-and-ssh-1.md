---
type: How-To
title: slirp hostfwd and gateway TCP with a PortAllocator and guest-side supervisors
description: Carry every host-guest channel over QEMU user-mode networking, with ports bound before the VM exists, handed to the guest on the kernel command line and remounted by guest-side supervisors
category: external-io
group: processes
topic: host-guest-transports-tcp-9p-and-ssh
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/slirp-tcp-host-guest-transport]
techniques: [process-hosting, background-thread]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: []
tags: [slirp, qemu, tcp, hostfwd, kernel-cmdline, port-allocation, 9p]
---

Outcome: the guest mounts `/sim` from a 9P server in the game process, reaches HTTP and MQTT at the slirp gateway, and the host reaches the guest's SSH server through one loopback port forward, with the same code path on Windows, Linux and macOS. Demonstrated by [Host-guest transport over slirp TCP](../../../../mods/gatos/features/slirp-tcp-host-guest-transport.md) in gatOS.

## Background: how the game does it

KSA is not involved. The decision record in the repository explains why the usual alternatives are not used. QEMU's virtio-9p host backend is compile-gated to Linux, macOS and FreeBSD, virtiofs relies on vhost-user (a Linux-only mechanism), and vhost-vsock needs a Linux host kernel module; even where virtio-9p exists, QEMU's server exports a host directory, not a synthetic in-process tree ([OS_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/OS_ANALYSIS.md#L119-L141)). The Linux kernel's `trans=tcp` 9p client is the one stock path to an out-of-QEMU 9p server. virtio *devices* (net, block, serial) are fine on Windows hosts and are used. A `guestfwd` rule was considered for a fixed in-guest address and rejected for HTTP because its chardev forwarding is effectively single-stream ([KSA_GAME_INTEGRATION_PLAN.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/KSA_GAME_INTEGRATION_PLAN.md#L474-L481)), so the guest simply dials the slirp gateway.

## Lifecycle and timing

- At `[StarMapAllModsLoaded]` the host servers start first, so a VM booted later always finds valid ports through provider callbacks evaluated at boot time ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L282-L306)).
- All sockets are served on thread-pool threads (`thread:background`); the game thread is never involved in transport.
- The guest mounts with `cache=none`, so every read reaches the server and values stay live.

## Steps

1. **Choose the channels and their direction.**

| Channel | Direction | Mechanism |
|---|---|---|
| SSH (terminal sessions) | host to guest | `-netdev user,hostfwd=tcp:127.0.0.1:<p>-:22` |
| `/sim` 9p | guest to host | guest dials `10.0.2.2:<simport>` (the slirp gateway is the host loopback) |
| `/mnt` 9p (host folders) | guest to host | `10.0.2.2:<mntport>` |
| HTTP, MQTT | guest to host | `$GATOS_HTTP` = `http://sim:<port>/v1`, `$GATOS_MQTT` = `sim:<port>` |
| QGA, QMP, `gatos.serial` | host to QEMU | loopback TCP chardev sockets (`server=on,wait=off`) |

2. **Bind the host servers before the VM exists.** The 9p server takes an ephemeral loopback port at mod load; HTTP and MQTT take preferred ports with an ephemeral fallback. They listen on loopback only: slirp delivers guest connections to `10.0.2.2` as host connections to `127.0.0.1`, so loopback suffices and the Windows firewall never prompts ([gatOS.NineP/Server/NinePServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Server/NinePServer.cs#L51-L61)).

3. **Allocate the QEMU-side ports all at once.** Bind n listeners on port 0 at the same time (so the ports are distinct), read the ports, then close them before QEMU binds. The residual race is covered by one retry in the VM host:

   ```csharp
   // gatOS.Vm/PortAllocator.cs L26-L47
       public static IReadOnlyList<int> AllocatePorts(int count)
       {
           ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

           var listeners = new List<TcpListener>(count);
           try
           {
               for (var i = 0; i < count; i++)
               {
                   var listener = new TcpListener(IPAddress.Loopback, 0);
                   listener.Start();
                   listeners.Add(listener);
               }

               return listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
           }
           finally
           {
               foreach (var listener in listeners)
                   listener.Stop();
           }
       }
   ```

4. **Hand the host ports to the guest on the kernel command line.** `-append` carries `gatos.simport=`, `gatos.httpport=`, `gatos.mqttport=` and `gatos.mntport=` (0 means disabled), and the SSH forward is a single `hostfwd` entry:

   ```csharp
   // gatOS.Vm/QemuCommandBuilder.cs L58-L70
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
   ```

5. **Write a guest supervisor that remounts forever.** The guest runs `sim-mount` under busybox init's `respawn`; it parses `/proc/cmdline`, idles with a long sleep when the port is 0 (a fast exit would make init throttle the respawn), and remounts `/sim` whenever it is not a mountpoint, so a host-side server restart heals itself. There is no kernel-side reconnect: a dropped connection gives `EIO` until remount.

   ```
   // guest/rootfs-overlay/sbin/sim-mount L11-L24
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

6. **Publish the other ports as environment.** The guest init writes `/etc/profile.d/gatos.sh` exporting `GATOS_HTTP` and `GATOS_MQTT`, and `/etc/hosts` aliases `10.0.2.2` as `sim`. The authoritative lines are in the guest init:

   ```
   // guest/rootfs-overlay/sbin/init-gatos L58-L74

   # Magic HTTP transport (G5): the host advertises its server port as gatos.httpport=<port> on
   # the kernel cmdline (0/absent = disabled). The guest reaches the host server outbound at
   # 10.0.2.2 (slirp); /etc/hosts aliases that to 'sim'. Login shells pick up $GATOS_HTTP via
   # /etc/profile.d, and /run/gatos/http-port carries the bare port for non-shell consumers.
   HTTP_PORT="$(sed -n 's/.*gatos\.httpport=\([0-9][0-9]*\).*/\1/p' /proc/cmdline)"
   mkdir -p /run/gatos /etc/profile.d
   MQTT_PORT="$(sed -n 's/.*gatos\.mqttport=\([0-9][0-9]*\).*/\1/p' /proc/cmdline)"
   : > /etc/profile.d/gatos.sh
   if [ -n "$HTTP_PORT" ] && [ "$HTTP_PORT" != "0" ]; then
       echo "$HTTP_PORT" > /run/gatos/http-port
       printf 'export GATOS_HTTP="http://sim:%s/v1"\n' "$HTTP_PORT" >> /etc/profile.d/gatos.sh
   else
       rm -f /run/gatos/http-port 2>/dev/null
   fi
   if [ -n "$MQTT_PORT" ] && [ "$MQTT_PORT" != "0" ]; then
       echo "$MQTT_PORT" > /run/gatos/mqtt-port
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.Vm/QemuCommandBuilder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/QemuCommandBuilder.cs) | Cmdline ports, hostfwd, virtio-serial chardevs, QMP |
| [gatOS.Vm/PortAllocator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/PortAllocator.cs) | Distinct ephemeral loopback ports |
| [guest/rootfs-overlay/sbin/sim-mount](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/sim-mount) | Guest 9p remount supervisor |
| [guest/rootfs-overlay/sbin/mnt-mount](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/mnt-mount) | Guest supervisor for host folder mounts |
| [guest/rootfs-overlay/sbin/init-gatos](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/init-gatos) | Network setup and transport environment variables |

## Variations and gotchas

- **Bind host services to `127.0.0.1`.** The slirp gateway still reaches them and nothing is exposed to the LAN. HTTP, MQTT and MCP accept a configurable bind host; the guest-only 9p listeners stay on loopback.
- **`msize` ceiling.** The server negotiates down from 512 KiB; the guest mount requests `msize=524288`.
- **TCP only.** No UDP is needed in this design.
- **A disabled supervisor sleeps** instead of exiting so busybox init does not throttle respawns.
- **`restrict_network`** adds `restrict=on` to the user netdev; the repository's plan marks whether the guest can still dial the gateway under it as something to spike before promising ([OS_PLAN.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/OS_PLAN.md#L1782-L1782)).

## See also

- [Overview](overview.md)
- [Approach 2: 9P2000.L server](host-guest-transports-tcp-9p-and-ssh-2.md)
- [Host-guest transport over slirp TCP](../../../../mods/gatos/features/slirp-tcp-host-guest-transport.md)
- [Host and supervise an external process or virtual machine from a KSA mod](../host-and-supervise-an-external-process-from-a-mod/overview.md)
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md)
