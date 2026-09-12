using Mailcoded.Core.Embedding;
using Xunit;

namespace Mailcoded.Core.Tests.Embedding;

public sealed class QuantizerTests
{
    [Fact]
    public void A_vector_is_maximally_similar_to_itself()
    {
        var vector = Random(384, seed: 1);
        var quantized = new sbyte[384];
        var scale = Quantizer.Quantize(vector, quantized);

        var similarity = Quantizer.Similarity(quantized, scale, quantized, scale);

        Assert.True(Math.Abs(similarity - 1f) < 0.01f, $"self-similarity was {similarity}");
    }

    /// <summary>The whole point: quantised similarity has to rank the same way the float one does.</summary>
    [Fact]
    public void Quantised_similarity_tracks_the_float_cosine()
    {
        var random = new Random(7);
        for (var trial = 0; trial < 40; trial++)
        {
            var left = Random(384, random.Next());
            var right = Random(384, random.Next());

            var leftUnit = (float[])left.Clone();
            var rightUnit = (float[])right.Clone();
            Kernels.L2Normalize(leftUnit);
            Kernels.L2Normalize(rightUnit);
            var exact = Kernels.Dot(leftUnit, rightUnit);

            var leftBytes = new sbyte[384];
            var rightBytes = new sbyte[384];
            var leftScale = Quantizer.Quantize(left, leftBytes);
            var rightScale = Quantizer.Quantize(right, rightBytes);
            var approximate = Quantizer.Similarity(leftBytes, leftScale, rightBytes, rightScale);

            Assert.True(Math.Abs(approximate - exact) < 0.02f, $"trial {trial}: {approximate} against {exact}");
        }
    }

    [Fact]
    public void Similarity_separates_a_near_duplicate_from_an_unrelated_vector()
    {
        var anchor = Random(384, seed: 11);
        var near = (float[])anchor.Clone();
        for (var i = 0; i < near.Length; i += 16) near[i] += 0.05f;
        var far = Random(384, seed: 99);

        var quantise = (float[] values) =>
        {
            var bytes = new sbyte[values.Length];
            return (bytes, scale: Quantizer.Quantize(values, bytes));
        };

        var (anchorBytes, anchorScale) = quantise(anchor);
        var (nearBytes, nearScale) = quantise(near);
        var (farBytes, farScale) = quantise(far);

        Assert.True(
            Quantizer.Similarity(anchorBytes, anchorScale, nearBytes, nearScale)
            > Quantizer.Similarity(anchorBytes, anchorScale, farBytes, farScale));
    }

    [Fact]
    public void A_zero_vector_quantises_to_zero_rather_than_dividing_by_nothing()
    {
        var bytes = new sbyte[8];

        var scale = Quantizer.Quantize(new float[8], bytes);

        Assert.Equal(0f, scale);
        Assert.Equal(new sbyte[8], bytes);
        Assert.Equal(0f, Quantizer.Similarity(bytes, scale, bytes, scale));
    }

    [Fact]
    public void Scaling_a_vector_does_not_change_its_quantisation()
    {
        var vector = Random(64, seed: 3);
        var scaled = new float[64];
        for (var i = 0; i < vector.Length; i++) scaled[i] = vector[i] * 17.5f;

        var first = new sbyte[64];
        var second = new sbyte[64];
        Quantizer.Quantize(vector, first);
        Quantizer.Quantize(scaled, second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Bytes_round_trip_through_storage()
    {
        var vector = Random(384, seed: 5);
        var quantized = new sbyte[384];
        Quantizer.Quantize(vector, quantized);

        var restored = new sbyte[384];
        Quantizer.FromBytes(Quantizer.ToBytes(quantized), restored);

        Assert.Equal(quantized, restored);
    }

    [Fact]
    public void The_dot_product_is_exact_at_the_extremes_its_lanes_could_overflow()
    {
        var left = new sbyte[96];
        var right = new sbyte[96];
        Array.Fill(left, (sbyte)127);
        Array.Fill(right, (sbyte)127);

        Assert.Equal(96 * 127 * 127, (int)Math.Round(Quantizer.Similarity(left, 1f, right, 1f)));

        Array.Fill(right, (sbyte)-127);
        Assert.Equal(-96 * 127 * 127, (int)Math.Round(Quantizer.Similarity(left, 1f, right, 1f)));
    }

    [Fact]
    public void The_dot_product_is_exact_at_every_length_around_a_vector_width()
    {
        foreach (var length in new[] { 1, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 384 })
        {
            var random = new Random(length);
            var left = new sbyte[length];
            var right = new sbyte[length];
            var expected = 0;
            for (var i = 0; i < length; i++)
            {
                left[i] = (sbyte)random.Next(-127, 128);
                right[i] = (sbyte)random.Next(-127, 128);
                expected += left[i] * right[i];
            }

            Assert.Equal(expected, (int)Math.Round(Quantizer.Similarity(left, 1f, right, 1f)));
        }
    }

    [Fact]
    public void Refuses_a_destination_of_the_wrong_size()
    {
        Assert.Throws<ArgumentException>(() => Quantizer.Quantize(new float[4], new sbyte[3]));
        Assert.Throws<ArgumentException>(() => Quantizer.Similarity(new sbyte[4], 1f, new sbyte[3], 1f));
    }

    private static float[] Random(int length, int seed)
    {
        var random = new Random(seed);
        var values = new float[length];
        for (var i = 0; i < length; i++) values[i] = ((float)random.NextDouble() * 2f) - 1f;
        return values;
    }
}
