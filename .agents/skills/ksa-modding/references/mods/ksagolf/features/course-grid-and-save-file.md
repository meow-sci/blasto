---
type: Mod Feature
title: Course grid model and JSON save file
description: An engine-free minigolf course model with ports as cell edges, an append-only piece catalogue with non-rectangular footprints and mirror twins, a playability check, port snapping, undo and redo, and a versioned JSON file anchored by latitude, longitude, heading and height above ground
mod: ksagolf
category: persistence
techniques: [json-config, immutable-model, headless-tests]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.Universe, KSA.CelestialSystem]
tags: [minigolf, grid, ports, catalogue, undo, redo, json, save-file, versioning, immutable]
---

A minigolf hole in KSAGolf is a small immutable data model with no KSA types: pieces on a square
grid, joined port to port from one tee to one cup. It is shared by the builder, the ball's exact
surface, the kitten's colliders, the line overlay and the save file, and it runs in the headless
test project. The save file is a standalone, versioned JSON document per course, placed on a body
by latitude, longitude, heading and a floor height above the ground rather than by an absolute
radius.[^golf-course][^golf-coursefile]

## How it works

### The grid

| Type | Meaning |
|---|---|
| `Cell(I, J)` | One grid square: I counts east, J counts north; `CourseSize.CellM` is 1 m |
| `Side` | North (+J), East (+I), South, West |
| `Port(Cell, Side, Level)` | **The cell edge a lane crosses**, and the floor level it crosses at; `Outside` is the cell a connecting piece's port must sit in, and `Meets` requires that cell, the opposite side and the same level |
| `Orientation(Turns, Mirrored)` | Mirrored east-west first, then turned anticlockwise in quarters; footprints swap width and height on odd turns; `Apply` and `Undo` map cells and sides |

A port is an edge rather than a tile because a tile does not say which way a lane leaves: a corner
cell has two outward sides, a 1x1 piece has four, and a 1x1 straight is entered and left through
the same tile.[^golf-coursegrid]

### The catalogue

`PieceDef(Id, Name, Kind, W, H, Ports, Mesh, MirrorMesh, Tiles)` describes a piece laid down
unturned. A tee and a cup have one port, a lane two and is played from either end. `Tiles` lists the
cells a non-rectangular piece takes up, so a wide bend is an L that leaves its cut corner free for
another piece. `Fault()` checks a definition (ports inside the footprint and facing out, the right
port count). **Ids are append-only**: a saved course names them.[^golf-catalogue]

| Id | Footprint | Notes |
|---|---|---|
| `tee`, `cup` | 1x1 | One port each |
| `straight`, `straight3` | 1x1, 1x3 | |
| `corner` | 1x1 | South to east |
| `bend2` | 2x2, 3 tiles | Wide bend leaving a corner free |
| `sbend` | 2x3, 4 tiles | Chiral, so it has a separately modelled mirror mesh |
| `ramp` | 1x2 | Exit port one level up |
| `room2`, `room4` | 2x2, 4x4 | Rooms; `room4` has bumper posts |

Two orientation helpers keep the builder honest. `DistinctOrientations()` keeps only the orientations
that lay a piece down differently (a straight has two, an S-bend four), so R never seems to do
nothing. `AsTurn(o)` finds the unmirrored orientation equivalent to a mirrored one, because a mesh
is drawn turned but never mirrored (mirroring turns its triangles inside out); a chiral piece with no
such turn uses its `MirrorMesh` twin.[^golf-catalogue]

### The course

`Course` is an immutable list of `Placed(Def, Origin, Orientation, Level)`; `With`, `Without` and
`Replacing` return new courses, which is what makes undo trivial. `CourseEdits` keeps two stacks of
whole courses: `Apply` pushes the current one and clears redo, `Undo` and `Redo` swap.[^golf-courseedits]

| Method | Rule |
|---|---|
| `Judge(ghost, ignore)` | `Overlaps` if it covers a piece or sinks below level 0; `Blocked` if one of its ports faces a piece with no matching port, or a course port runs into it without a match; `Connects` if a port meets one; else `Open`. `ignore` is the piece being moved |
| `Check()` | Problems with piece indices: below the floor, overlaps, open ports, a level mismatch (reported once per seam), a lane into a piece's side, tee and cup counts. Then walks from the tee: each lane has two ports, so the tee's component is a simple path; it must end at the cup, and lanes off it are reported |
| `OpenPorts()` | Ports with nothing outside them |
| `Snap(def, preferred, cursor, reach = 2)` | The open port whose outside cell is nearest the cursor (Chebyshev distance; ties to the lower piece, then N, E, S, W), tried in the preferred orientation and then each distinct one, with one of the piece's own ports placed facing it; the first that `Connects` wins |

`CourseLines.Of(course)` turns a course into line segments for the overlay: tile outlines (red for
pieces the check faults), inner cell edges, lane-wide openings at each port, the lane's centre
line, the cup's rim and the tee's cross, cut into pieces of at most 0.25 m lifted onto the floor so a
line follows a ramp. `SampleCourse` builds a demonstration hole by snapping every kind of piece onto
the last open port.[^golf-courselines]

### The save file

`CourseFile` is a record serialised with `System.Text.Json`, snake-case names and indentation:
`version` (1), `name`, `par`, `body`, `latitude_deg`, `longitude_deg`, `heading_deg` (the grid's +J,
clockwise from north), `floor_above_ground_m`, `cell_m`, and `pieces` as `{def, i, j, turns,
mirrored, level}`. `ToCourse` leaves out ids this build does not know and names them; `FromJson`
refuses a file from a newer version with the reason, rather than half-reading it.[^golf-coursefile]

`CourseBuilder.Save` writes `<Name>.json` (default names "Course 1", "Course 2"...) into
`CourseBuilder.Folder`, which is `KSAGolf/Courses` beside the folder the mod's log lives in. The log
goes to KSA's own `Logs` directory when it can be found (on Windows
`Documents/My Games/Kitten Space Agency/Logs`), so courses land in
`Documents/My Games/Kitten Space Agency/KSAGolf/Courses`; if no KSA folder exists the log falls back
to the temp directory and the courses folder is derived from that instead.[^golf-coursebuilder][^golf-log]

The anchor is written as latitude `asin(up.z)` and longitude `atan2(up.y, up.x)` of the body-fixed up
vector, the same convention as `Celestial.GetDirCcfFromLatLon`, plus the heading of +J against local
north and east.[^ksa-celestial] `Load` finds the body by id, rebuilds up and +J, re-levels the floor
against today's ground, and warns if the floor moved more than 5 cm from the saved height above
ground. Keeping a height above the ground instead of a radius means a KSA update that reshapes the
terrain moves the course with it instead of burying it.[^golf-coursefile][^golf-coursebuilder]

## Lifecycle and timing

Everything here is plain data on the main thread. Edits happen in the builder's input handling and
panel buttons, both inside `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`, `frame:draw-ui`);
saving and loading are synchronous file IO from the panel's buttons in the same hook. The file is
independent of KSA's own save games: nothing loads a course when a save loads, and a save load makes
the builder `Forget()` the course in memory (see [Save-load detection](save-load-detection.md)).
Because a `Course` is immutable, the snapshot handed to the ball and to the physics after an edit
can be read later, from any thread, without copying.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Sim/CourseGrid.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseGrid.cs) | `Cell`, `Side`, `Port`, `Orientation` |
| [src/KSAGolf/Sim/PieceCatalogue.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/PieceCatalogue.cs) | `PieceDef` (tiles, faults, distinct orientations, `AsTurn`) and the catalogue |
| [src/KSAGolf/Sim/Course.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Course.cs) | `Placed` (cells, ports, piece/course transforms, turning under the cursor), `Fit`, `Course` (judge, check, open ports, snap) |
| [src/KSAGolf/Sim/CourseEdits.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseEdits.cs) | Undo and redo over immutable courses |
| [src/KSAGolf/Sim/CourseFile.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseFile.cs) | `CourseSize` and the versioned JSON record |
| [src/KSAGolf/Sim/CourseLines.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseLines.cs) | Overlay lines, floor height, the sample hole |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L440-L527) | Folder, save (lat, lon, heading, floor height), load and drift warning |
| [tests/KSAGolf.Tests/CourseTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseTests.cs) | Catalogue well-formed, chains and loops, blocking, snapping, ramps, undo, save round trip, unknown and newer files |
| [tests/KSAGolf.Tests/CourseLinesTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseLinesTests.cs) | Sample hole playable, ramp rise, openings on ports, mirror twins, every model declared |

## Snippets

```csharp
// src/KSAGolf/Sim/CourseGrid.cs L28-L42
/// <summary>
/// Where a lane leaves a piece: the cell edge it crosses, and the floor level it crosses at.
///
/// <para>An edge rather than a tile, because a tile does not say which way the lane leaves: a corner
/// cell has two outward sides and a 1×1 piece has four, and a 1×1 straight is entered and left through
/// the same tile.</para>
/// </summary>
internal readonly record struct Port(Cell Cell, Side Side, int Level)
{
    /// <summary>The cell a connecting piece's port has to sit in.</summary>
    public Cell Outside => Cell.Toward(Side);

    /// <summary>Whether a lane runs straight from this port into that one.</summary>
    public bool Meets(Port other) => other.Cell == Outside && other.Side == Sides.Opposite(Side) && other.Level == Level;
}
```

```csharp
// src/KSAGolf/Sim/PieceCatalogue.cs L101-L120
/// <summary>The pieces a course is built from. Ids are append-only: a saved course names them.</summary>
internal static class PieceCatalogue
{
    private static Port P(int i, int j, Side side, int level = 0) => new(new Cell(i, j), side, level);

    public static readonly PieceDef[] All =
    [
        new("tee", "Tee", PieceKind.Tee, 1, 1, [P(0, 0, Side.North)], "KSAGolf_Piece_Tee_Glb"),
        new("cup", "Cup", PieceKind.Cup, 1, 1, [P(0, 0, Side.South)], "KSAGolf_Piece_Cup_Glb"),
        new("straight", "Straight", PieceKind.Lane, 1, 1, [P(0, 0, Side.South), P(0, 0, Side.North)], "KSAGolf_Piece_Straight_Glb"),
        new("straight3", "Long straight", PieceKind.Lane, 1, 3, [P(0, 0, Side.South), P(0, 2, Side.North)], "KSAGolf_Piece_Straight3_Glb"),
        new("corner", "Corner", PieceKind.Lane, 1, 1, [P(0, 0, Side.South), P(0, 0, Side.East)], "KSAGolf_Piece_Corner_Glb"),
        new("bend2", "Wide bend", PieceKind.Lane, 2, 2, [P(0, 0, Side.South), P(1, 1, Side.East)], "KSAGolf_Piece_Bend2_Glb", null,
            [new(0, 0), new(0, 1), new(1, 1)]),
        new("sbend", "S-bend", PieceKind.Lane, 2, 3, [P(0, 0, Side.South), P(1, 2, Side.North)], "KSAGolf_Piece_SBend_Glb", "KSAGolf_Piece_SBendMirror_Glb",
            [new(0, 0), new(0, 1), new(1, 1), new(1, 2)]),
        new("ramp", "Ramp", PieceKind.Lane, 1, 2, [P(0, 0, Side.South), P(0, 1, Side.North, 1)], "KSAGolf_Piece_Ramp_Glb"),
        new("room2", "Small room", PieceKind.Lane, 2, 2, [P(0, 0, Side.South), P(1, 1, Side.North)], "KSAGolf_Piece_Room2_Glb"),
        new("room4", "Bumper room", PieceKind.Lane, 4, 4, [P(1, 0, Side.South), P(2, 3, Side.North)], "KSAGolf_Piece_Room4_Glb"),
    ];
```

```csharp
// src/KSAGolf/Sim/Course.cs L256-L283
    public Placed? Snap(PieceDef def, Orientation preferred, Cell cursor, int reach = 2, int ignore = -1)
    {
        var open = OpenPorts()
            .Where(o => o.Piece != ignore)
            .Select(o => (o.Piece, o.Port, Distance: Math.Max(Math.Abs(o.Port.Outside.I - cursor.I), Math.Abs(o.Port.Outside.J - cursor.J))))
            .Where(o => o.Distance <= reach)
            .OrderBy(o => o.Distance).ThenBy(o => o.Piece).ThenBy(o => (int)o.Port.Side)
            .ToList();
        if (open.Count == 0) return null;

        Orientation[] turns = def.DistinctOrientations();
        int start = Math.Max(0, Array.IndexOf(turns, preferred));
        Port target = open[0].Port;

        for (int k = 0; k < turns.Length; k++)
        {
            Orientation o = turns[(start + k) % turns.Length];
            foreach (Port own in def.Ports)
            {
                if (o.Apply(own.Side) != Sides.Opposite(target.Side)) continue;

                var ghost = new Placed(def, target.Outside - o.Apply(own.Cell, def.W, def.H), o, target.Level - own.Level);
                if (Judge(ghost, ignore) == Fit.Connects) return ghost;
            }
        }

        return null;
    }
```

```csharp
// src/KSAGolf/Sim/CourseFile.cs L29-L60
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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Celestial.GetDirCcfFromLatLon` (convention only) | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L667-L675) | The engine's body-fixed latitude and longitude convention, which the mod's own conversion matches |
| `Universe.CurrentSystem` / `CelestialSystem.GetIndex` (via `KsaWorld.FindCelestial`) | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L68-L74) | Finding the saved body by id on load |

## Pitfalls and version notes

- **Never rename or remove a piece id.** A saved course names its pieces; an unknown id is dropped
  with a warning rather than crashing, but the hole is broken. The same rule as KSA's own part ids,
  which the repo also treats as append-only.[^golf-catalogue][^golf-claude]
- **Refuse newer files.** A version number and a refusal with a reason are cheaper than a silent
  half-read of a format you do not understand.[^golf-coursefile]
- **Anchor to the ground, not to a radius.** Terrain changes between game builds; a stored radius
  would bury or float the course after an update. The load warns when the floor moves by more than
  5 cm.
- **Mirroring needs a twin mesh.** Mirroring a mesh inverts its winding, so a chiral piece needs a
  separately modelled mirror (`MirrorMesh`), and `AsTurn` decides when a turn suffices.
- **Provisional sizes.** `CourseSize` (1 m cells, 0.8 m lanes, 0.30 m per level so a 2 m ramp is
  steeper than the ball's 0.08 rest slope) is marked provisional until judged in game.[^golf-coursefile]
- **Folder depends on the log.** The courses folder is derived from where the log went; on a system
  where the KSA folder could not be found it is derived from the temp directory instead.[^golf-log]
- **Version.** No engine API beyond body lookup; unaffected by the 2026.9.22.5482 to 2026.10.10.5554
  changes.

## See also

- [Top-down minigolf course builder](minigolf-course-builder.md)
- [Course surface from exact shapes](course-surface-exact-shapes.md)
- [Course colliders registered as ground clutter](course-colliders-as-clutter.md)
- [Prop and course asset XML](prop-and-course-asset-xml.md)
- [Mod log file with rotation and a verbose toggle](mod-log-file-with-rotation.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Course frame anchored at the kitten and saved as latitude, longitude, heading and floor height](../../../howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-3.md) - how-to article demonstrating this feature
- [Derive the data folder from the mod's log folder, with a temp-directory fallback](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-4.md) - how-to article demonstrating this feature
- [Save a user-built layout as a versioned snake_case JSON file anchored to a body by latitude and longitude](../../../howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-5.md) - how-to article demonstrating this feature

[^golf-coursegrid]: src/KSAGolf/Sim/CourseGrid.cs
[^golf-catalogue]: src/KSAGolf/Sim/PieceCatalogue.cs
[^golf-course]: src/KSAGolf/Sim/Course.cs
[^golf-courseedits]: src/KSAGolf/Sim/CourseEdits.cs
[^golf-coursefile]: src/KSAGolf/Sim/CourseFile.cs
[^golf-courselines]: src/KSAGolf/Sim/CourseLines.cs
[^golf-coursebuilder]: src/KSAGolf/Ksa/CourseBuilder.cs
[^golf-log]: src/KSAGolf/Ksa/Log.cs
[^golf-claude]: CLAUDE.md (layout, parts)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
