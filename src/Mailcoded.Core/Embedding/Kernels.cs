using System.Numerics;

namespace Mailcoded.Core.Embedding;

/// <summary>The arithmetic the encoder is made of. Portable <see cref="Vector{T}"/> throughout, never
/// the Avx2 or Fma intrinsics classes: those throw at the default AOT instruction-set baseline, where
/// the portable API degrades to whatever the machine has.</summary>
public static class Kernels
{
    public static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Vectors differ in length.", nameof(right));

        var width = Vector<float>.Count;
        var total = Vector<float>.Zero;
        var i = 0;

        for (; i <= left.Length - width; i += width)
        {
            total += new Vector<float>(left[i..]) * new Vector<float>(right[i..]);
        }

        var sum = Vector.Sum(total);
        for (; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }

    /// <summary>Row-major [outputs, inputs], the layout the weights file already uses, so each output
    /// is one contiguous dot product.</summary>
    public static void Linear(
        ReadOnlySpan<float> input,
        ReadOnlySpan<float> weight,
        ReadOnlySpan<float> bias,
        Span<float> output)
    {
        var inputs = input.Length;
        var outputs = output.Length;

        if (weight.Length != checked(inputs * outputs))
        {
            throw new ArgumentException($"A [{outputs},{inputs}] weight needs {inputs * outputs} values.", nameof(weight));
        }
        if (bias.Length != 0 && bias.Length != outputs)
        {
            throw new ArgumentException($"A bias for {outputs} outputs needs {outputs} values.", nameof(bias));
        }

        for (var o = 0; o < outputs; o++)
        {
            var row = weight.Slice(o * inputs, inputs);
            output[o] = Dot(input, row) + (bias.Length == 0 ? 0f : bias[o]);
        }
    }

    public static void LayerNorm(Span<float> values, ReadOnlySpan<float> gamma, ReadOnlySpan<float> beta, float epsilon)
    {
        if (gamma.Length != values.Length || beta.Length != values.Length)
        {
            throw new ArgumentException("Scale and shift must match the value count.", nameof(gamma));
        }

        double sum = 0;
        foreach (var value in values) sum += value;
        var mean = (float)(sum / values.Length);

        double square = 0;
        foreach (var value in values)
        {
            var delta = value - mean;
            square += delta * delta;
        }

        var scale = 1f / MathF.Sqrt((float)(square / values.Length) + epsilon);
        for (var i = 0; i < values.Length; i++) values[i] = ((values[i] - mean) * scale * gamma[i]) + beta[i];
    }

    /// <summary>The exact error-function GELU the reference models use, not the tanh approximation:
    /// a different activation is a different model, and the weights were trained against this one.</summary>
    public static void Gelu(Span<float> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var x = values[i];
            values[i] = 0.5f * x * (1f + Erf(x * 0.70710678f));
        }
    }

    public static void Softmax(Span<float> values)
    {
        if (values.IsEmpty) return;

        var max = float.NegativeInfinity;
        foreach (var value in values) max = MathF.Max(max, value);
        if (float.IsNegativeInfinity(max))
        {
            values.Fill(0f);
            return;
        }

        double total = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var weight = MathF.Exp(values[i] - max);
            values[i] = weight;
            total += weight;
        }

        var scale = (float)(1.0 / total);
        for (var i = 0; i < values.Length; i++) values[i] *= scale;
    }

    /// <summary>Leaves an all-zero vector alone rather than dividing by nothing.</summary>
    public static void L2Normalize(Span<float> values)
    {
        var length = MathF.Sqrt(Dot(values, values));
        if (length <= 1e-12f) return;

        var scale = 1f / length;
        for (var i = 0; i < values.Length; i++) values[i] *= scale;
    }

    public static void Add(Span<float> target, ReadOnlySpan<float> addend)
    {
        if (target.Length != addend.Length) throw new ArgumentException("Vectors differ in length.", nameof(addend));

        var width = Vector<float>.Count;
        var i = 0;
        for (; i <= target.Length - width; i += width)
        {
            (new Vector<float>(target[i..]) + new Vector<float>(addend[i..])).CopyTo(target[i..]);
        }

        for (; i < target.Length; i++) target[i] += addend[i];
    }

    public static void Scale(Span<float> values, float factor)
    {
        for (var i = 0; i < values.Length; i++) values[i] *= factor;
    }

    /// <summary>Abramowitz and Stegun 7.1.26, accurate to about 1.5e-7 across the range.</summary>
    private static float Erf(float x)
    {
        const float a1 = 0.254829592f;
        const float a2 = -0.284496736f;
        const float a3 = 1.421413741f;
        const float a4 = -1.453152027f;
        const float a5 = 1.061405429f;
        const float p = 0.3275911f;

        var sign = MathF.Sign(x);
        var value = MathF.Abs(x);
        var t = 1f / (1f + (p * value));
        var y = 1f - ((((((((a5 * t) + a4) * t) + a3) * t) + a2) * t) + a1) * t * MathF.Exp(-value * value);
        return sign * y;
    }
}
