# Game Feature

* [Host and supervise an external process or virtual machine from a KSA mod](overview.md) - Start a child process from the game without blocking the frame or leaking it, with output pumps, a queued writer, readiness probing and bounded shutdown at unload, shown for a QEMU microVM, a Windows pseudoconsole shell, a Unix pty shell and background shell detection

# How-To

* [POSIX pty via posix_openpt and posix_spawnp with a poll reader and a waitpid thread](host-and-supervise-an-external-process-from-a-mod-3.md) - Start a shell on a Unix pseudo-terminal from a large multithreaded game process without calling fork, with a poll-based reader, a queued writer, a waitpid thread and a SIGHUP then SIGKILL stop
* [QEMU microVM with async boot state machine, accelerator ladder, qcow2 overlays and a shutdown ladder](host-and-supervise-an-external-process-from-a-mod-1.md) - Run a Linux guest next to the game as a QEMU child process, started lazily off the main thread, with per-OS accelerator selection, copy-on-write disks over an immutable versioned base and a bounded shutdown at unload
* [ShellCommandResolver, WslDistributionDetector and UnixShellDetector feeding a menu cache](host-and-supervise-an-external-process-from-a-mod-4.md) - Detect installed shells and WSL distributions once on a background thread with bounded waits, cache an immutable menu snapshot, and build launch command lines with correct quoting
* [Windows ConPTY with a never-sleeping ReadFile pump, queued writer and close-first teardown](host-and-supervise-an-external-process-from-a-mod-2.md) - Start a real Windows shell under a pseudoconsole created by the game process, with a blocking ReadFile pump, a queued input writer and a teardown that closes the pseudoconsole first and leaks handles still in use
