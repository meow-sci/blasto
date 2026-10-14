namespace Blasto.Rendering;

/// <summary>Last terrain upload and cumulative command counts for one shader viewport slot.</summary>
public sealed class TerrainViewDiagnostics
{
    public int Slot { get; }
    public bool IsMain { get; internal set; }
    public string BodyId { get; internal set; } = "";
    public string Status { get; internal set; } = "Waiting for terrain view";
    public int SelectedMarkCount { get; internal set; }
    public int BodyMarkCount { get; internal set; }
    public int WeakMarkCount { get; internal set; }
    public int OutOfRangeMarkCount { get; internal set; }
    public int BudgetExcludedMarkCount { get; internal set; }
    public double LargestSelectedRadiusMeters { get; internal set; }
    public double NearestMarkDistanceMeters { get; internal set; } = double.PositiveInfinity;
    public double StrongestMarkStrength { get; internal set; }
    public long RenderCalls { get; internal set; }
    public long DescriptorBinds { get; internal set; }
    public long DrawAttempts { get; internal set; }

    internal TerrainViewDiagnostics(int slot) => Slot = slot;

    internal void ResetSelection()
    {
        SelectedMarkCount = BodyMarkCount = WeakMarkCount = OutOfRangeMarkCount = BudgetExcludedMarkCount = 0;
        NearestMarkDistanceMeters = double.PositiveInfinity;
        StrongestMarkStrength = 0;
        LargestSelectedRadiusMeters = 0;
    }
}
