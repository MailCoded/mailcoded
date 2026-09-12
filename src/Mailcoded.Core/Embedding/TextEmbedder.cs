using System.Security.Cryptography;

namespace Mailcoded.Core.Embedding;

/// <summary>An installed model directory, loaded into the three pieces that turn text into a stored
/// vector: tokenizer, encoder, quantizer. Nothing here fetches anything; the files are already there
/// or the model is not installed.</summary>
public sealed class TextEmbedder
{
    public const string ConfigFileName = "config.json";
    public const string WeightsFileName = "model.safetensors";
    public const string VocabularyFileName = "vocab.txt";
    public const string MeanPooling = "mean";

    private readonly WordPieceTokenizer _tokenizer;
    private readonly BertEncoder _encoder;

    private TextEmbedder(WordPieceTokenizer tokenizer, BertEncoder encoder, string fingerprint, string? name)
    {
        _tokenizer = tokenizer;
        _encoder = encoder;
        Fingerprint = fingerprint;
        Name = name;
    }

    /// <summary>Identity of this model, over all three files. Vectors built by two embedders are
    /// comparable only if this matches.</summary>
    public string Fingerprint { get; }

    public string? Name { get; }

    public int Dimensions => _encoder.Dimensions;

    public string Pooling => MeanPooling;

    public int MaxTokens => _tokenizer.MaxTokens;

    public static bool IsInstalledAt(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && File.Exists(Path.Combine(directory, ConfigFileName))
        && File.Exists(Path.Combine(directory, WeightsFileName))
        && File.Exists(Path.Combine(directory, VocabularyFileName));

    public static TextEmbedder Load(string directory, int maxTokens = WordPieceTokenizer.DefaultMaxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var configPath = Path.Combine(directory, ConfigFileName);
        var weightsPath = Path.Combine(directory, WeightsFileName);
        var vocabularyPath = Path.Combine(directory, VocabularyFileName);

        if (!IsInstalledAt(directory))
        {
            throw new InvalidDataException(
                $"No model is installed at '{directory}': {ConfigFileName}, {WeightsFileName} and {VocabularyFileName} are all needed.");
        }

        var config = EncoderConfig.Read(configPath);
        var vocabulary = WordPieceVocabulary.Load(vocabularyPath);

        if (vocabulary.Count > config.VocabularySize)
        {
            throw new InvalidDataException(
                $"The vocabulary holds {vocabulary.Count} tokens but the model was built for {config.VocabularySize}.");
        }

        using var weights = SafetensorsFile.Open(weightsPath);
        var encoder = BertEncoder.Load(weights, config);

        var tokens = Math.Min(maxTokens, config.MaxPositions);
        var tokenizer = new WordPieceTokenizer(vocabulary, config.Uncased, tokens);

        return new TextEmbedder(
            tokenizer,
            encoder,
            FingerprintOf(weightsPath, configPath, vocabularyPath),
            new DirectoryInfo(directory).Name);
    }

    /// <summary>Encodes text into a unit vector of <see cref="Dimensions"/>.</summary>
    public void Embed(string text, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(text);

        Span<int> ids = _tokenizer.MaxTokens <= 512 ? stackalloc int[_tokenizer.MaxTokens] : new int[_tokenizer.MaxTokens];
        var count = _tokenizer.Tokenize(text, ids);
        _encoder.Encode(ids[..count], destination);
    }

    /// <summary>Encodes text straight to the int8 form the store holds, returning the scale that
    /// turns a dot product back into a cosine.</summary>
    public float Embed(string text, Span<sbyte> destination)
    {
        if (destination.Length != Dimensions)
        {
            throw new ArgumentException($"A {Dimensions}-dimension destination is needed.", nameof(destination));
        }

        Span<float> dense = Dimensions <= 1024 ? stackalloc float[Dimensions] : new float[Dimensions];
        Embed(text, dense);
        return Quantizer.Quantize(dense, destination);
    }

    /// <summary>Subject first, because it is the shortest honest summary a message carries.</summary>
    public static string Compose(string? subject, string? bodyText) =>
        string.IsNullOrWhiteSpace(subject) ? bodyText ?? string.Empty
        : string.IsNullOrWhiteSpace(bodyText) ? subject
        : subject + "\n" + bodyText;

    private static string FingerprintOf(string weightsPath, string configPath, string vocabularyPath)
    {
        Span<byte> combined = stackalloc byte[96];
        HashInto(weightsPath, combined[..32]);
        HashInto(configPath, combined[32..64]);
        HashInto(vocabularyPath, combined[64..]);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(combined, digest);

        return Convert.ToHexStringLower(digest);
    }

    private static void HashInto(string path, Span<byte> destination)
    {
        using var stream = File.OpenRead(path);
        SHA256.HashData(stream, destination);
    }
}
