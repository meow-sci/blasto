---
type: Mod Feature
title: Vendored libghostty-vt binding as the terminal engine
description: Delegate all VT emulation to Ghostty's standalone libghostty-vt through a vendored, pruned and patched .NET binding with source-generated P/Invoke, a managed packed-cell decoder and a startup layout self-check
mod: purrtty
category: external-io
techniques: [native-interop]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: []
tags: [native, p-invoke, terminal-emulation, ghostty, zig, vendoring]
---

purrTTY does not contain a terminal emulator. It feeds PTY bytes to **libghostty-vt**, the
standalone, conformance-tested VT engine extracted from Ghostty, and reads back a render state.
The .NET binding (originally `deblasis/libghostty-vt-dotnet`) is vendored under
`vendor/Ghostty.Vt/`, retargeted to `net10.0`, pruned to the surface purrTTY calls, fixed where the
upstream binding had lifetime bugs, and extended with a fast frame-read path. Prebuilt shared
libraries for win-x64, linux-x64 and osx-arm64 are checked in next to it. This is the template for
any KSA mod that wants to embed a native library rather than reimplement it in C#.

## How it works

| Layer | Contents |
|---|---|
| Native | `native/<rid>/` `ghostty-vt.dll`, `libghostty-vt.so`, `libghostty-vt.dylib`, built with zig 0.15 from a pinned ghostty commit plus three vendored patches (`native-patches/*.patch`) |
| P/Invoke | `NativeMethods` (`[LibraryImport("ghostty-vt")]`, source-generated, `unsafe partial`), plus selection and tracked-grid-ref partials |
| Handles | `GhosttySafeHandle` subclasses for `Terminal`, `RenderState`, `KeyEncoder` and `MouseEncoder`; key and mouse events manage their own handles with double-free guards |
| Engine API | `Terminal` (`VTWrite`, `Resize`, modes, scroll, title, kitty storage limit), `RenderState` (`Update`, `Dirty`, colors), `KeyEncoder`, `MouseEncoder`, `Paste` |
| purrTTY additions | `RenderFrameReader` and `RawCell` (decode the packed 64-bit `page.Cell` in managed code: content tag, codepoint, bg color, style id, width), dirty-flag clearing, selection gestures, `TrackedGridRef`, `KittyPlacementCursor` with an FNV-1a payload hash, configurable `MaxScrollback` (bytes), `RawCellLayout.Validate()` |
| Fixes | Encoders copy their result out of a `stackalloc` buffer instead of returning a dangling span; `KeyEvent.Text` keeps a persistent pin; reply buffers pinned; paste encodes a mutable copy |

**Self-check.** `RawCellLayout.Validate()` writes known content into a throwaway terminal and
compares the managed bit decode with the native `ghostty_cell_get` accessors.
`GhosttyTerminalSurface` runs it once per process at construction and logs an error on mismatch;
a test runs it on every CI platform. A native pin bump that changes the cell layout therefore
fails loudly instead of rendering garbage.[^surface][^frame-reader]

**Build.** One host cross-compiles all three RIDs (`zig build -Demit-lib-vt -Dtarget=...
-Doptimize=ReleaseFast`), Windows with the `gnu` ABI, Linux pinned to glibc 2.31, never
`-mcpu native`; only the shared libraries are vendored (static and import libs cannot be loaded at
runtime).[^vt-readme][^build-and-test] The csproj copies all three libraries flat into every build
output; the filenames never collide, so one build runs on every OS.[^vt-csproj]

## Lifecycle and timing

- The engine is single-threaded: every call except the PTY byte enqueue runs on the thread that
  ticks the surface, which purrTTY makes the KSA main thread inside `[StarMapAfterGui]`
  (`frame:draw-ui`) (gotcha 1).[^gotchas]
- The native library is loaded lazily on the first P/Invoke, after the module initializer has
  registered the resolver (see [native library resolver](./per-platform-native-library-resolver.md)).
- Callbacks from native (`OnWritePty`, bell, title, device attributes) fire synchronously inside
  `VTWrite` on that same thread; purrTTY only records them and acts after the call returns.

## Key code

| Path | Role |
|---|---|
| [vendor/Ghostty.Vt/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/README.md) | Provenance, pin, divergence list, rebuild commands, upgrade workflow |
| [vendor/Ghostty.Vt/src/Native/NativeMethods.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeMethods.cs#L1-L30) | `LibraryImport` surface |
| [vendor/Ghostty.Vt/src/RenderState.FrameReader.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/RenderState.FrameReader.cs#L56-L106) | `RawCell` managed decode |
| [vendor/Ghostty.Vt/src/RenderState.FrameReader.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/RenderState.FrameReader.cs#L276-L330) | `RawCellLayout.Validate` |
| [vendor/Ghostty.Vt/src/KeyEncoder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/KeyEncoder.cs#L25-L43) | Encoder returning owned bytes |
| [vendor/Ghostty.Vt/src/KittyGraphics.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/KittyGraphics.cs) | Read-only kitty placement cursor and payload hash |
| [vendor/Ghostty.Vt/src/TrackedGridRef.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/TrackedGridRef.cs) | Engine-tracked selection anchor |
| [vendor/Ghostty.Vt/native-patches/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/native-patches/README.md) | The three native patches and when to drop them |
| [vendor/Ghostty.Vt/Ghostty.Vt.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/Ghostty.Vt.csproj) | Relaxed warnings for vendored code, flat native copy, `InternalsVisibleTo` |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L269-L316) | Engine construction, callbacks, kitty storage limit, layout check |

## Snippets

```csharp
// vendor/Ghostty.Vt/src/RenderState.FrameReader.cs L64-L78
public readonly struct RawCell
{
    public const int TagCodepoint = 0;
    public const int TagCodepointGrapheme = 1;
    public const int TagBgColorPalette = 2;
    public const int TagBgColorRgb = 3;

    public readonly ulong Bits;

    public RawCell(ulong bits) => Bits = bits;

    public int ContentTag => (int)(Bits & 0x3);

    /// <summary>First (or only) codepoint; 0 for blank and bg-only cells.</summary>
    public uint Codepoint => ContentTag <= TagCodepointGrapheme ? (uint)((Bits >> 2) & 0x1FFFFF) : 0u;
```

```csharp
// vendor/Ghostty.Vt/src/KeyEncoder.cs L25-L43
    public unsafe byte[] Encode(KeyEvent keyEvent)
    {
        ObjectDisposedException.ThrowIf(_handle.IsInvalid, this);
        Span<byte> buf = stackalloc byte[256];
        fixed (byte* ptr = buf)
        {
            nuint len;
            var result = NativeMethods.ghostty_key_encoder_encode(
                _handle.DangerousGetHandle(), keyEvent.NativeHandle, ptr, 256, &len);
            GhosttyException.ThrowIfFailure(result);
            // purrtty fix: copy out of the stack buffer while it is still alive.
            // Returning a span into `buf` (upstream behavior) is a
            // use-after-scope (the stackalloc is freed when this method
            // returns); the caller then reads clobbered stack memory. That was
            // the real cause of the cross-platform "first-use misfire" (garbage
            // byte read: 0x00 on macOS, 0xB0 on Windows x64).
            return buf[..(int)len].ToArray();
        }
    }
```

```csharp
// purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L276-L288
        _terminal = new VtTerminal(cols, rows, opts =>
        {
            if (maxScrollback is { } mb)
            {
                opts.MaxScrollback = mb;
            }

            opts.OnWritePty = span => _replies.AddRange(span);
            opts.OnBell = () => _bellPending = true;
            opts.OnTitleChanged = () => _titlePending = true;
            opts.OnDeviceAttributes = () => DeviceAttributesReply;
        });
        _terminal.KittyImageStorageLimit = KittyImageStorageLimitBytes;
```

## Game APIs touched

None. The binding and the engine are game-independent; KSA only matters for where the native
library is loaded from (the mod folder) and on which thread it is driven.

## Pitfalls and version notes

- Any native API that writes into a caller buffer or keeps a pointer past the call needs explicit
  lifetime handling in the binding; three such bugs existed upstream (README divergence list).[^vt-readme]
- The library's own kitty image storage default (10 MB) is too small for video-style streams;
  purrTTY sets 256 MiB at construction (gotcha 36).
- `MaxScrollback` is in bytes, not lines, despite the C header comment.
- The kitty keyboard protocol encoder emits nothing for letter keys unless the event carries the
  unshifted codepoint (gotcha 3).
- Native patches matter: building bare upstream reintroduces a pin leak, the zlib heap corruption
  (a zig 0.15.2 std bug) and loses the APC bulk-parse speedup.[^patches-readme]
- Third-party licenses for the engine and its SIMD deps (highway, simdutf) ship in the mod folder.[^notices]

## See also

- [Per-platform native library resolver](./per-platform-native-library-resolver.md)
- [Headless terminal surface and frame snapshot](./headless-terminal-surface-and-frame-snapshot.md)
- [ImGui keyboard and mouse to terminal input](./imgui-keyboard-mouse-to-vt-input.md)
- [Cross-platform build and release](./cross-platform-build-and-release.md)
- [purrTTY overview](../overview.md)
- [Vendored libghostty-vt binding with managed packed-cell decoding and a startup self-check](../../../howto/external-io/processes/bind-and-vendor-native-c-libraries/bind-and-vendor-native-c-libraries-1.md) - how-to article demonstrating this feature

[^surface]: purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs
[^frame-reader]: vendor/Ghostty.Vt/src/RenderState.FrameReader.cs
[^vt-readme]: vendor/Ghostty.Vt/README.md
[^build-and-test]: purrTTY docs/build-and-test.md
[^vt-csproj]: vendor/Ghostty.Vt/Ghostty.Vt.csproj
[^gotchas]: purrTTY docs/gotchas.md
[^patches-readme]: vendor/Ghostty.Vt/native-patches/README.md
[^notices]: purrTTY THIRD-PARTY-NOTICES.md
