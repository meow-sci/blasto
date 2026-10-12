using Blasto.Core;
using Blasto.Persistence;
using Blasto.Rendering;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blasto.Tests;

internal static class Program
{
    private static readonly (string Name, Action Run)[] Checks =
    [
        ("tangent axes remain orthonormal near both poles", TangentsNearPoles),
        ("exposure is invariant to time-step subdivision", ExposureSubdivision),
        ("zero, invalid, and non-finite deposits are rejected", InvalidDeposits),
        ("malformed surface hits are rejected without changing stored marks", MalformedSurfaceHitsAreAtomic),
        ("merge keeps anchor, orientation, and deterministic seed", MergeKeepsStableIdentity),
        ("wide and narrow impacts at the same point remain separate sites", DifferentScaleSitesStaySeparate),
        ("exposure saturates at the configured bound", ExposureSaturates),
        ("capacity rejects new sites without evicting old sites", CapacityPreservesExistingSites),
        ("diagnostic marks are visible across surface profiles and probe scales", DiagnosticPlacementIgnoresSurfaceDose),
        ("diagnostic marks merge and preserve bounded history", DiagnosticMergeAndCapacity),
        ("malformed diagnostic placement is atomic", DiagnosticPlacementRejectsMalformedHit),
        ("unsigned Vulkan limits accept high-bit values", GpuLimitsPreserveUnsignedValues),
        ("ordinary GPU limits above requirements pass", GpuLimitsAcceptOrdinaryValues),
        ("GPU capability requirements accept exact boundaries", GpuLimitsAcceptExactRequirements),
        ("genuinely insufficient GPU limits report actual and required values", GpuLimitsRejectInsufficientValues),
        ("replace rejects duplicate and malformed records atomically", ReplaceIsAtomic),
        ("nearby impacts on different bodies stay separate", BodyIdentitySeparatesSites),
        ("ray intersection handles analytic spheres, range, direction, and terrain height", RayIntersectionCases),
        ("grazing ray intersection continues beyond the old march limit", GrazingRayIntersectionContinuesToRange),
        ("non-finite terrain height during root refinement is rejected", NonFiniteHeightDuringBisectionIsRejected),
        ("sidecar round-trips and repeated loads replace without duplicates", SaveRoundTripAndRepeatedLoads),
        ("missing sidecar is treated as an empty optional save", MissingSidecarIsEmpty),
        ("sidecar rejects a native-save hash mismatch", SaveRejectsHashMismatch),
        ("sidecar rejects unsupported versions", SaveRejectsUnsupportedVersions),
        ("sidecar rejects duplicate properties, duplicate marks, and invalid marks", SaveRejectsMalformedDocuments),
        ("sidecar requires schema fields", SaveRequiresSchemaFields),
        ("shader transform refuses missing or duplicated integration anchors", TransformRejectsAnchorDrift),
    ];

    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args.Length == 3 && args[0] == "--export-shaders")
                return ExportShaders(args[1], args[2]);

            Console.Error.WriteLine("Usage: Blasto.Tests [--export-shaders <shader-root> <output-directory>]");
            return 2;
        }

        int failures = 0;
        foreach (var (name, run) in Checks)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{Checks.Length - failures}/{Checks.Length} checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static int ExportShaders(string sourceRoot, string outputDirectory)
    {
        string planetSource = Path.Combine(Path.GetFullPath(sourceRoot), "Planet");
        string planetOutput = Path.Combine(Path.GetFullPath(outputDirectory), "Planet");
        Directory.CreateDirectory(planetOutput);

        (string File, string Id)[] shaders =
        [
            ("Planet.frag", "PlanetFrag"),
            ("Planet.vert", "PlanetVert"),
            ("PlanetTessEvaluation.tese", "PlanetTessEvaluation"),
        ];

        foreach (var (file, id) in shaders)
        {
            string sourcePath = Path.Combine(planetSource, file);
            string source = File.ReadAllText(sourcePath);
            string transformed = TerrainShaderSource.Transform(source, id);
            File.WriteAllText(Path.Combine(planetOutput, file), transformed, new UTF8Encoding(false));
        }

        return 0;
    }

    private static void TangentsNearPoles()
    {
        foreach (var normal in new[]
        {
            new Double3(1e-10, 0, 1),
            new Double3(1e-10, 0, -1),
            new Double3(.1, -.2, .97),
            new Double3(-.3, .15, -.94),
        })
        {
            var (u, v) = Double3.Tangents(normal);
            var unitNormal = normal.Normalized();
            Near(1, u.Length, 1e-12, "u unit length");
            Near(1, v.Length, 1e-12, "v unit length");
            Near(0, Double3.Dot(unitNormal, u), 1e-12, "u perpendicular to normal");
            Near(0, Double3.Dot(unitNormal, v), 1e-12, "v perpendicular to normal");
            Near(0, Double3.Dot(u, v), 1e-12, "axes perpendicular");
            if (Double3.Dot(Double3.Cross(u, v), unitNormal) < .999999)
                throw new InvalidOperationException("tangent handedness does not follow the surface normal");
        }
    }

    private static void ExposureSubdivision()
    {
        var singleStep = new BlastSiteStore();
        var splitSteps = new BlastSiteStore();
        var hit = Hit();

        singleStep.Deposit(hit, thrust: 10_000, dt: 1, epoch: 4);
        splitSteps.Deposit(hit, thrust: 10_000, dt: .5, epoch: 4);
        splitSteps.Deposit(hit, thrust: 10_000, dt: .5, epoch: 4);

        Equal(1, singleStep.Count, "single-step site count");
        Equal(1, splitSteps.Count, "split-step site count");
        Near(singleStep.Snapshot()[0].Exposure, splitSteps.Snapshot()[0].Exposure, 1e-12,
            "integrated exposure");
    }

    private static void InvalidDeposits()
    {
        var store = new BlastSiteStore();
        var hit = Hit();

        IsNull(store.Deposit(hit, 0, 1, 0), "zero thrust");
        IsNull(store.Deposit(hit, -1, 1, 0), "negative thrust");
        IsNull(store.Deposit(hit, double.NaN, 1, 0), "NaN thrust");
        IsNull(store.Deposit(hit, 10_000, double.NaN, 0), "NaN time step");
        IsNull(store.Deposit(hit, 10_000, 1.01, 0), "oversized time step");
        IsNull(store.Deposit(hit, 10_000, 1, double.PositiveInfinity), "infinite epoch");
        IsNull(store.Deposit(hit with { CenterCcf = new Double3(double.NaN, 0, 0) }, 10_000, 1, 0),
            "non-finite center");
        IsNull(store.Deposit(hit with { Incidence = .05 }, 10_000, 1, 0), "grazing incidence");
        Equal(0, store.Count, "no site should be created from invalid exposure");
    }

    private static void MalformedSurfaceHitsAreAtomic()
    {
        var valid = Hit(center: new Double3(3_000, 0, 0));
        (string Name, SurfaceHit Hit)[] malformed =
        [
            ("NaN incidence", valid with { Incidence = double.NaN }),
            ("NaN anisotropy", valid with { Anisotropy = double.NaN }),
            ("empty body id", valid with { BodyId = "" }),
            ("zero normal", valid with { NormalCcf = default }),
            ("parallel tangent axes", valid with { TangentV = valid.TangentU }),
        ];

        foreach (var (name, hit) in malformed)
        {
            var store = new BlastSiteStore();
            store.Replace([ValidMark() with { CenterCcf = valid.CenterCcf }]);
            var before = store.Snapshot();
            long version = store.Version;

            IsNull(store.Deposit(hit, thrust: 10_000, dt: .5, epoch: 13), name);
            Equal(1, store.Count, $"{name} count");
            Equal(version, store.Version, $"{name} version");
            Equal(before[0], store.Snapshot()[0], $"{name} leaves the old mark unchanged");
        }
    }

    private static void MergeKeepsStableIdentity()
    {
        var firstStore = new BlastSiteStore();
        var secondStore = new BlastSiteStore();
        var originalHit = Hit(center: new Double3(2_000, 0, 0));
        var nearbyHit = originalHit with { CenterCcf = new Double3(2_002, 0, 0) };

        var first = firstStore.Deposit(originalHit, 10_000, .5, 10)
            ?? throw new InvalidOperationException("initial impact did not create a site");
        var updated = firstStore.Deposit(nearbyHit, 10_000, .5, 11)
            ?? throw new InvalidOperationException("nearby impact did not merge");
        var independentlyCreated = secondStore.Deposit(originalHit, 10_000, .5, 10)
            ?? throw new InvalidOperationException("independent impact did not create a site");

        Equal(first.Id, updated.Id, "merged site id");
        Equal(first.CenterCcf, updated.CenterCcf, "merged anchor");
        Equal(first.TangentU, updated.TangentU, "merged tangent U");
        Equal(first.TangentV, updated.TangentV, "merged tangent V");
        Equal(first.Seed, updated.Seed, "merged seed");
        Equal(first.Seed, independentlyCreated.Seed, "seed reproduced from the same body-fixed site");
        Equal(2_000d, updated.CenterCcf.X, "original anchor remains stationary");
        if (updated.Exposure <= first.Exposure)
            throw new InvalidOperationException("merged impact did not accumulate exposure");
    }

    private static void DifferentScaleSitesStaySeparate()
    {
        var store = new BlastSiteStore();
        var center = new Double3(4_000, 0, 0);
        var wide = store.Deposit(Hit(center: center, radius: 30), thrust: 50_000, dt: .25, epoch: 20)
            ?? throw new InvalidOperationException("wide impact was not accepted");
        if (wide.Exposure >= .001)
            throw new InvalidOperationException($"wide impact should have weak exposure, got {wide.Exposure:R}");

        var narrow = store.Deposit(Hit(center: center, radius: 10), thrust: 50_000, dt: .25, epoch: 21)
            ?? throw new InvalidOperationException("narrow impact was not accepted");

        Equal(2, store.Count, "different-scale site count");
        if (wide.Id == narrow.Id)
            throw new InvalidOperationException("materially different footprint scales were merged");
        True(store.Snapshot().Any(mark => mark.Id == wide.Id && mark.Radius == 30), "wide site retained");
        True(store.Snapshot().Any(mark => mark.Id == narrow.Id && mark.Radius == 10), "narrow site created");
    }

    private static void ExposureSaturates()
    {
        var store = new BlastSiteStore();
        var hit = Hit(radius: .25);
        var first = store.Deposit(hit, thrust: 1e9, dt: 1, epoch: 0)
            ?? throw new InvalidOperationException("high-exposure impact did not create a site");
        Near(100, first.Exposure, 0, "first deposit cap");

        var saturated = store.Deposit(hit, thrust: 1e9, dt: 1, epoch: 1)
            ?? throw new InvalidOperationException("saturating impact did not merge");
        Near(100, saturated.Exposure, 0, "merged exposure cap");
        Near(1 - Math.Exp(-100), saturated.Strength, 1e-15, "saturated visual strength");
    }

    private static void CapacityPreservesExistingSites()
    {
        var store = new BlastSiteStore(capacity: 1);
        var first = store.Deposit(Hit(center: new Double3(1_000, 0, 0)), 10_000, 1, 1)
            ?? throw new InvalidOperationException("first site was not accepted");

        var nearby = store.Deposit(Hit(center: new Double3(1_002, 0, 0)), 10_000, 1, 2)
            ?? throw new InvalidOperationException("existing site should still accept exposure at capacity");
        Equal(first.Id, nearby.Id, "at-capacity merge id");

        IsNull(store.Deposit(Hit(center: new Double3(2_000, 0, 0)), 10_000, 1, 3), "new site at capacity");
        Equal(1, store.Count, "capacity count");
        Equal(first.Id, store.Snapshot()[0].Id, "old site retained");
        Equal(1L, store.CapacityRejected, "capacity rejection count");
    }

    private static void DiagnosticPlacementIgnoresSurfaceDose()
    {
        SurfaceKind[] profiles = Enum.GetValues<SurfaceKind>();
        double[] probeRadii = [.25, 1, 30, 120];
        for (int i = 0; i < profiles.Length; i++)
        {
            var hit = Hit(body: "Body" + i, center: new Double3(1_000 + 100 * i, 0, 0),
                radius: probeRadii[i], profile: profiles[i]) with
            {
                Distance = 350,
                Incidence = i % 2 == 0 ? .051 : 1,
            };
            var store = new BlastSiteStore();
            var mark = store.DepositDiagnostic(hit, epoch: 20 + i)
                ?? throw new InvalidOperationException($"diagnostic placement failed for {profiles[i]}");

            Near(BlastSiteStore.DiagnosticRadius, mark.Radius, 0, $"{profiles[i]} diagnostic radius");
            Near(BlastSiteStore.DiagnosticExposure, mark.Exposure, 0, $"{profiles[i]} diagnostic exposure");
            Near(1 - Math.Exp(-BlastSiteStore.DiagnosticExposure), mark.Strength, 1e-15,
                $"{profiles[i]} diagnostic strength");
            Equal(profiles[i], mark.Profile, $"{profiles[i]} profile retained");
            True(mark.IsValid(), $"{profiles[i]} diagnostic mark validity");
        }

        var physicalStore = new BlastSiteStore();
        var lowDoseRock = physicalStore.Deposit(Hit(radius: 40, profile: SurfaceKind.Rock),
            thrust: 300_000, dt: .3, epoch: 30)
            ?? throw new InvalidOperationException("low-dose physical reference mark was rejected");
        if (lowDoseRock.Strength >= .001)
            throw new InvalidOperationException($"physical reference should be faint, got strength {lowDoseRock.Strength:R}");

        var diagnostic = new BlastSiteStore().DepositDiagnostic(
            Hit(radius: 40, profile: SurfaceKind.Rock), epoch: 30)
            ?? throw new InvalidOperationException("diagnostic rock mark was rejected");
        if (diagnostic.Strength < .9)
            throw new InvalidOperationException($"diagnostic reference should be clearly visible, got strength {diagnostic.Strength:R}");
    }

    private static void DiagnosticMergeAndCapacity()
    {
        var store = new BlastSiteStore(capacity: 1);
        var original = store.DepositDiagnostic(Hit(center: new Double3(2_000, 0, 0)), epoch: 5)
            ?? throw new InvalidOperationException("initial diagnostic mark was not accepted");
        var merged = store.DepositDiagnostic(Hit(center: original.CenterCcf), epoch: 6)
            ?? throw new InvalidOperationException("repeated diagnostic mark did not merge");

        Equal(original.Id, merged.Id, "diagnostic merge id");
        Equal(original.CenterCcf, merged.CenterCcf, "diagnostic merge anchor");
        Equal(original.Seed, merged.Seed, "diagnostic merge seed");
        Near(2 * BlastSiteStore.DiagnosticExposure, merged.Exposure, 0, "merged diagnostic exposure");
        Near(BlastSiteStore.DiagnosticRadius, merged.Radius, 0, "merged diagnostic radius");
        Equal(6d, merged.UpdatedSeconds, "diagnostic merge timestamp");

        var beforeRejection = store.Snapshot();
        IsNull(store.DepositDiagnostic(Hit(center: new Double3(2_500, 0, 0)), epoch: 7),
            "new diagnostic site at capacity");
        Equal(1, store.Count, "diagnostic capacity count");
        Equal(1L, store.CapacityRejected, "diagnostic capacity rejection count");
        Equal(beforeRejection[0], store.Snapshot()[0], "diagnostic capacity rejection preserves old mark");
    }

    private static void DiagnosticPlacementRejectsMalformedHit()
    {
        var valid = Hit(center: new Double3(3_000, 0, 0));
        (string Name, SurfaceHit Hit)[] malformed =
        [
            ("NaN incidence", valid with { Incidence = double.NaN }),
            ("NaN anisotropy", valid with { Anisotropy = double.NaN }),
            ("empty body id", valid with { BodyId = "" }),
            ("zero normal", valid with { NormalCcf = default }),
            ("parallel tangent axes", valid with { TangentV = valid.TangentU }),
            ("invalid input radius", valid with { Radius = .1 }),
        ];

        foreach (var (name, hit) in malformed)
        {
            var store = new BlastSiteStore();
            store.DepositDiagnostic(valid, epoch: 13);
            var before = store.Snapshot();
            long version = store.Version;

            IsNull(store.DepositDiagnostic(hit, epoch: 14), name);
            Equal(1, store.Count, $"{name} count");
            Equal(version, store.Version, $"{name} version");
            Equal(before[0], store.Snapshot()[0], $"{name} leaves the old mark unchanged");
        }

        var epochStore = new BlastSiteStore();
        var existing = epochStore.DepositDiagnostic(valid, epoch: 13)
            ?? throw new InvalidOperationException("valid diagnostic seed was rejected");
        long epochVersion = epochStore.Version;
        IsNull(epochStore.DepositDiagnostic(valid, epoch: double.NaN), "NaN epoch");
        Equal(epochVersion, epochStore.Version, "invalid diagnostic epoch version");
        Equal(existing, epochStore.Snapshot()[0], "invalid diagnostic epoch leaves the mark unchanged");
    }

    private static void GpuLimitsPreserveUnsignedValues()
    {
        var maximum = GpuCapabilities(maxStorageBufferRangeRaw: -1);
        Equal(uint.MaxValue, maximum.MaxStorageBufferRange, "-1 bit pattern becomes uint.MaxValue");
        maximum.EnsureSupported();

        var highBits = new TerrainGpuCapabilities(
            MaxBoundDescriptorSetsRaw: int.MinValue,
            MaxPushConstantsSizeRaw: int.MinValue,
            MaxDescriptorSetStorageBuffersRaw: int.MinValue,
            MaxPerStageDescriptorStorageBuffersRaw: int.MinValue,
            MaxStorageBufferRangeRaw: int.MinValue,
            RequiredStorageBufferRange: 5_136);
        Equal(0x80000000u, highBits.MaxBoundDescriptorSets, "descriptor-set limit keeps its high bit");
        Equal(0x80000000u, highBits.MaxPushConstantsSize, "push-constant limit keeps its high bit");
        Equal(0x80000000u, highBits.MaxDescriptorSetStorageBuffers, "descriptor storage-buffer limit keeps its high bit");
        Equal(0x80000000u, highBits.MaxPerStageDescriptorStorageBuffers, "per-stage storage-buffer limit keeps its high bit");
        Equal(0x80000000u, highBits.MaxStorageBufferRange, "storage-buffer-range limit keeps its high bit");
        highBits.EnsureSupported();
    }

    private static void GpuLimitsAcceptOrdinaryValues()
    {
        var ordinary = new TerrainGpuCapabilities(
            MaxBoundDescriptorSetsRaw: 8,
            MaxPushConstantsSizeRaw: 256,
            MaxDescriptorSetStorageBuffersRaw: 4,
            MaxPerStageDescriptorStorageBuffersRaw: 8,
            MaxStorageBufferRangeRaw: 16_384,
            RequiredStorageBufferRange: 5_136);

        ordinary.EnsureSupported();
    }

    private static void GpuLimitsAcceptExactRequirements()
    {
        var atBoundary = new TerrainGpuCapabilities(
            MaxBoundDescriptorSetsRaw: 6,
            MaxPushConstantsSizeRaw: 128,
            MaxDescriptorSetStorageBuffersRaw: 3,
            MaxPerStageDescriptorStorageBuffersRaw: 3,
            MaxStorageBufferRangeRaw: 5_136,
            RequiredStorageBufferRange: 5_136);

        atBoundary.EnsureSupported();
    }

    private static void GpuLimitsRejectInsufficientValues()
    {
        (string Name, TerrainGpuCapabilities Capabilities, string Actual, string Required)[] insufficient =
        [
            ("MaxBoundDescriptorSets", GpuCapabilities(maxBoundDescriptorSetsRaw: 5), "5", "6"),
            ("MaxPushConstantsSize", GpuCapabilities(maxPushConstantsSizeRaw: 127), "127", "128"),
            ("MaxDescriptorSetStorageBuffers", GpuCapabilities(maxDescriptorSetStorageBuffersRaw: 2), "2", "3"),
            ("MaxPerStageDescriptorStorageBuffers", GpuCapabilities(maxPerStageDescriptorStorageBuffersRaw: 2), "2", "3"),
            ("MaxStorageBufferRange", GpuCapabilities(maxStorageBufferRangeRaw: 5_135), "5135", "5136"),
        ];

        foreach (var (name, capabilities, actual, required) in insufficient)
        {
            InvalidOperationException exception = Throws<InvalidOperationException>(capabilities.EnsureSupported, name);
            True(exception.Message.Contains(name, StringComparison.Ordinal), $"{name} failure names the limit");
            True(exception.Message.Contains($"actual={actual},", StringComparison.Ordinal), $"{name} failure reports actual value {actual}");
            True(exception.Message.Contains($"required={required},", StringComparison.Ordinal), $"{name} failure reports required value {required}");
        }
    }

    private static TerrainGpuCapabilities GpuCapabilities(
        int maxBoundDescriptorSetsRaw = 6,
        int maxPushConstantsSizeRaw = 128,
        int maxDescriptorSetStorageBuffersRaw = 3,
        int maxPerStageDescriptorStorageBuffersRaw = 3,
        int maxStorageBufferRangeRaw = 5_136) => new(
            maxBoundDescriptorSetsRaw,
            maxPushConstantsSizeRaw,
            maxDescriptorSetStorageBuffersRaw,
            maxPerStageDescriptorStorageBuffersRaw,
            maxStorageBufferRangeRaw,
            RequiredStorageBufferRange: 5_136);

    private static void ReplaceIsAtomic()
    {
        var store = new BlastSiteStore();
        var original = store.Deposit(Hit(), 10_000, 1, 7)
            ?? throw new InvalidOperationException("initial site was not accepted");
        var duplicate = original with { CenterCcf = new Double3(2_000, 0, 0) };
        var malformed = original with { Radius = double.NaN };

        Throws<ArgumentException>(() => store.Replace([original, duplicate]), "duplicate ids");
        Equal(original, store.Snapshot()[0], "store after duplicate rejection");

        Throws<ArgumentException>(() => store.Replace([malformed]), "malformed mark");
        Equal(original, store.Snapshot()[0], "store after malformed record rejection");

        Throws<ArgumentException>(() => store.Replace([null!]), "null mark");
        Equal(original, store.Snapshot()[0], "store after null record rejection");
    }

    private static void BodyIdentitySeparatesSites()
    {
        var store = new BlastSiteStore();
        var center = new Double3(1_500, 0, 0);

        var lunar = store.Deposit(Hit(body: "Luna", center: center), 10_000, 1, 1)
            ?? throw new InvalidOperationException("lunar site was not accepted");
        var martian = store.Deposit(Hit(body: "Mars", center: center), 10_000, 1, 1)
            ?? throw new InvalidOperationException("martian site was not accepted");

        Equal(2, store.Count, "per-body site count");
        if (lunar.Id == martian.Id)
            throw new InvalidOperationException("sites on distinct bodies shared an id");
        Equal("Luna", lunar.BodyId, "lunar body id");
        Equal("Mars", martian.BodyId, "martian body id");
    }

    private static void RayIntersectionCases()
    {
        bool hit = SurfaceIntersection.TryRay(
            origin: new Double3(0, 0, 2), direction: new Double3(0, 0, -1), meanRadius: 1,
            height: _ => 0, maxDistance: 5, out var point, out var distance);

        True(hit, "ray should intersect");
        Near(1, distance, .002, "surface distance");
        Near(1, point.Z, .002, "surface point");

        double xSlope = .25;
        var obliqueDirection = new Double3(xSlope, 0, -1).Normalized();
        var origin = new Double3(0, 0, 2);
        double projection = Double3.Dot(origin, obliqueDirection);
        double analyticDistance = -projection - Math.Sqrt(projection * projection - (origin.LengthSquared - 1));
        bool analyticHit = SurfaceIntersection.TryRay(
            origin, obliqueDirection, meanRadius: 1, height: _ => 0, maxDistance: 5,
            out _, out var analyticDistanceFound);
        True(analyticHit, "oblique ray should intersect the sphere");
        Near(analyticDistance, analyticDistanceFound, .002, "analytic sphere distance");

        bool belowRangeHit = SurfaceIntersection.TryRay(
            origin: new Double3(0, 0, 2), direction: new Double3(0, 0, -1), meanRadius: 1,
            height: _ => 0, maxDistance: .99, out _, out _);
        True(!belowRangeHit, "ray should miss when the crossing is outside the range");

        bool upwardHit = SurfaceIntersection.TryRay(
            origin: new Double3(0, 0, 2), direction: new Double3(0, 0, 1), meanRadius: 1,
            height: _ => 0, maxDistance: 5, out _, out _);
        True(!upwardHit, "outward ray should not hit the sphere");

        double RoughHeight(Double3 normal) => .2 * Math.Clamp(normal.X, 0, 1) + .05 * Math.Sin(normal.X * 40);
        bool roughHit = SurfaceIntersection.TryRay(
            origin, obliqueDirection, meanRadius: 1, height: RoughHeight, maxDistance: 5,
            out var roughPoint, out _);
        True(roughHit, "ray should intersect the rough terrain height field");
        Near(1, roughPoint.Length - RoughHeight(roughPoint.Normalized()), .002,
            "rough terrain crossing clearance");
    }

    private static void GrazingRayIntersectionContinuesToRange()
    {
        var origin = new Double3(0, 0, 1_000_000.1);
        var direction = new Double3(.9999995, 0, -.001).Normalized();
        double projection = Double3.Dot(origin, direction);
        double analyticDistance = -projection - Math.Sqrt(
            projection * projection - (origin.LengthSquared - 1_000_000d * 1_000_000d));

        bool hit = SurfaceIntersection.TryRay(origin, direction, meanRadius: 1_000_000,
            height: _ => 0, maxDistance: 350, out var point, out var distance);
        True(hit, "grazing ray should intersect within the requested range");
        Near(analyticDistance, distance, .02, "grazing analytic sphere distance");
        Near(1_000_000, point.Length, .01, "grazing sphere surface radius");
    }

    private static void NonFiniteHeightDuringBisectionIsRejected()
    {
        int calls = 0;
        double Height(Double3 _) => ++calls switch
        {
            1 => 0,
            2 => 1,
            3 => double.NaN,
            _ => 0,
        };

        bool hit = SurfaceIntersection.TryRay(
            origin: new Double3(0, 0, 2), direction: new Double3(0, 0, -1), meanRadius: 1,
            height: Height, maxDistance: 5, out _, out _);
        True(!hit, "non-finite terrain height while refining the crossing must reject the ray");
        Equal(3, calls, "refinement should stop at the non-finite height sample");
    }

    private static void SaveRoundTripAndRepeatedLoads()
    {
        WithTempDirectory(directory =>
        {
            byte[] universe = Encoding.UTF8.GetBytes("<universe version='1'/>");
            File.WriteAllBytes(Path.Combine(directory, "universe.xml"), universe);
            var mark = ValidMark();
            var source = ValidDocument([mark]);

            BlastSaveCodec.Write(directory, source);
            var firstRead = BlastSaveCodec.Read(directory)
                ?? throw new InvalidOperationException("existing sidecar returned no document");
            Equal(Convert.ToHexString(SHA256.HashData(universe)), firstRead.UniverseSha256,
                "hash bound to the native universe file");
            Equal(source.Settings, firstRead.Settings, "round-trip settings");
            Equal(mark, firstRead.Marks.Single(), "round-trip mark");

            var store = new BlastSiteStore();
            store.Replace(firstRead.Marks);
            store.Replace(firstRead.Marks);
            Equal(1, store.Count, "repeated load count");
            Equal(mark.Id, store.Snapshot()[0].Id, "repeated load id");
        });
    }

    private static void MissingSidecarIsEmpty()
    {
        WithTempDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "universe.xml"), "<universe/>");
            IsNull(BlastSaveCodec.Read(directory), "missing optional sidecar");
        });
    }

    private static void SaveRejectsHashMismatch()
    {
        WithTempDirectory(directory =>
        {
            string universePath = Path.Combine(directory, "universe.xml");
            File.WriteAllText(universePath, "<universe id='first'/>");
            BlastSaveCodec.Write(directory, ValidDocument([ValidMark()]));
            File.WriteAllText(universePath, "<universe id='second'/>");
            Throws<InvalidDataException>(() => BlastSaveCodec.Read(directory), "universe hash mismatch");
        });
    }

    private static void SaveRejectsUnsupportedVersions()
    {
        var document = ValidDocument([ValidMark()]);
        Throws<InvalidDataException>(
            () => BlastSaveCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(document with { SchemaVersion = 2 })),
            "unsupported schema version");
        Throws<InvalidDataException>(
            () => BlastSaveCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(document with { PatternVersion = 2 })),
            "unsupported pattern version");
    }

    private static void SaveRejectsMalformedDocuments()
    {
        var mark = ValidMark();
        var document = ValidDocument([mark]);
        string json = JsonSerializer.Serialize(document);
        string duplicateProperty = json.Replace("\"SchemaVersion\":1,", "\"SchemaVersion\":1,\"SchemaVersion\":1,", StringComparison.Ordinal);
        if (duplicateProperty == json)
            throw new InvalidOperationException("test fixture did not contain the expected schema field");
        Throws<InvalidDataException>(() => BlastSaveCodec.Decode(Encoding.UTF8.GetBytes(duplicateProperty)),
            "duplicate JSON property");

        var duplicateMarks = document with { Marks = [mark, mark] };
        Throws<InvalidDataException>(
            () => BlastSaveCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(duplicateMarks)), "duplicate mark ids");

        var malformedMark = document with { Marks = [mark with { Radius = .1 }] };
        Throws<InvalidDataException>(
            () => BlastSaveCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(malformedMark)), "invalid mark geometry");
    }

    private static void SaveRequiresSchemaFields()
    {
        var document = ValidDocument([ValidMark()]);
        string json = JsonSerializer.Serialize(document);
        string missingRequiredField = json.Replace("\"PatternVersion\":1,", "", StringComparison.Ordinal);
        if (missingRequiredField == json)
            throw new InvalidOperationException("test fixture did not contain the expected pattern field");
        Throws<JsonException>(() => BlastSaveCodec.Decode(Encoding.UTF8.GetBytes(missingRequiredField)),
            "missing required pattern version");
    }

    private static void TransformRejectsAnchorDrift()
    {
        string fragment = "void main()\n{\n    // Lighting calculations\n    outColor = finalColor;\n}";
        string transformedFragment = TerrainShaderSource.Transform(fragment, "PlanetFrag");
        True(transformedFragment.Contains("BlastoMaterial(blendedDiffuse, roughness, worldNormal);", StringComparison.Ordinal),
            "fragment material hook inserted");
        True(transformedFragment.Contains("BlastoHighlight(finalColor);\n    outColor = finalColor;", StringComparison.Ordinal),
            "projection diagnostic overrides the final fragment color");
        int projectionBranch = transformedFragment.IndexOf("if ((blasto.header.y & 1u) != 0u)", StringComparison.Ordinal);
        int normalSlab = transformedFragment.IndexOf("if (abs(dot(delta,N)) > max(6.0, radius*0.2))", StringComparison.Ordinal);
        True(projectionBranch >= 0 && normalSlab > projectionBranch,
            "projection diagnostic bypasses height tolerance while normal rendering keeps the slab test");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(fragment.Replace("    // Lighting calculations", "", StringComparison.Ordinal), "PlanetFrag"),
            "missing fragment lighting anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(fragment + "\n    // Lighting calculations", "PlanetFrag"),
            "duplicated fragment lighting anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(fragment + "\nvoid main() {}", "PlanetFrag"),
            "duplicated fragment entry-point anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(fragment.Replace("    outColor = finalColor;", "", StringComparison.Ordinal), "PlanetFrag"),
            "missing fragment output anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(fragment + "\n    outColor = finalColor;", "PlanetFrag"),
            "duplicated fragment output anchor");

        string vertex = "void main()\n{\n    outWorldPosition = worldPos;\n}";
        string transformedVertex = TerrainShaderSource.Transform(vertex, "PlanetVert");
        True(transformedVertex.Contains("blastoSurfacePosition = worldPos.xyz;", StringComparison.Ordinal),
            "vertex surface position output inserted");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(vertex.Replace("    outWorldPosition = worldPos;", "", StringComparison.Ordinal), "PlanetVert"),
            "missing vertex position anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(vertex + "\n    outWorldPosition = worldPos;", "PlanetVert"),
            "duplicated vertex position anchor");

        string tessellation = "void main(void)\n{\n        outWorldPosition = vec4(worldPos.xyz, 1.0f);\n    }\n    outWorldPosition = vec4(worldPos.xyz, 1.0f);";
        string transformedTessellation = TerrainShaderSource.Transform(tessellation, "PlanetTessEvaluation");
        True(transformedTessellation.Contains("blastoSurfacePosition = worldPos.xyz;", StringComparison.Ordinal),
            "tessellation surface position output inserted");
        True(transformedTessellation.Contains("blastoSurfacePosition = displacedPosition;", StringComparison.Ordinal),
            "displaced tessellation surface position output inserted");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(tessellation.Replace("void main(void)", "", StringComparison.Ordinal), "PlanetTessEvaluation"),
            "missing tessellation entry-point anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(tessellation + "\nvoid main(void) {}", "PlanetTessEvaluation"),
            "duplicated tessellation entry-point anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(tessellation.Replace("\n        outWorldPosition = vec4(worldPos.xyz, 1.0f);", "", StringComparison.Ordinal), "PlanetTessEvaluation"),
            "missing tessellation inner output anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(tessellation.Replace("\n    outWorldPosition = vec4(worldPos.xyz, 1.0f);", "", StringComparison.Ordinal), "PlanetTessEvaluation"),
            "missing tessellation outer output anchor");
        Throws<InvalidOperationException>(
            () => TerrainShaderSource.Transform(tessellation + "\n    outWorldPosition = vec4(worldPos.xyz, 1.0f);", "PlanetTessEvaluation"),
            "duplicated tessellation output anchor");
    }

    private static BlastSaveDocument ValidDocument(BlastMark[] marks) => new()
    {
        SchemaVersion = 1,
        PatternVersion = 1,
        GameBuild = "test-build",
        UniverseSha256 = new string('A', 64),
        Settings = new BlastSettings(Enabled: true, DustEnabled: false, Intensity: 1.25),
        Marks = marks,
    };

    private static BlastMark ValidMark() => new(
        Guid.Parse("22222222-2222-4222-8222-222222222222"), "Luna",
        new Double3(1_000, 0, 0), new Double3(0, 1, 0), new Double3(0, 0, 1),
        Radius: 10, Exposure: 1.5, Anisotropy: 1.2, Seed: 42,
        Profile: SurfaceKind.Lunar, CreatedSeconds: 10, UpdatedSeconds: 12);

    private static void WithTempDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "blasto-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SurfaceHit Hit(string body = "Luna", Double3? center = null,
        double radius = 10, SurfaceKind profile = SurfaceKind.Lunar) =>
        new(body, center ?? new Double3(1_000, 0, 0), new Double3(1, 0, 0),
            new Double3(0, 1, 0), new Double3(0, 0, 1), 0, 1, radius, 1, profile);

    private static void Near(double expected, double actual, double tolerance, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{label}: expected {expected:R}, got {actual:R} (tolerance {tolerance:R})");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }

    private static void IsNull(object? value, string label)
    {
        if (value is not null)
            throw new InvalidOperationException($"{label}: expected rejection, got a value");
    }

    private static void True(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private static TException Throws<TException>(Action action, string label) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"{label}: expected {typeof(TException).Name}");
    }
}
