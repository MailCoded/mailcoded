using System.Numerics;

namespace Mailcoded.Core.Embedding;

/// <summary>Stores a vector as one byte per dimension plus a scale. The vector is L2-normalised first,
/// so cosine similarity becomes a plain dot product and a query needs no renormalising at search time.</summary>
public static class Quantizer
{
    private const int Ceiling = 127;

    /// <summary>Normalises, quantises, and returns the scale that turns the bytes back into floats.</summary>
    public static float Quantize(ReadOnlySpan<float> vector, Span<sbyte> destination)
    {
        if (destination.Length != vector.Length)
        {
            throw new ArgumentException($"A {vector.Length}-dimension vector needs {vector.Length} bytes.", nameof(destination));
        }

        Span<float> unit = vector.Length <= 1024 ? stackalloc float[vector.Length] : new float[vector.Length];
        vector.CopyTo(unit);
        Kernels.L2Normalize(unit);

        var largest = 0f;
        foreach (var value in unit) largest = MathF.Max(largest, MathF.Abs(value));
        if (largest <= 0f)
        {
            destination.Clear();
            return 0f;
        }

        var scale = largest / Ceiling;
        var inverse = 1f / scale;
        for (var i = 0; i < unit.Length; i++)
        {
            var rounded = MathF.Round(unit[i] * inverse);
            destination[i] = (sbyte)Math.Clamp(rounded, -Ceiling, Ceiling);
        }

        return scale;
    }

    /// <summary>Cosine similarity of two quantised unit vectors.</summary>
    public static float Similarity(ReadOnlySpan<sbyte> left, float leftScale, ReadOnlySpan<sbyte> right, float rightScale)
    {
        if (left.Length != right.Length) throw new ArgumentException("Vectors differ in length.", nameof(right));
        return Dot(left, right) * leftScale * rightScale;
    }

    public static byte[] ToBytes(ReadOnlySpan<sbyte> values)
    {
        var bytes = new byte[values.Length];
        for (var i = 0; i < values.Length; i++) bytes[i] = unchecked((byte)values[i]);
        return bytes;
    }

    public static void FromBytes(ReadOnlySpan<byte> bytes, Span<sbyte> values)
    {
        if (bytes.Length != values.Length) throw new ArgumentException("Lengths differ.", nameof(values));
        for (var i = 0; i < bytes.Length; i++) values[i] = unchecked((sbyte)bytes[i]);
    }

    private static int Dot(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
    {
        // Widened to short before multiplying: two sbytes at 127 overflow a single byte lane.
        var width = Vector<sbyte>.Count;
        var total = 0;
        var i = 0;

        for (; i <= left.Length - width; i += width)
        {
            Vector.Widen(new Vector<sbyte>(left[i..]), out var leftLow, out var leftHigh);
            Vector.Widen(new Vector<sbyte>(right[i..]), out var rightLow, out var rightHigh);

            Vector.Widen(leftLow * rightLow, out var productA, out var productB);
            Vector.Widen(leftHigh * rightHigh, out var productC, out var productD);

            total += Vector.Sum(productA) + Vector.Sum(productB) + Vector.Sum(productC) + Vector.Sum(productD);
        }

        for (; i < left.Length; i++) total += left[i] * right[i];
        return total;
    }
}
