---
type: How-To
title: Vendored libghostty-vt binding with managed packed-cell decoding and a startup self-check
description: Embed a native library by vendoring its .NET binding, marking every local change, cross-compiling shared libraries for three runtime ids and failing loudly at startup if the native layout drifts
category: external-io
group: processes
topic: bind-and-vendor-native-c-libraries
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/vendored-libghostty-vt-binding]
techniques: [native-interop, assembly-load-context]
hooks: []
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: []
tags: [vendoring, libraryimport, safehandle, zig, rid, self-check, divergence, native-patches]
---

Outcome: purrtty does not contain a terminal emulator. It feeds PTY bytes to libghostty-vt, the standalone VT engine extracted from Ghostty, and reads back a render state. The .NET binding (originally `libghostty-vt-dotnet`) is vendored, retargeted, pruned to the surface purrtty calls, fixed where the upstream had lifetime bugs, and extended with a fast frame-read path; prebuilt shared libraries for three runtime ids are checked in beside it. This is the template for any KSA mod that wants to embed a native library rather than reimplement it. Demonstrated by [Vendored libghostty-vt binding](../../../../mods/purrtty/features/vendored-libghostty-vt-binding.md) in purrtty.

## Background: how the game does it

The game offers nothing; the loading problem is StarMap's. The mod's assembly loads in its own context, where default native probing does not reliably search the assembly directory, so the binding registers its own resolver (below). The engine is single-threaded: every call except the PTY byte enqueue runs on the thread that ticks the surface, which purrtty makes the KSA main thread inside `[StarMapAfterGui]` (see [Host a terminal emulator in the game](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)). Native callbacks (write-to-pty, bell, title, device attributes) fire synchronously inside `VTWrite` on that same thread, so the mod only records them and acts after the call returns.

## Lifecycle and timing

| Moment | What happens |
|---|---|
| Assembly load | A `[ModuleInitializer]` registers the `DllImportResolver` before any P/Invoke |
| First P/Invoke | The native library is loaded lazily |
| Surface construction (main thread, once per process) | The layout self-check runs; a mismatch is logged as an error |
| Each frame (`frame:draw-ui`) | The surface ticks, writes queued bytes into the engine and reads a frame |

## Steps

1. **Vendor the binding, do not consume it blindly.** Copy the managed sources into the repo, retarget to your TFM, and record provenance: upstream repo and commit, the pinned native commit, the toolchain, the vendoring date and the previous pin ([vendor/Ghostty.Vt/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/README.md#L16-L33)).

2. **Mark every local change in-file** with a greppable tag. The README states the rule: additions are marked "purrtty addition", fixes to upstream behaviour "purrtty fix", upstream is a reference only, and re-vendoring wholesale would revert the fixes. The load-bearing fixes are lifetime bugs, for example an encoder that returned a span into a dead `stackalloc`:

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

   Other fixes: `KeyEvent.Text` keeps a persistent pin (the native event stores the raw utf8 pointer and reads it at encode time), reply buffers are pinned, paste encodes a mutable copy, event handles guard against double free, and `MaxScrollback` is plumbed through ([vendor/Ghostty.Vt/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/README.md#L35-L56)).

3. **Prune what you do not call.** Parsers, formatters and enums that drifted from the pinned headers were removed rather than carried as freight; the README says to restore from upstream and re-verify against the pinned headers if a feature needs one.

4. **Use source-generated imports and safe handles.** `[LibraryImport("ghostty-vt")]` in an `unsafe partial` class, plus `SafeHandle` subclasses for the terminal, render state and encoders ([vendor/Ghostty.Vt/src/Native/NativeMethods.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeMethods.cs#L1-L30)).

5. **Register a resolver from a module initializer.** It maps the logical name to the platform file name and tries the assembly directory (and `runtimes/<rid>/native`) before the OS default; the registration is idempotent because `SetDllImportResolver` throws if called twice for one assembly:

   ```csharp
   // vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs L21-L58
       private const string LibraryName = "ghostty-vt";

       private static int _registered;

       [ModuleInitializer]
       internal static void Register()
       {
           // Idempotent: SetDllImportResolver throws if called twice for one assembly.
           if (Interlocked.Exchange(ref _registered, 1) != 0)
           {
               return;
           }

           NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
       }

       private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
       {
           if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
           {
               return nint.Zero;
           }

           var fileName = PlatformFileName();
           var assemblyDir = Path.GetDirectoryName(assembly.Location);

           foreach (var candidate in CandidatePaths(assemblyDir, fileName))
           {
               if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
               {
                   return handle;
               }
           }

           // Fall back to the OS loader's default search (handles cases where the
           // library is installed system-wide or already resolvable by name).
           return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallback)
               ? fallback
   ```

6. **Decode hot data in managed code, and prove the decode.** Rather than two native calls per cell field, purrtty decodes the packed 64-bit cell directly (`RawCell`), which makes the code depend on the native layout. `RawCellLayout.Validate()` writes known content (wide styled text, plain text, cells erased under an RGB and a palette background) into a throwaway terminal and compares the managed decode with the native `ghostty_cell_get` accessors:

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

   The mod runs it once per process when the first surface is constructed and logs an error on mismatch; a test runs it on every CI platform. A native pin bump that changes the layout therefore fails loudly instead of rendering garbage:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L300-L316
           if (Interlocked.Exchange(ref s_cellLayoutChecked, 1) == 0)
           {
               try
               {
                   if (!VtRawCellLayout.Validate(out var layoutError))
                   {
                       _logger.LogError(
                           "libghostty-vt packed-cell layout mismatch — cell rendering will be wrong until the managed decode is updated: {Error}",
                           layoutError);
                   }
               }
               catch (Exception ex)
               {
                   _logger.LogWarning(ex, "libghostty-vt cell layout self-check failed to run");
               }
           }
       }
   ```

7. **Build natives reproducibly, per runtime id.** One host cross-compiles all three libraries from the pinned commit with zig 0.15 at `ReleaseFast` with explicit baseline targets (never `-mcpu native`; a host-tuned binary access-violated inside `vt_write`). Windows uses the `gnu` ABI; Linux pins glibc 2.31. Only shared libraries are vendored: static archives and import libs cannot be loaded at runtime. Three native patches ship in `native-patches/` with notes on when to drop them ([vendor/Ghostty.Vt/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/README.md#L98-L140), [vendor/Ghostty.Vt/native-patches/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/native-patches/README.md)).

8. **Copy all runtime ids flat into every build.** The file names never collide (`ghostty-vt.dll`, `libghostty-vt.so`, `libghostty-vt.dylib`), so one build output runs on every OS and a single Linux CI job can produce the release for all platforms; if two RIDs of one OS are ever added, switch to the `runtimes/<rid>/native` layout, which the resolver already probes ([vendor/Ghostty.Vt/Ghostty.Vt.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/Ghostty.Vt.csproj)).

## Complete example

| File | Role |
|---|---|
| [vendor/Ghostty.Vt/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/README.md) | Provenance, pin, divergence list, rebuild commands, upgrade workflow |
| [vendor/Ghostty.Vt/src/Native/NativeMethods.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeMethods.cs) | `LibraryImport` surface |
| [vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs) | Module-initializer resolver |
| [vendor/Ghostty.Vt/src/RenderState.FrameReader.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/RenderState.FrameReader.cs) | `RawCell` decode and `RawCellLayout.Validate` |
| [vendor/Ghostty.Vt/src/KeyEncoder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/KeyEncoder.cs) | Encoder returning owned bytes |
| [vendor/Ghostty.Vt/native-patches/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/native-patches/README.md) | The native patches and when to drop them |
| [vendor/Ghostty.Vt/Ghostty.Vt.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/Ghostty.Vt.csproj) | Relaxed warnings for vendored code, flat native copy, `InternalsVisibleTo` |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs) | Engine construction, callbacks, layout check |

## Variations and gotchas

- **Pin-bump checklist.** Rebuild all three natives, run the layout check and the regression tests, re-verify C enums against the headers (they are 0-based), and re-read the divergence list before touching vendored files.
- **Header comments can be wrong.** The README records that the scrollback limit is in bytes though the upstream header comment says lines; do not "fix" the binding doc to match the header.
- **Vendored code is not held to your warning policy.** The csproj relaxes warnings-as-errors and documentation for the vendored project.
- **A native crash is a game crash.** Keep native calls on one thread, validate inputs managed-side, and prefer failing the check at startup.

## See also

- [Overview](overview.md)
- [Approach 2: Clarabel and SCS](bind-and-vendor-native-c-libraries-2.md), [Approach 3: variadic ioctl on Apple arm64](bind-and-vendor-native-c-libraries-3.md)
- [Vendored libghostty-vt binding](../../../../mods/purrtty/features/vendored-libghostty-vt-binding.md)
- [Ship native libraries with a mod](../../../getting-started/dependencies/ship-native-libraries-with-a-mod/overview.md)
- [Host a terminal emulator in the game with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
