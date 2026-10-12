---
type: How-To
title: Save a user-built layout as a versioned snake_case JSON file anchored to a body by latitude and longitude
description: Write each user-built layout as its own indented JSON document with a version, stable piece ids, and a position stored as latitude, longitude, heading and height above the ground, then reload it tolerantly by dropping unknown ids, refusing newer versions and warning when the terrain has moved
category: persistence
group: user-data
topic: persist-user-created-presets-and-content
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/course-grid-and-save-file]
techniques: [json-config]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.Universe, KSA.CelestialSystem]
tags: [json, versioning, layouts, latitude, longitude, ground-anchor, user-content, snake-case]
---
Outcome: the player builds something on a planet (KSAGolf's minigolf course, assembled from catalogue pieces), presses *Save*, and later loads it back in the same place, on the same body, even after a game update changed the terrain. The file is a standalone JSON document per layout, not part of any KSA save. Demonstrated by [Course model, grid and save file](../../../../mods/ksagolf/features/course-grid-and-save-file.md). The grid model, port-on-edge design, snapping and undo stacks of that mod are deliberately not taught here; only the persistence of a placed layout is.

## Background: how the game does it

Bodies are `Celestial` objects found through `Universe.CurrentSystem` (a `CelestialSystem`) by id; their surface frame is body-fixed ("CCF"). The game converts latitude and longitude to a body-fixed unit direction in `Celestial.GetDirCcfFromLatLon`: z is `sin(lat)`, x is `cos(lat)cos(lon)`, y is `cos(lat)sin(lon)` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L667-L675)). A saved position that uses the same convention stays meaningful if the body rotates or the system is reloaded. Absolute radius is not stable across game builds, which is why the file stores height above the ground instead.

## Lifecycle and timing

Everything here is plain data on the main thread. *Save* and *Load* are button handlers in the mod's panel, drawn from `[StarMapAfterGui]` (`frame:draw-ui`), and are also reachable from the mod's file bridge; both do synchronous file I/O of a few kilobytes. Nothing loads a course when a KSA save loads, and a save load makes the builder forget the course in memory (see [Detect a save load or quickload and reset mod state safely](../../saves/detect-save-load/overview.md)). See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Make the catalogue ids append-only.** A saved layout names its pieces, so an id you rename or reuse silently breaks every file already on disk. KSAGolf's catalogue says so in its type comment:

   ```csharp
   // src/KSAGolf/Sim/PieceCatalogue.cs L101-L109, L122
   /// <summary>The pieces a course is built from. Ids are append-only: a saved course names them.</summary>
   internal static class PieceCatalogue
   {
       private static Port P(int i, int j, Side side, int level = 0) => new(new Cell(i, j), side, level);

       public static readonly PieceDef[] All =
       [
           new("tee", "Tee", PieceKind.Tee, 1, 1, [P(0, 0, Side.North)], "KSAGolf_Piece_Tee_Glb"),
           new("cup", "Cup", PieceKind.Cup, 1, 1, [P(0, 0, Side.South)], "KSAGolf_Piece_Cup_Glb"),
   // ...
       public static PieceDef? Find(string id) => All.FirstOrDefault(d => d.Id == id);
   ```

2. **Define the file as an immutable record with a version.** Snake_case names (`latitude_deg`, `floor_above_ground_m`), indented output, and every field written even when default. The record carries the anchor, not the geometry of the world:

   ```csharp
   // src/KSAGolf/Sim/CourseFile.cs L24-L60
   /// <summary>
   /// A course as saved: where it stands on which body, and its pieces. The floor is kept as a height above
   /// the ground at the anchor rather than a radius, so a KSA update that reshapes the terrain moves the course
   /// with it instead of burying it.
   /// </summary>
   internal sealed record CourseFile
   {
       public const int CurrentVersion = 1;

       public int Version { get; init; } = CurrentVersion;
       public string Name { get; init; } = "";
       public int Par { get; init; } = 2;
       public string Body { get; init; } = "";
       public double LatitudeDeg { get; init; }
       public double LongitudeDeg { get; init; }

       /// <summary>The grid's +J, clockwise from north.</summary>
       public double HeadingDeg { get; init; }

       public double FloorAboveGroundM { get; init; }
       public double CellM { get; init; } = CourseSize.CellM;
       public List<SavedPiece> Pieces { get; init; } = [];

       public sealed record SavedPiece(string Def, int I, int J, int Turns, bool Mirrored, int Level);

       private static readonly JsonSerializerOptions Options = new()
       {
           WriteIndented = true,
           PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
           DefaultIgnoreCondition = JsonIgnoreCondition.Never,
       };

       public static CourseFile Of(Course course, CourseFile where) => where with
       {
           Version = CurrentVersion,
           Pieces = [.. course.Pieces.Select(p => new SavedPiece(p.Def.Id, p.Origin.I, p.Origin.J, p.Orientation.Quarter, p.Orientation.Mirrored, p.Level))],
       };
   ```

3. **Load tolerantly, refuse the future.** `ToCourse` leaves out ids this build does not know and returns their names; `FromJson` returns null with a reason for malformed input and for a file written by a newer version, rather than half-reading it:

   ```csharp
   // src/KSAGolf/Sim/CourseFile.cs L62-L100
   /// <summary>The course, leaving out pieces this build does not know, which are named in <paramref name="unknown"/>.</summary>
   public Course ToCourse(out List<string> unknown)
   {
       unknown = [];
       List<Placed> pieces = [];
       foreach (SavedPiece saved in Pieces)
       {
           if (PieceCatalogue.Find(saved.Def) is not { } def)
           {
               unknown.Add(saved.Def);
               continue;
           }

           pieces.Add(new Placed(def, new Cell(saved.I, saved.J), new Orientation(saved.Turns, saved.Mirrored), saved.Level));
       }

       return new Course(pieces);
   }

   public string ToJson() => JsonSerializer.Serialize(this, Options);

   /// <summary>The file read, or null with the reason: a course from a newer build is refused rather than half-read.</summary>
   public static CourseFile? FromJson(string json, out string why)
   {
       why = "";
       try
       {
           CourseFile? file = JsonSerializer.Deserialize<CourseFile>(json, Options);
           if (file is null) why = "empty";
           else if (file.Version > CurrentVersion) why = $"written by a newer KSAGolf (version {file.Version})";
           else return file;
       }
       catch (JsonException e)
       {
           why = e.Message;
       }

       return null;
   }
   ```

4. **Choose a folder.** Courses go to `KSAGolf/Courses` beside the folder the mod log lives in, which is KSA's own `Logs` folder when found (falling back to the temp directory); the placement trade-offs are in [Derive the folder from KSA's own Logs folder with a temp fallback](../choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-4.md). The names offered by *Load* are the `*.json` files in the folder, and an unnamed course gets the first free `Course n`:

   ```csharp
   // src/KSAGolf/Ksa/CourseBuilder.cs L442-L445
   public static string Folder => Path.Combine(Directory.GetParent(Log.Folder)?.FullName ?? Log.Folder, "KSAGolf", "Courses");

   public static IEnumerable<string> Saved()
       => Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order() : [];
   ```

5. **Save the anchor, not the radius.** Latitude and longitude come from the body-fixed up vector (the inverse of `GetDirCcfFromLatLon`), the heading is the angle of the grid axis against local north and east, and the floor is stored as a height above the ground measured at the anchor:

   ```csharp
   // src/KSAGolf/Ksa/CourseBuilder.cs L447-L484
   public bool Save()
   {
       if (_body is not { } body) return false;

       if (Name.Length == 0)
       {
           int n = 1;
           while (Saved().Contains($"Course {n}")) n++;
           Name = $"Course {n}";
       }

       double3 north = Vec.Unit(Vec.RejectFrom(new double3(0, 0, 1), _upCcf));
       double3 east = Vec.Cross(north, _upCcf);
       var where = new CourseFile
       {
           Name = Name,
           Body = body.Id,
           LatitudeDeg = double.RadiansToDegrees(Math.Asin(Math.Clamp(_upCcf.Z, -1.0, 1.0))),
           LongitudeDeg = double.RadiansToDegrees(Math.Atan2(_upCcf.Y, _upCcf.X)),
           HeadingDeg = double.RadiansToDegrees(Math.Atan2(Vec.Dot(_jCcf, east), Vec.Dot(_jCcf, north))),
           FloorAboveGroundM = _floorRadius - _groundAtAnchor,
       };

       try
       {
           Directory.CreateDirectory(Folder);
           File.WriteAllText(Path.Combine(Folder, $"{Name}.json"), CourseFile.Of(Course, where).ToJson());
           Status = $"saved as {Name}";
           Log.Info($"course '{Name}' saved: {Course.Pieces.Count} pieces on {body.Id}");
           return true;
       }
       catch (Exception e)
       {
           Status = $"could not save: {e.Message}";
           Log.Warn(Status);
           return false;
       }
   }
   ```

6. **Load by rebuilding the frame, then warn about drift.** The body is found by id (a world without it gets a status message, not an exception), up and heading are rebuilt from the angles, the floor is re-levelled against today's ground, and a difference of more than 5 cm between the saved and the new height above ground is logged. Unknown piece ids are named in the status line:

   ```csharp
   // src/KSAGolf/Ksa/CourseBuilder.cs L486-L527
   public bool Load(string name)
   {
       CourseFile? file;
       try
       {
           file = CourseFile.FromJson(File.ReadAllText(Path.Combine(Folder, $"{name}.json")), out string why);
           if (file is null)
           {
               Status = $"{name}: {why}";
               return false;
           }
       }
       catch (Exception e)
       {
           Status = $"could not read {name}: {e.Message}";
           return false;
       }

       if (KsaWorld.FindCelestial(file.Body) is not { } body)
       {
           Status = $"{name} is on {file.Body}, which this world does not have";
           return false;
       }

       double lat = double.DegreesToRadians(file.LatitudeDeg), lon = double.DegreesToRadians(file.LongitudeDeg);
       double3 up = new(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
       double3 north = Vec.Unit(Vec.RejectFrom(new double3(0, 0, 1), up));
       double3 east = Vec.Cross(north, up);
       double heading = double.DegreesToRadians(file.HeadingDeg);
       double3 j = (north * Math.Cos(heading)) + (east * Math.Sin(heading));

       Frame(body, up, Vec.Cross(j, up), j);
       _edits = new CourseEdits(file.ToCourse(out List<string> unknown));
       Name = file.Name.Length > 0 ? file.Name : name;
       Changed();

       double drift = (_floorRadius - _groundAtAnchor) - file.FloorAboveGroundM;
       if (Math.Abs(drift) > 0.05) Log.Warn($"course '{Name}' stands {drift * 100:F0} cm off where it was saved: the ground under it has changed");
       if (unknown.Count > 0) Status = $"left out pieces this version does not know: {string.Join(", ", unknown)}";
       Log.Info($"course '{Name}' loaded: {Course.Pieces.Count} pieces on {body.Id}");
       return true;
   }
   ```

7. **Find the body by id without holding references.** `KsaWorld.FindCelestial` walks `Universe.CurrentSystem` by index and returns null if no system is loaded, so loading a file before a flight scene exists fails softly:

   ```csharp
   // src/KSAGolf/Ksa/KsaWorld.cs L507-L516
   public static Celestial? FindCelestial(string id)
   {
       try
       {
           if (Universe.CurrentSystem is not { } system) return null;

           for (int i = 0; i < system.Count; i++)
           {
               if (system.GetIndex(i) is Celestial body && body.Id == id) return body;
           }
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/CourseFile.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseFile.cs) | Versioned record, serializer options, `ToCourse`, `FromJson` |
| [src/KSAGolf/Sim/PieceCatalogue.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PieceCatalogue.cs#L101-L122) | Append-only piece ids and `Find` |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L440-L527) | Folder, save, load, drift warning |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L217-L221) | *Save* and *Load* buttons |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L507-L516) | Body lookup by id |

## Variations and gotchas

- **Never rename or remove an id.** Unknown ids are dropped and named rather than thrown, but the hole is then broken. Treat the catalogue as an API.
- **Refuse newer files with a reason.** A version number plus an explicit refusal is cheaper than a silent half-read; consider the same for a file older than your oldest migration.
- **The course name is the file name.** `Save` writes `<Name>.json` straight from the typed name without sanitising it, so a name with a path separator or an invalid character fails (logged) or lands somewhere unexpected; sanitise as purrTTY's theme catalog does in [Load bundled and user color themes from TOML files and let the user's theme win a name clash](./persist-user-created-presets-and-content-2.md).
- **A load never overwrites on its own.** There is no write on load, so opening a course in a world without the body leaves the file untouched.
- **Anchor to the ground, not a radius.** Terrain changes between game builds; the 5 cm drift warning makes the change visible instead of silently burying or floating the layout.
- **Mirrored meshes need a twin.** Mirroring a mesh inverts its winding, so a chiral piece needs a separately modelled mirror; the file stores `mirrored` and the loader maps it back.
- **Provisional sizes.** `CourseSize` constants are marked provisional; a size change must bump `Version` or the cell size stored in the file (`cell_m`) is the only protection.
- **File location depends on the log.** The courses folder derives from where the log went, so a system where the KSA folder could not be found stores courses under the temp directory.

## See also

- Sibling approaches: [KxAsset profile type with XmlSerializer export](./persist-user-created-presets-and-content-4.md), [SharedFileLibrary with import caps](./persist-user-created-presets-and-content-6.md), [overview](./overview.md)
- [Course model, grid and save file](../../../../mods/ksagolf/features/course-grid-and-save-file.md)
- [Place static objects on a planet surface](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md), [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../../saves/save-mod-state-with-the-scene/overview.md)
- [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md) ([overview](../choose-where-a-mod-stores-its-files/overview.md)), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
