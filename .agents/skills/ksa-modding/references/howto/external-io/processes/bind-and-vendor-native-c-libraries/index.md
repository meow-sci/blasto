# Game Feature

* [Bind and vendor native C libraries with layout-exact P/Invoke](overview.md) - Call a C or Rust library from a KSA mod safely with layout-exact structs checked against the headers, a startup layout self-check, thread ownership rules, per-RID builds, a vendored and patched upstream binding and the Apple arm64 variadic-call pitfall

# How-To

* [Clarabel and SCS solvers with layout-exact structs, reused sparse CSC patterns and single-thread ownership](bind-and-vendor-native-c-libraries-2.md) - Bind two native conic solvers with struct layouts copied from their C headers and proven by a C probe, one-byte bools, widened index arrays, per-call pinning and a rule that a solver instance is never touched by two threads
* [Pad register arguments for variadic C calls such as ioctl on Apple arm64](bind-and-vendor-native-c-libraries-3.md) - Call a variadic libc function from C# on Apple arm64 by importing it a second time with filler arguments so the real argument lands on the stack where the variadic ABI looks for it
* [Vendored libghostty-vt binding with managed packed-cell decoding and a startup self-check](bind-and-vendor-native-c-libraries-1.md) - Embed a native library by vendoring its .NET binding, marking every local change, cross-compiling shared libraries for three runtime ids and failing loudly at startup if the native layout drifts
