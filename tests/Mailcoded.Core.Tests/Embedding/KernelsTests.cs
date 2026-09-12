using Mailcoded.Core.Embedding;
using Xunit;

namespace Mailcoded.Core.Tests.Embedding;

/// <summary>Each kernel is checked against a naive scalar version of itself. The vectorised loops have
/// a tail the scalar ones do not, so the lengths below straddle every plausible SIMD width.</summary>
public sealed class KernelsTests
{
    private static readonly int[] Lengths = [0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 384];

    [Fact]
    public void Dot_matches_the_scalar_sum_at_every_length()
    {
        foreach (var length in Lengths)
        {
            var random = new Random(length + 1);
            var left = Random(random, length);
            var right = Random(random, length);

            double expected = 0;
            for (var i = 0; i < length; i++) expected += (double)left[i] * right[i];

            Assert.True(
                Math.Abs(Kernels.Dot(left, right) - expected) < 1e-3,
                $"length {length}: {Kernels.Dot(left, right)} against {expected}");
        }
    }

    [Fact]
    public void Add_matches_the_scalar_sum_at_every_length()
    {
        foreach (var length in Lengths)
        {
            var random = new Random(length + 7);
            var target = Random(random, length);
            var addend = Random(random, length);
            var expected = new float[length];
            for (var i = 0; i < length; i++) expected[i] = target[i] + addend[i];

            Kernels.Add(target, addend);

            Assert.Equal(expected, target);
        }
    }

    [Fact]
    public void Linear_matches_a_naive_matrix_multiply()
    {
        foreach (var inputs in new[] { 1, 3, 8, 17, 64 })
        {
            foreach (var outputs in new[] { 1, 2, 9 })
            {
                var random = new Random((inputs * 31) + outputs);
                var input = Random(random, inputs);
                var weight = Random(random, inputs * outputs);
                var bias = Random(random, outputs);

                var expected = new float[outputs];
                for (var o = 0; o < outputs; o++)
                {
                    double sum = bias[o];
                    for (var i = 0; i < inputs; i++) sum += (double)input[i] * weight[(o * inputs) + i];
                    expected[o] = (float)sum;
                }

                var actual = new float[outputs];
                Kernels.Linear(input, weight, bias, actual);

                for (var o = 0; o < outputs; o++)
                {
                    Assert.True(Math.Abs(actual[o] - expected[o]) < 1e-3, $"[{outputs},{inputs}] output {o}");
                }
            }
        }
    }

    [Fact]
    public void Linear_accepts_an_empty_bias_as_no_bias()
    {
        var input = new[] { 1f, 2f };
        var weight = new[] { 1f, 0f, 0f, 1f };
        var output = new float[2];

        Kernels.Linear(input, weight, [], output);

        Assert.Equal([1f, 2f], output);
    }

    [Fact]
    public void Linear_refuses_a_weight_that_is_not_the_declared_shape()
    {
        Assert.Throws<ArgumentException>(() => Kernels.Linear(new float[3], new float[5], [], new float[2]));
        Assert.Throws<ArgumentException>(() => Kernels.Linear(new float[3], new float[6], new float[3], new float[2]));
    }

    [Fact]
    public void LayerNorm_centres_and_scales_before_the_learned_parameters()
    {
        var values = new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 9f };
        var gamma = new float[values.Length];
        var beta = new float[values.Length];
        Array.Fill(gamma, 1f);

        Kernels.LayerNorm(values, gamma, beta, 1e-12f);

        double mean = 0;
        foreach (var value in values) mean += value;
        Assert.True(Math.Abs(mean / values.Length) < 1e-5, $"mean {mean / values.Length}");

        double square = 0;
        foreach (var value in values) square += (double)value * value;
        Assert.True(Math.Abs((square / values.Length) - 1) < 1e-4, $"variance {square / values.Length}");
    }

    [Fact]
    public void LayerNorm_applies_gamma_and_beta_after_normalising()
    {
        var plain = new[] { 1f, 2f, 3f, 4f };
        var scaled = new[] { 1f, 2f, 3f, 4f };
        var ones = new[] { 1f, 1f, 1f, 1f };
        var zeros = new float[4];
        var gamma = new[] { 2f, 2f, 2f, 2f };
        var beta = new[] { 0.5f, 0.5f, 0.5f, 0.5f };

        Kernels.LayerNorm(plain, ones, zeros, 1e-12f);
        Kernels.LayerNorm(scaled, gamma, beta, 1e-12f);

        for (var i = 0; i < plain.Length; i++)
        {
            Assert.True(Math.Abs(scaled[i] - ((plain[i] * 2f) + 0.5f)) < 1e-5, $"index {i}");
        }
    }

    [Fact]
    public void LayerNorm_survives_a_constant_input_without_dividing_by_zero()
    {
        var values = new[] { 3f, 3f, 3f, 3f };
        var ones = new[] { 1f, 1f, 1f, 1f };

        Kernels.LayerNorm(values, ones, new float[4], 1e-12f);

        foreach (var value in values) Assert.True(float.IsFinite(value), $"{value} is not finite");
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 0.8413447f)]
    [InlineData(-1f, -0.1586553f)]
    [InlineData(2f, 1.9544997f)]
    [InlineData(-3f, -0.0040498f)]
    public void Gelu_matches_the_error_function_definition(float input, float expected)
    {
        var values = new[] { input };

        Kernels.Gelu(values);

        Assert.True(Math.Abs(values[0] - expected) < 1e-4, $"gelu({input}) was {values[0]}, expected {expected}");
    }

    [Fact]
    public void Softmax_sums_to_one_and_keeps_the_order()
    {
        var values = new[] { 1f, 3f, 2f, -4f };

        Kernels.Softmax(values);

        double total = 0;
        foreach (var value in values) total += value;
        Assert.True(Math.Abs(total - 1) < 1e-5, $"total {total}");
        Assert.True(values[1] > values[2] && values[2] > values[0] && values[0] > values[3]);
    }

    /// <summary>An attention row over a long padded sequence is exactly where a naive exp overflows.</summary>
    [Fact]
    public void Softmax_does_not_overflow_on_large_inputs()
    {
        var values = new[] { 10_000f, 10_001f, 9_999f };

        Kernels.Softmax(values);

        foreach (var value in values) Assert.True(float.IsFinite(value), $"{value} is not finite");
        double total = 0;
        foreach (var value in values) total += value;
        Assert.True(Math.Abs(total - 1) < 1e-5, $"total {total}");
    }

    [Fact]
    public void Softmax_of_an_entirely_masked_row_is_zero_rather_than_nan()
    {
        var values = new[] { float.NegativeInfinity, float.NegativeInfinity };

        Kernels.Softmax(values);

        Assert.Equal([0f, 0f], values);
    }

    [Fact]
    public void L2Normalize_gives_a_unit_vector_and_leaves_zero_alone()
    {
        var values = new[] { 3f, 4f };
        Kernels.L2Normalize(values);
        Assert.True(Math.Abs(Kernels.Dot(values, values) - 1) < 1e-5);

        var zero = new float[8];
        Kernels.L2Normalize(zero);
        Assert.Equal(new float[8], zero);
    }

    private static float[] Random(Random random, int length)
    {
        var values = new float[length];
        for (var i = 0; i < length; i++) values[i] = ((float)random.NextDouble() * 4f) - 2f;
        return values;
    }
}
