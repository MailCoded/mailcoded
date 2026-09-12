using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Mailcoded.Core.Embedding;

namespace Mailcoded.Core.Tests.Support;

/// <summary>
/// A real model directory, two layers wide, written to disk. Its weights are random, so it says
/// nothing about meaning — but it exercises every path an installed model takes.
/// </summary>
public static class TinyModel
{
    public const int Dimensions = 8;

    public static readonly string[] Vocabulary =
    [
        "[PAD]", "[UNK]", "[CLS]", "[SEP]",
        "quarterly", "report", "invoice", "roof", "the", "meeting",
        "budget", "garden", "weather", "notes", "leak", "repair",
    ];

    public static EncoderConfig Config { get; } = new()
    {
        Layers = 2,
        Hidden = Dimensions,
        Heads = 2,
        Intermediate = 16,
        VocabularySize = Vocabulary.Length,
        MaxPositions = 64,
    };

    /// <summary>Writes config.json, model.safetensors and vocab.txt into <paramref name="directory"/>.</summary>
    public static string Write(string directory, int seed = 17)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, TextEmbedder.ConfigFileName),
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                {"num_hidden_layers":{{Config.Layers}},"hidden_size":{{Config.Hidden}},
                 "num_attention_heads":{{Config.Heads}},"intermediate_size":{{Config.Intermediate}},
                 "vocab_size":{{Config.VocabularySize}},"max_position_embeddings":{{Config.MaxPositions}}}
                """));

        File.WriteAllLines(Path.Combine(directory, TextEmbedder.VocabularyFileName), Vocabulary);
        File.WriteAllBytes(Path.Combine(directory, TextEmbedder.WeightsFileName), Weights(seed));

        return directory;
    }

    private static byte[] Weights(int seed)
    {
        var random = new Random(seed);
        var names = new List<string>();
        var shapes = new List<int[]>();
        var values = new List<float[]>();

        void Add(string name, bool ones, params int[] shape)
        {
            var count = 1;
            foreach (var dimension in shape) count *= dimension;

            var tensor = new float[count];
            for (var i = 0; i < count; i++)
                tensor[i] = ones ? 1f : ((float)random.NextDouble() - 0.5f) * 0.4f;

            names.Add(name);
            shapes.Add(shape);
            values.Add(tensor);
        }

        var hidden = Config.Hidden;
        Add("embeddings.word_embeddings.weight", false, Config.VocabularySize, hidden);
        Add("embeddings.position_embeddings.weight", false, Config.MaxPositions, hidden);
        Add("embeddings.token_type_embeddings.weight", false, 2, hidden);
        Add("embeddings.LayerNorm.weight", true, hidden);
        Add("embeddings.LayerNorm.bias", false, hidden);

        for (var layer = 0; layer < Config.Layers; layer++)
        {
            var p = $"encoder.layer.{layer}.";
            Add(p + "attention.self.query.weight", false, hidden, hidden);
            Add(p + "attention.self.query.bias", false, hidden);
            Add(p + "attention.self.key.weight", false, hidden, hidden);
            Add(p + "attention.self.key.bias", false, hidden);
            Add(p + "attention.self.value.weight", false, hidden, hidden);
            Add(p + "attention.self.value.bias", false, hidden);
            Add(p + "attention.output.dense.weight", false, hidden, hidden);
            Add(p + "attention.output.dense.bias", false, hidden);
            Add(p + "attention.output.LayerNorm.weight", true, hidden);
            Add(p + "attention.output.LayerNorm.bias", false, hidden);
            Add(p + "intermediate.dense.weight", false, Config.Intermediate, hidden);
            Add(p + "intermediate.dense.bias", false, Config.Intermediate);
            Add(p + "output.dense.weight", false, hidden, Config.Intermediate);
            Add(p + "output.dense.bias", false, hidden);
            Add(p + "output.LayerNorm.weight", true, hidden);
            Add(p + "output.LayerNorm.bias", false, hidden);
        }

        var header = new StringBuilder("{");
        var body = new List<byte>();

        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0) header.Append(',');

            var start = body.Count;
            var bytes = new byte[values[i].Length * 4];
            for (var v = 0; v < values[i].Length; v++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(v * 4), values[i][v]);
            body.AddRange(bytes);

            header.Append(CultureInfo.InvariantCulture, $"\"{names[i]}\":{{\"dtype\":\"F32\",\"shape\":[{string.Join(",", shapes[i])}],\"data_offsets\":[{start},{body.Count}]}}");
        }

        header.Append('}');

        var headerBytes = Encoding.UTF8.GetBytes(header.ToString());
        var file = new byte[8 + headerBytes.Length + body.Count];
        BinaryPrimitives.WriteUInt64LittleEndian(file, (ulong)headerBytes.Length);
        headerBytes.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 8 + headerBytes.Length);
        return file;
    }
}
