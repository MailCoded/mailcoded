using System.Buffers.Binary;
using System.Text;
using Mailcoded.Core.Embedding;
using Xunit;

namespace Mailcoded.Core.Tests.Embedding;

public sealed class BertEncoderTests : IDisposable
{
    private static readonly EncoderConfig Tiny = new()
    {
        Layers = 2,
        Hidden = 8,
        Heads = 2,
        Intermediate = 16,
        VocabularySize = 12,
        MaxPositions = 10,
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("mailcoded-encoder-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Produces_a_unit_vector_of_the_configured_width()
    {
        using var weights = Model(Tiny, seed: 1);
        var encoder = BertEncoder.Load(weights, Tiny);
        var vector = new float[Tiny.Hidden];

        encoder.Encode([2, 4, 5, 3], vector);

        Assert.Equal(Tiny.Hidden, encoder.Dimensions);
        Assert.True(Math.Abs(Kernels.Dot(vector, vector) - 1f) < 1e-4f, "the result is not a unit vector");
        foreach (var value in vector) Assert.True(float.IsFinite(value), $"{value} is not finite");
    }

    [Fact]
    public void Is_deterministic_and_separates_different_sequences()
    {
        using var weights = Model(Tiny, seed: 2);
        var encoder = BertEncoder.Load(weights, Tiny);

        var first = new float[Tiny.Hidden];
        var again = new float[Tiny.Hidden];
        var other = new float[Tiny.Hidden];

        encoder.Encode([2, 4, 5, 3], first);
        encoder.Encode([2, 4, 5, 3], again);
        encoder.Encode([2, 7, 8, 3], other);

        Assert.Equal(first, again);
        Assert.True(Kernels.Dot(first, other) < 0.9999f, "two different sequences produced the same vector");
    }

    /// <summary>Head slicing is the easiest thing to get wrong, so the whole pass is checked against a
    /// second implementation written plainly, with no shared code but the weights.</summary>
    [Fact]
    public void Matches_a_naive_forward_pass()
    {
        var tensors = Tensors(Tiny, seed: 3);
        using var weights = Model(tensors);
        var encoder = BertEncoder.Load(weights, Tiny);

        int[] ids = [2, 6, 9, 4, 3];
        var actual = new float[Tiny.Hidden];
        encoder.Encode(ids, actual);

        var expected = NaiveEncode(tensors, Tiny, ids);

        for (var i = 0; i < actual.Length; i++)
        {
            Assert.True(Math.Abs(actual[i] - expected[i]) < 1e-4f, $"dimension {i}: {actual[i]} against {expected[i]}");
        }
    }

    [Fact]
    public void Reads_weights_that_are_nested_under_a_bert_prefix()
    {
        var prefixed = new Dictionary<string, (int[] Shape, float[] Values)>();
        foreach (var (name, tensor) in Tensors(Tiny, seed: 4)) prefixed["bert." + name] = tensor;

        using var weights = Model(prefixed);
        var encoder = BertEncoder.Load(weights, Tiny);
        var vector = new float[Tiny.Hidden];

        encoder.Encode([2, 5, 3], vector);

        Assert.True(Math.Abs(Kernels.Dot(vector, vector) - 1f) < 1e-4f);
    }

    [Fact]
    public void Refuses_weights_that_disagree_with_the_config()
    {
        using var weights = Model(Tiny, seed: 5);
        var wider = Tiny with { Hidden = 16 };

        var thrown = Assert.Throws<InvalidDataException>(() => BertEncoder.Load(weights, wider));
        Assert.Contains("implies", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_config_whose_heads_do_not_divide_the_hidden_size()
    {
        var config = Tiny with { Heads = 3 };

        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void Refuses_a_token_outside_the_vocabulary()
    {
        using var weights = Model(Tiny, seed: 6);
        var encoder = BertEncoder.Load(weights, Tiny);

        Assert.Throws<ArgumentException>(() => encoder.Encode([2, 99, 3], new float[Tiny.Hidden]));
        Assert.Throws<ArgumentException>(() => encoder.Encode([2, -1, 3], new float[Tiny.Hidden]));
    }

    [Fact]
    public void Refuses_more_tokens_than_the_model_has_positions_for()
    {
        using var weights = Model(Tiny, seed: 7);
        var encoder = BertEncoder.Load(weights, Tiny);
        var tooMany = new int[Tiny.MaxPositions + 1];
        Array.Fill(tooMany, 4);

        Assert.Throws<ArgumentException>(() => encoder.Encode(tooMany, new float[Tiny.Hidden]));
    }

    [Fact]
    public void Refuses_an_empty_sequence_and_a_destination_of_the_wrong_width()
    {
        using var weights = Model(Tiny, seed: 8);
        var encoder = BertEncoder.Load(weights, Tiny);

        Assert.Throws<ArgumentException>(() => encoder.Encode([], new float[Tiny.Hidden]));
        Assert.Throws<ArgumentException>(() => encoder.Encode([2, 3], new float[Tiny.Hidden + 1]));
    }

    private static float[] NaiveEncode(
        Dictionary<string, (int[] Shape, float[] Values)> tensors,
        EncoderConfig config,
        int[] ids)
    {
        float[] Get(string name) => tensors[name].Values;
        var hidden = config.Hidden;
        var headSize = config.Hidden / config.Heads;

        var states = new float[ids.Length][];
        for (var t = 0; t < ids.Length; t++)
        {
            var state = new float[hidden];
            for (var i = 0; i < hidden; i++)
            {
                state[i] = Get("embeddings.word_embeddings.weight")[(ids[t] * hidden) + i]
                    + Get("embeddings.position_embeddings.weight")[(t * hidden) + i]
                    + Get("embeddings.token_type_embeddings.weight")[i];
            }

            Norm(state, Get("embeddings.LayerNorm.weight"), Get("embeddings.LayerNorm.bias"), config);
            states[t] = state;
        }

        for (var layer = 0; layer < config.Layers; layer++)
        {
            var p = $"encoder.layer.{layer}.";
            var q = new float[ids.Length][];
            var k = new float[ids.Length][];
            var v = new float[ids.Length][];

            for (var t = 0; t < ids.Length; t++)
            {
                q[t] = Dense(states[t], Get(p + "attention.self.query.weight"), Get(p + "attention.self.query.bias"), hidden);
                k[t] = Dense(states[t], Get(p + "attention.self.key.weight"), Get(p + "attention.self.key.bias"), hidden);
                v[t] = Dense(states[t], Get(p + "attention.self.value.weight"), Get(p + "attention.self.value.bias"), hidden);
            }

            var context = new float[ids.Length][];
            for (var t = 0; t < ids.Length; t++) context[t] = new float[hidden];

            for (var head = 0; head < config.Heads; head++)
            {
                var start = head * headSize;
                for (var t = 0; t < ids.Length; t++)
                {
                    var scores = new float[ids.Length];
                    for (var s = 0; s < ids.Length; s++)
                    {
                        double sum = 0;
                        for (var i = 0; i < headSize; i++) sum += q[t][start + i] * k[s][start + i];
                        scores[s] = (float)(sum / Math.Sqrt(headSize));
                    }

                    SoftMax(scores);
                    for (var s = 0; s < ids.Length; s++)
                    {
                        for (var i = 0; i < headSize; i++) context[t][start + i] += scores[s] * v[s][start + i];
                    }
                }
            }

            for (var t = 0; t < ids.Length; t++)
            {
                var projected = Dense(context[t], Get(p + "attention.output.dense.weight"), Get(p + "attention.output.dense.bias"), hidden);
                for (var i = 0; i < hidden; i++) states[t][i] += projected[i];
                Norm(states[t], Get(p + "attention.output.LayerNorm.weight"), Get(p + "attention.output.LayerNorm.bias"), config);

                var inner = Dense(states[t], Get(p + "intermediate.dense.weight"), Get(p + "intermediate.dense.bias"), config.Intermediate);
                for (var i = 0; i < inner.Length; i++)
                {
                    var x = inner[i];
                    inner[i] = (float)(0.5 * x * (1 + Erf(x / Math.Sqrt(2))));
                }

                var outer = Dense(inner, Get(p + "output.dense.weight"), Get(p + "output.dense.bias"), hidden);
                for (var i = 0; i < hidden; i++) states[t][i] += outer[i];
                Norm(states[t], Get(p + "output.LayerNorm.weight"), Get(p + "output.LayerNorm.bias"), config);
            }
        }

        var pooled = new float[hidden];
        foreach (var state in states)
        {
            for (var i = 0; i < hidden; i++) pooled[i] += state[i] / ids.Length;
        }

        double length = 0;
        foreach (var value in pooled) length += (double)value * value;
        length = Math.Sqrt(length);
        for (var i = 0; i < hidden; i++) pooled[i] = (float)(pooled[i] / length);
        return pooled;
    }

    private static float[] Dense(float[] input, float[] weight, float[] bias, int outputs)
    {
        var result = new float[outputs];
        for (var o = 0; o < outputs; o++)
        {
            double sum = bias[o];
            for (var i = 0; i < input.Length; i++) sum += input[i] * weight[(o * input.Length) + i];
            result[o] = (float)sum;
        }

        return result;
    }

    private static void Norm(float[] values, float[] gamma, float[] beta, EncoderConfig config)
    {
        double mean = 0;
        foreach (var value in values) mean += value;
        mean /= values.Length;

        double variance = 0;
        foreach (var value in values) variance += (value - mean) * (value - mean);
        variance /= values.Length;

        var scale = 1.0 / Math.Sqrt(variance + config.LayerNormEpsilon);
        for (var i = 0; i < values.Length; i++) values[i] = (float)(((values[i] - mean) * scale * gamma[i]) + beta[i]);
    }

    private static void SoftMax(float[] values)
    {
        var max = float.NegativeInfinity;
        foreach (var value in values) max = Math.Max(max, value);

        double total = 0;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)Math.Exp(values[i] - max);
            total += values[i];
        }

        for (var i = 0; i < values.Length; i++) values[i] = (float)(values[i] / total);
    }

    private static double Erf(double x)
    {
        const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1.0 / (1.0 + (p * x));
        var y = 1.0 - ((((((((a5 * t) + a4) * t) + a3) * t) + a2) * t) + a1) * t * Math.Exp(-x * x);
        return sign * y;
    }

    private static Dictionary<string, (int[] Shape, float[] Values)> Tensors(EncoderConfig config, int seed)
    {
        var random = new Random(seed);
        var tensors = new Dictionary<string, (int[], float[])>(StringComparer.Ordinal);

        void Add(string name, params int[] shape)
        {
            var count = 1;
            foreach (var dimension in shape) count *= dimension;
            var values = new float[count];
            for (var i = 0; i < count; i++) values[i] = ((float)random.NextDouble() - 0.5f) * 0.4f;
            tensors[name] = (shape, values);
        }

        Add("embeddings.word_embeddings.weight", config.VocabularySize, config.Hidden);
        Add("embeddings.position_embeddings.weight", config.MaxPositions, config.Hidden);
        Add("embeddings.token_type_embeddings.weight", 2, config.Hidden);
        Ones(tensors, "embeddings.LayerNorm.weight", config.Hidden);
        Add("embeddings.LayerNorm.bias", config.Hidden);

        for (var layer = 0; layer < config.Layers; layer++)
        {
            var p = $"encoder.layer.{layer}.";
            Add(p + "attention.self.query.weight", config.Hidden, config.Hidden);
            Add(p + "attention.self.query.bias", config.Hidden);
            Add(p + "attention.self.key.weight", config.Hidden, config.Hidden);
            Add(p + "attention.self.key.bias", config.Hidden);
            Add(p + "attention.self.value.weight", config.Hidden, config.Hidden);
            Add(p + "attention.self.value.bias", config.Hidden);
            Add(p + "attention.output.dense.weight", config.Hidden, config.Hidden);
            Add(p + "attention.output.dense.bias", config.Hidden);
            Ones(tensors, p + "attention.output.LayerNorm.weight", config.Hidden);
            Add(p + "attention.output.LayerNorm.bias", config.Hidden);
            Add(p + "intermediate.dense.weight", config.Intermediate, config.Hidden);
            Add(p + "intermediate.dense.bias", config.Intermediate);
            Add(p + "output.dense.weight", config.Hidden, config.Intermediate);
            Add(p + "output.dense.bias", config.Hidden);
            Ones(tensors, p + "output.LayerNorm.weight", config.Hidden);
            Add(p + "output.LayerNorm.bias", config.Hidden);
        }

        return tensors;
    }

    private static void Ones(Dictionary<string, (int[], float[])> tensors, string name, int count)
    {
        var values = new float[count];
        Array.Fill(values, 1f);
        tensors[name] = ([count], values);
    }

    private SafetensorsFile Model(EncoderConfig config, int seed) => Model(Tensors(config, seed));

    private SafetensorsFile Model(Dictionary<string, (int[] Shape, float[] Values)> tensors)
    {
        var header = new StringBuilder("{");
        var body = new List<byte>();
        var first = true;

        foreach (var (name, tensor) in tensors)
        {
            if (!first) header.Append(',');
            first = false;

            var start = body.Count;
            var bytes = new byte[tensor.Values.Length * 4];
            for (var i = 0; i < tensor.Values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), tensor.Values[i]);
            }

            body.AddRange(bytes);

            header.Append($"\"{name}\":{{\"dtype\":\"F32\",\"shape\":[{string.Join(",", tensor.Shape)}],\"data_offsets\":[{start},{body.Count}]}}");
        }

        header.Append('}');

        var headerBytes = Encoding.UTF8.GetBytes(header.ToString());
        var file = new byte[8 + headerBytes.Length + body.Count];
        BinaryPrimitives.WriteUInt64LittleEndian(file, (ulong)headerBytes.Length);
        headerBytes.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 8 + headerBytes.Length);

        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.safetensors");
        File.WriteAllBytes(path, file);
        return SafetensorsFile.Open(path);
    }
}
