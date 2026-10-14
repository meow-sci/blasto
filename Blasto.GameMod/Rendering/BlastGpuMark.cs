using System;
using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace Blasto.Rendering;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct BlastGpuMark
{
    public float4 CenterRadius;
    public float4 TangentUStrength;
    public float4 TangentVAnisotropy;
    public float4 TintRoughness;
    public uint Seed, Profile, Reserved0, Reserved1;

    public const int Size = BlastVisualMath.MarkStride;
    public const int HeaderSize = BlastVisualMath.HeaderSize;
    public const int MaximumMarks = BlastVisualMath.MaximumMarks;
    public const int Range = BlastVisualMath.Range;

    public static void AssertLayout()
    {
        if (Marshal.SizeOf<BlastGpuHeader>() != HeaderSize ||
            Marshal.OffsetOf<BlastGpuHeader>(nameof(BlastGpuHeader.Tuning0)).ToInt32() != 16 ||
            Marshal.OffsetOf<BlastGpuHeader>(nameof(BlastGpuHeader.Tuning1)).ToInt32() != 32 ||
            Marshal.SizeOf<BlastGpuMark>() != Size || Marshal.SizeOf<float4>() != 16 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(CenterRadius)).ToInt32() != 0 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(TangentUStrength)).ToInt32() != 16 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(TangentVAnisotropy)).ToInt32() != 32 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(TintRoughness)).ToInt32() != 48 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(Seed)).ToInt32() != 64 ||
            Marshal.OffsetOf<BlastGpuMark>(nameof(Profile)).ToInt32() != 68)
            throw new InvalidOperationException("Blasto std430 mark layout mismatch.");
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct BlastGpuHeader
{
    public uint Count, Flags, Reserved0, Reserved1;
    public float4 Tuning0;
    public float4 Tuning1;
}
