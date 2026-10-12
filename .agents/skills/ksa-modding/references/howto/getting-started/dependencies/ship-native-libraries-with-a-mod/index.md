# Game Feature

* [Ship and load native libraries from a mod folder](overview.md) - Why StarMap's per-mod load context leaves native libraries to default probing, and two ways to ship and load them with an explicit resolver - flat per-OS files and a per-RID payload with an ABI check

# How-To

* [Load flat per-OS native libraries with a DllImportResolver and a libc mapping](ship-native-libraries-with-a-mod-1.md) - Ship Windows, Linux and macOS native libraries side by side and pick the right file at the first P/Invoke with a ModuleInitializer resolver, because StarMap's mod context leaves native libraries to default probing
* [Ship a per-RID native payload from the deploy targets and bind it with layout-exact structs](ship-native-libraries-with-a-mod-2.md) - Build native solver libraries per runtime identifier, list them in one payload item list shared by deploy and release, load them with a resolver beside the assembly, and prove the C ABI matches the managed structs
