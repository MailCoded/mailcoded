using System.Text.Json;

namespace Mailcoded.Core.Embedding;

/// <summary>The geometry of a BERT-family encoder. Every field is checked against the weights that
/// claim to match it, because a config that disagrees with its tensors produces silent nonsense.</summary>
public sealed record EncoderConfig
{
    public required int Layers { get; init; }

    public required int Hidden { get; init; }

    public required int Heads { get; init; }

    public required int Intermediate { get; init; }

    public required int VocabularySize { get; init; }

    public required int MaxPositions { get; init; }

    public bool Uncased { get; init; } = true;

    public float LayerNormEpsilon { get; init; } = 1e-12f;

    public int HeadSize => Hidden / Heads;

    public static EncoderConfig Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The model config is not an object.");

        var config = new EncoderConfig
        {
            Layers = Int(root, "num_hidden_layers"),
            Hidden = Int(root, "hidden_size"),
            Heads = Int(root, "num_attention_heads"),
            Intermediate = Int(root, "intermediate_size"),
            VocabularySize = Int(root, "vocab_size"),
            MaxPositions = Int(root, "max_position_embeddings"),
            LayerNormEpsilon = root.TryGetProperty("layer_norm_eps", out var epsilon) && epsilon.TryGetSingle(out var value)
                ? value
                : 1e-12f,
        };

        config.Validate();
        return config;
    }

    public void Validate()
    {
        Positive(Layers, nameof(Layers));
        Positive(Hidden, nameof(Hidden));
        Positive(Heads, nameof(Heads));
        Positive(Intermediate, nameof(Intermediate));
        Positive(VocabularySize, nameof(VocabularySize));
        Positive(MaxPositions, nameof(MaxPositions));

        if (Hidden % Heads != 0)
        {
            throw new InvalidDataException($"A hidden size of {Hidden} does not divide into {Heads} heads.");
        }
    }

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"The model config has no integer '{name}'.");

    private static void Positive(int value, string name)
    {
        if (value <= 0) throw new InvalidDataException($"'{name}' must be positive, not {value}.");
    }
}
