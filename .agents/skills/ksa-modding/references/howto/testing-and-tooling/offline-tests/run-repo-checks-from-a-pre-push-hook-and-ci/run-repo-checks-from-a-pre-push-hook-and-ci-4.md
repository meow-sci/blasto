---
type: How-To
title: Build a guest image with a smoke boot in CI and fetch it by pinned release with checksums
description: Assemble a Linux guest image from pinned bootstrap packages, boot it in QEMU as the last build step, publish it as a versioned release and have consumers download and verify it instead of building
category: testing-and-tooling
group: offline-tests
topic: run-repo-checks-from-a-pre-push-hook-and-ci
approach: 4
difficulty: advanced
prerequisites: [bash, qemu, github-actions]
demonstrated_by: [mods/gatos/features/alpine-guest-image]
techniques: [ci-pipeline, smoke-test, pinned-artifacts]
hooks: []
lifecycle: []
game_types: []
tags: [testing, ci, guest-image, qemu, smoke-test, checksums, release]
---

Outcome: a CI job that builds a bootable guest image, proves it boots (an SSH banner answers on a forwarded port) before it publishes anything, uploads the artifacts and a checksum list as a versioned GitHub release, and a fetch script that mod builds and integration tests use to download exactly that version and verify it.
Demonstrated by gatOS, which ships an Alpine Linux userland inside a KSA mod ([guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh#L323-L362)). Prerequisites: a mod that bundles a large binary artefact built from pinned inputs.

## Background: how the game does it

The game does not take part; the artefact is a mod payload. What matters is that a mod's build must be able to obtain it without rebuilding it (dev builds, the distribution CI and integration tests all need it), and that a broken image is found before a player boots it inside a KSA session. See [Ship content and code in one mod](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md) for how the mod folder is assembled around such payloads.

## Lifecycle and timing

Build and CI time only. At run time the host reads the installed `manifest.toml` on a background task to construct the kernel command line and the SSH pin, which is why the manifest is part of the artefact contract.

## Steps

1. **Pin every bootstrap input by version and hash.** Alpine keeps only the newest revision per stable branch, so a 404 means a security bump: update both pins in one commit. The comment in the script says exactly that:

   ```bash
   # guest/build-image.sh L32-L39
   # Bootstrap artifacts are pinned exactly (version + sha256). Alpine keeps only the
   # newest revision per stable branch, so when these 404 the branch got a security
   # bump: look up the new version + sha256 at $ALPINE_MIRROR/v$ALPINE_VERSION/main/x86_64/
   # and update both pins in one commit.
   APK_TOOLS_STATIC_APK=apk-tools-static-3.0.6-r0.apk
   APK_TOOLS_STATIC_SHA256=a62f54609910d1eb23d8ebcf69dd7954280fe76047452bb88410122cbca14a6e
   ALPINE_KEYS_APK=alpine-keys-2.6-r0.apk
   ALPINE_KEYS_SHA256=dd211936d544f4050924ce8aec078d24e7b1b036ae70b30bd07867349587c708
   ```

2. **Assemble the root filesystem directly instead of installing interactively.** The build fetches a static `apk` and keys (verifying SHA-256), runs `apk --root $ROOTFS --initdb add $GUEST_PACKAGES`, copies an overlay, regenerates the initramfs, packs a partitionless ext4 image into a compressed qcow2, and writes `manifest.toml` (guest version, kernel command line, SSH user, host-key fingerprint) and `sha256sums.txt`. The package list is a single variable, so a diff shows what the image contains:

   ```bash
   # guest/build-image.sh L41-L55
   # OS_PLAN.md T2.1: deliberately no openrc — busybox init + the hand-written
   # inittab in rootfs-overlay/. linux-virt pulls mkinitfs automatically.
   # alpine-release provides /etc/alpine-release (the manifest stamps its exact
   # version) and a stock /etc/os-release that apply_branding later overwrites with
   # the gatOS-branded one (split out of baselayout in modern Alpine).
   # e2fsprogs-extra carries resize2fs (busybox has none): init-gatos grows the root
   # ext4 online to fill a host-resized overlay (config disk_size_gb). The base image
   # stays small (DISK_SIZE_MB); the per-save overlay is what the host grows.
   GUEST_PACKAGES="alpine-baselayout alpine-release busybox busybox-suid musl musl-utils
                   alpine-keys apk-tools linux-virt
                   openssh openssh-server openssh-client openssh-keygen openssh-sftp-server
                   qemu-guest-agent ca-certificates
                   e2fsprogs e2fsprogs-extra
                   mandoc man-pages bash zsh shadow vim neovim less curl wget git coreutils procps-ng
                   libstdc++"
   ```

3. **Use a minimal init and supervise services by respawn.** No OpenRC: busybox init runs one boot script and respawns the services, so a service that dies is restarted without extra tooling:

   ```
   // guest/rootfs-overlay/etc/inittab L1-L11
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

   A supervisor that waits for a device instead of exec'ing immediately avoids tripping init's respawn throttling:

   ```
   // guest/rootfs-overlay/sbin/qga-gatos L1-L12
   #!/bin/sh
   # qemu-guest-agent launcher (respawned by busybox init). Waits for the QGA
   # virtio-serial port instead of exec'ing qemu-ga directly: when the device is
   # absent (or slow to probe) a fast-exiting qemu-ga would trip init's respawn
   # throttling; this loop keeps the respawn quiet and robust.
   export PATH=/usr/sbin:/usr/bin:/sbin:/bin

   P=/dev/virtio-ports/org.qemu.guest_agent.0
   while [ ! -e "$P" ]; do
       sleep 2
   done
   exec /usr/bin/qemu-ga -m virtio-serial -p "$P"
   ```

4. **Boot the artefacts as the last build step.** The smoke test starts QEMU with the built kernel, initramfs and image, forwards guest port 22 to a free loopback port, and waits for the SSH banner. `snapshot=on` sends all writes to a throwaway file so the shipped image stays pristine; the accelerator is KVM when `/dev/kvm` is writable and TCG otherwise:

   ```bash
   # guest/build-image.sh L323-L362
   smoke_test() {
       log "smoke test: booting out/ artifacts (timeout ${SMOKE_TIMEOUT}s)"
       for t in qemu-system-x86_64 qemu-img ssh ssh-keyscan python3; do
           command -v "$t" >/dev/null || die "smoke test needs $t"
       done
       [ -f "$OUT/manifest.toml" ] || die "no out/manifest.toml — build first (or guest/fetch-guest.sh)"

       local cmdline accel cpu port t0 deadline
       cmdline="$(sed -n 's/^kernel_cmdline = "\(.*\)"$/\1/p' "$OUT/manifest.toml")"
       [ -n "$cmdline" ] || die "manifest.toml has no kernel_cmdline"
       accel="${GATOS_SMOKE_ACCEL:-}"
       if [ -z "$accel" ]; then
           if [ -e /dev/kvm ] && [ -w /dev/kvm ]; then accel=kvm; else accel=tcg; fi
       fi
       cpu=max; [ "$accel" = kvm ] && cpu=host
       port="$(free_port)"
       SMOKE_TMP="$(mktemp -d)"
       trap smoke_cleanup EXIT
       # snapshot=on: all writes go to a throwaway temp file, base.qcow2 stays pristine.
       qemu-system-x86_64 \
           -accel "$accel" -M q35 -cpu "$cpu" -m 256 -smp 2 \
           -kernel "$OUT/vmlinuz-virt" -initrd "$OUT/initramfs-virt" \
           -append "$cmdline" \
           -drive "file=$OUT/base.qcow2,if=virtio,format=qcow2,snapshot=on" \
           -netdev "user,id=n0,hostfwd=tcp:127.0.0.1:$port-:22" \
           -device virtio-net-pci,netdev=n0 \
           -display none -serial "file:$SMOKE_TMP/serial.log" -monitor none -no-reboot &
       SMOKE_QEMU_PID=$!

       t0="$(date +%s)"
       deadline=$((t0 + SMOKE_TIMEOUT))
       local rc=0
       wait_for_ssh_banner "$port" "$deadline" "$SMOKE_QEMU_PID" || rc=$?
       if [ "$rc" != 0 ]; then
           echo "--- serial.log tail ---" >&2; tail -40 "$SMOKE_TMP/serial.log" >&2 || true
           [ "$rc" = 2 ] && die "QEMU exited before sshd came up"
           die "sshd not reachable within ${SMOKE_TIMEOUT}s (accel=$accel)"
       fi
       local boot_secs=$(( $(date +%s) - t0 ))
       log "cold boot -> sshd: ${boot_secs}s (accel=$accel)"
   ```

5. **Poll for readiness with a deadline, not a sleep.** A tiny Python probe connects and checks the first four bytes are `SSH-`; it returns distinct codes for "QEMU died" and "timed out", and the caller prints the tail of the serial log on failure:

   ```bash
   # guest/build-image.sh L301-L312
   wait_for_ssh_banner() {  # $1=port $2=deadline-epoch $3=qemu-pid -> 0 when sshd answers
       while [ "$(date +%s)" -lt "$2" ]; do
           kill -0 "$3" 2>/dev/null || return 2
           if python3 -c 'import socket,sys
   try:
       s=socket.create_connection(("127.0.0.1",int(sys.argv[1])),1);s.settimeout(2)
       sys.exit(0 if s.recv(4).startswith(b"SSH-") else 1)
   except Exception: sys.exit(1)' "$1" 2>/dev/null; then return 0; fi
           sleep 0.25
       done
       return 1
   }
   ```

6. **Publish a versioned release from CI.** The workflow installs the build dependencies, runs the script (the smoke test runs inside it), and uploads the artefacts to `guest-v<N>` where N comes from `guest/GUEST_VERSION`; an existing tag has its assets replaced, and a new immutable guest release is a manual version bump:

   ```yaml
   # .github/workflows/guest-image.yml L31-L63
   jobs:
     build-guest:
       runs-on: ubuntu-latest
       steps:
         - name: Checkout
           uses: actions/checkout@v6

         - name: Install build dependencies
           run: |
             sudo apt-get update -qq
             sudo apt-get install -y -qq --no-install-recommends \
               qemu-system-x86 qemu-utils e2fsprogs openssh-client curl python3

         - name: Build guest image (with boot smoke test)
           run: sudo --preserve-env=GATOS_SMOKE_TIMEOUT guest/build-image.sh

         - name: Publish guest-v<N> release
           if: github.event_name == 'workflow_dispatch' || github.ref == 'refs/heads/main'
           env:
             GH_TOKEN: ${{ github.token }}
           run: |
             TAG="guest-v$(cat guest/GUEST_VERSION)"
             ALPINE="$(sed -n 's/^alpine_version = "\(.*\)"$/\1/p' guest/out/manifest.toml)"
             ASSETS=(guest/out/base.qcow2 guest/out/vmlinuz-virt guest/out/initramfs-virt
                     guest/out/manifest.toml guest/out/id_ed25519 guest/out/id_ed25519.pub
                     guest/out/host_key_fingerprint.txt guest/out/sha256sums.txt)
             if gh release view "$TAG" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
               gh release upload "$TAG" "${ASSETS[@]}" --repo "$GITHUB_REPOSITORY" --clobber
             else
               gh release create "$TAG" "${ASSETS[@]}" --repo "$GITHUB_REPOSITORY" \
                 --title "gatOS guest image v$(cat guest/GUEST_VERSION)" \
                 --notes "Alpine $ALPINE guest image for gatOS (built by guest-image.yml from guest/ at $GITHUB_SHA). Fetch with guest/fetch-guest.sh — do not download by hand."
             fi
   ```

7. **Fetch by pin and verify.** Consumers never build the image. The fetch script reads the version pin, does nothing when the installed manifest already matches, downloads the assets into a temp directory and verifies `sha256sums.txt` before moving anything into place:

   ```bash
   # guest/fetch-guest.sh L6-L38
   set -euo pipefail

   SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
   OUT="$SCRIPT_DIR/out"
   REPO="${GATOS_GUEST_REPO:-meow-sci/gatOS}"
   VERSION="$(tr -d '[:space:]' < "$SCRIPT_DIR/GUEST_VERSION")"
   TAG="guest-v$VERSION"
   ASSETS=(base.qcow2 vmlinuz-virt initramfs-virt manifest.toml
           id_ed25519 id_ed25519.pub host_key_fingerprint.txt sha256sums.txt)

   if [ -f "$OUT/manifest.toml" ] && grep -q "^guest_version = $VERSION\$" "$OUT/manifest.toml"; then
       echo "guest/out/ already at guest_version=$VERSION — nothing to do"
       exit 0
   fi

   TMP="$(mktemp -d "$SCRIPT_DIR/.fetch-tmp.XXXXXX")"
   trap 'rm -rf "$TMP"' EXIT

   echo "==> fetching $TAG from $REPO"
   if command -v gh >/dev/null; then
       gh release download "$TAG" --repo "$REPO" --dir "$TMP"
   else
       for a in "${ASSETS[@]}"; do
           curl -fsSL -o "$TMP/$a" "https://github.com/$REPO/releases/download/$TAG/$a"
       done
   fi

   echo "==> verifying checksums"
   if command -v sha256sum >/dev/null; then
       ( cd "$TMP" && sha256sum -c --quiet sha256sums.txt )
   else
       ( cd "$TMP" && shasum -a 256 -c --quiet sha256sums.txt )   # macOS
   fi
   ```

8. **Use static keys on purpose when the guest is local-only.** The SSH host key and session key pair are committed under `guest/keys/`, so the host-key pin never drifts across rebuilds and access is only through the loopback port forward.

## Complete example

| File | Role |
|---|---|
| [guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh) | The image build and smoke test |
| [guest/rootfs-overlay/etc/inittab](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/etc/inittab) | busybox init with respawned supervisors |
| [guest/rootfs-overlay/sbin/qga-gatos](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/sbin/qga-gatos) | Device-waiting launcher for the guest agent |
| [guest/fetch-guest.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/fetch-guest.sh) | Pinned release download and checksum verification |
| [.github/workflows/guest-image.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/guest-image.yml) | CI build, smoke boot and release |
| [guest/GUEST_VERSION](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/GUEST_VERSION) | Version pin |

## Variations and gotchas

- **The smoke test needs real tools.** QEMU, `qemu-img`, `ssh`, `ssh-keyscan` and `python3` are required; the script dies with a message naming the missing one.
- **KVM is not guaranteed.** Standard `ubuntu-latest` runners expose it; without it the run falls back to TCG and takes longer, so give the timeout headroom (`GATOS_SMOKE_TIMEOUT`).
- **Immutable versus refreshed releases.** Re-running a push to `main` replaces the assets of the pinned release in place; a fresh immutable release needs a version bump.
- **A new guest version needs host support.** The host's boot logic is version-pinned; old overlays keep booting their own kernel.
- **Game updates.** The image does not depend on a KSA build; the host side that reads the manifest is the only game-adjacent part.

## See also

- [Overview](overview.md); [approach 3](run-repo-checks-from-a-pre-push-hook-and-ci-3.md), [approach 1](run-repo-checks-from-a-pre-push-hook-and-ci-1.md)
- [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md), [Package and publish a mod release](../../../getting-started/build/package-and-publish-a-mod-release/overview.md)
- Mod feature: [gatOS Alpine guest image](../../../../mods/gatos/features/alpine-guest-image.md)
