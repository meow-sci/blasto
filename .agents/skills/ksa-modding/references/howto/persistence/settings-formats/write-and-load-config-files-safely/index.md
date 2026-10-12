# Game Feature

* [Write config files atomically and load hand-edited ones without losing them](overview.md) - Write rules (atomic replace, save on change, debounce, flush at unload) and load rules (clamp, drop the broken field, migrate, never overwrite an unparseable file) for mod config files

# How-To

* [Flush on close and unload, skip junk keys on load and migrate the previous format](write-and-load-config-files-safely-4.md) - Write side - a dirty flag set by editors, open flags compared every frame, written when the window closes and again at StarMapUnload; load side - unknown keys and malformed values skipped, the old flat color format and purrTTY's legacy flat settings converted into the current structure
* [Mark dirty, debounce, swap the file in atomically, and retry deserialisation by dropping the broken field](write-and-load-config-files-safely-1.md) - Write side - MarkDirty from the UI, Flush(dt) from the draw loop after one second of quiet, a temp file and File.Move overwrite, a final SaveNow at unload; load side - parse to a JsonObject, remove the fragile field and retry on JsonException, parse sections independently, then sanitise
* [Save from Task.Run with an atomic helper, clamp bad values on load and leave an unparseable file alone](write-and-load-config-files-safely-3.md) - Write side - an AtomicFile helper called from a thread-pool task so the render thread does no file I/O; load side - Normalize clamps every numeric knob into a bootable range with a warning, and a parse error or unknown schema returns in-memory defaults without touching the file
* [Skip the dirty flag by comparing the serialised snapshot with the last write](write-and-load-config-files-safely-2.md) - Capture the live state into a DTO on a fixed Stopwatch interval, serialise it and write only when the string differs from the last one, so a clean load never writes and no UI code has to remember to mark anything dirty
