# Game Feature

* [Connect a VM guest to the game with slirp TCP, a 9P filesystem and pinned SSH](overview.md) - The channels between a QEMU guest and the game process that work on Windows, Linux and macOS - slirp TCP with pre-allocated host ports, a from-scratch 9P2000.L server with synthetic and confined host files, and SSH sessions pinned to the guest manifest's host key

# How-To

* [From-scratch 9P2000.L server with a VfsNode tree and HostDirectory confinement](host-guest-transports-tcp-9p-and-ssh-2.md) - Serve a synthetic filesystem and confined host folders to a Linux guest by implementing the 9P2000.L protocol over TCP, with one session per connection, pipelined parked reads and flush handling
* [Pin the guest SSH host key to a manifest value](host-guest-transports-tcp-9p-and-ssh-4.md) - Open SSH.NET sessions into the guest only when the presented host key's SHA-256 equals the fingerprint in the installed guest manifest, with lazy boot, a single retry and a bounded input writer thread
* [Sensor, StreamFile, EventsFile and CommandFile models under Linux netfslib rules](host-guest-transports-tcp-9p-and-ssh-3.md) - Choose the right read model for each synthetic file - snapshot-memoized sensors, growing logs, blocking event files, control files that return errno - so cat, watch and tail -f behave on a 9p mount in a modern Linux guest
* [slirp hostfwd and gateway TCP with a PortAllocator and guest-side supervisors](host-guest-transports-tcp-9p-and-ssh-1.md) - Carry every host-guest channel over QEMU user-mode networking, with ports bound before the VM exists, handed to the guest on the kernel command line and remounted by guest-side supervisors
