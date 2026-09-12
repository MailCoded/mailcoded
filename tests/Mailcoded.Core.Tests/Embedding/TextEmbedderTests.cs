using Mailcoded.Core.Embedding;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Embedding;

public sealed class TextEmbedderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mailcoded-model-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Loads_a_model_directory_and_reports_its_shape()
    {
        var embedder = TextEmbedder.Load(TinyModel.Write(_directory));

        Assert.Equal(TinyModel.Dimensions, embedder.Dimensions);
        Assert.Equal("mean", embedder.Pooling);
        Assert.Equal(TinyModel.Config.MaxPositions, embedder.MaxTokens);
        Assert.Equal(64, embedder.Fingerprint.Length);
    }

    [Fact]
    public void Reports_whether_a_model_is_installed()
    {
        Assert.False(TextEmbedder.IsInstalledAt(_directory));
        Assert.False(TextEmbedder.IsInstalledAt(null));

        TinyModel.Write(_directory);
        Assert.True(TextEmbedder.IsInstalledAt(_directory));

        File.Delete(Path.Combine(_directory, TextEmbedder.VocabularyFileName));
        Assert.False(TextEmbedder.IsInstalledAt(_directory));
        Assert.Throws<InvalidDataException>(() => TextEmbedder.Load(_directory));
    }

    /// <summary>The fingerprint is what keeps two models' vectors apart, so it must move when any of
    /// the three files does — the weights alone are not the model.</summary>
    [Fact]
    public void Fingerprints_every_file_the_model_is_made_of()
    {
        var original = TextEmbedder.Load(TinyModel.Write(_directory)).Fingerprint;

        Assert.Equal(original, TextEmbedder.Load(TinyModel.Write(_directory)).Fingerprint);

        var vocabulary = Path.Combine(_directory, TextEmbedder.VocabularyFileName);
        File.WriteAllLines(vocabulary, [.. TinyModel.Vocabulary[..^1], "different"]);
        Assert.NotEqual(original, TextEmbedder.Load(_directory).Fingerprint);

        File.WriteAllLines(vocabulary, TinyModel.Vocabulary);
        TinyModel.Write(_directory, seed: 99);
        Assert.NotEqual(original, TextEmbedder.Load(_directory).Fingerprint);
    }

    [Fact]
    public void Refuses_a_vocabulary_wider_than_the_model()
    {
        TinyModel.Write(_directory);
        File.WriteAllLines(
            Path.Combine(_directory, TextEmbedder.VocabularyFileName),
            [.. TinyModel.Vocabulary, "one", "too", "many"]);

        var thrown = Assert.Throws<InvalidDataException>(() => TextEmbedder.Load(_directory));
        Assert.Contains("was built for", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quantizes_to_a_vector_that_still_points_the_same_way()
    {
        var embedder = TextEmbedder.Load(TinyModel.Write(_directory));

        var dense = new float[embedder.Dimensions];
        embedder.Embed("the quarterly report", dense);

        var quantized = new sbyte[embedder.Dimensions];
        var scale = embedder.Embed("the quarterly report", quantized);

        var reconstructed = new float[embedder.Dimensions];
        for (var i = 0; i < reconstructed.Length; i++) reconstructed[i] = quantized[i] * scale;

        Assert.True(Kernels.Dot(dense, reconstructed) > 0.99f, "int8 lost the direction of the vector");
    }

    [Fact]
    public void Composes_a_subject_and_a_body_without_losing_either()
    {
        Assert.Equal("Roof repair\nthe roof leaks", TextEmbedder.Compose("Roof repair", "the roof leaks"));
        Assert.Equal("Roof repair", TextEmbedder.Compose("Roof repair", null));
        Assert.Equal("Roof repair", TextEmbedder.Compose("Roof repair", "   "));
        Assert.Equal("the roof leaks", TextEmbedder.Compose(null, "the roof leaks"));
        Assert.Equal(string.Empty, TextEmbedder.Compose(null, null));
    }

    public static TheoryData<string, string> HostileBodies() => new()
    {
        { "nothing at all", "" },
        { "only whitespace", " \t\r\n   " },
        { "a lone high surrogate", "roof \ud800 leak" },
        { "a lone low surrogate", "roof \udc00 leak" },
        { "a reversed surrogate pair", "roof \udc00\ud800 leak" },
        { "the replacement character", "roof \ufffd leak" },
        { "a null and an escape", "roof \u0000 \u001b[31m leak" },
        { "only punctuation", "!!! ... --- ???" },
        { "combining marks without a base", "\u0301\u0302\u0303" },
        { "right-to-left overrides", "roof \u202E kael \u202C leak" },
        { "zero-width joiners", "ro\u200dof le\u200dak" },
        { "unassigned planes", "roof \U000e0041 leak" },
        { "a single character", "a" },
        { "nothing but newlines", "\n\n\n\n\n" },
    };

    /// <summary>A body is attacker-chosen text. Refusing to embed one is acceptable; throwing out of
    /// the backfill worker, which would stop every other message being embedded, is not.</summary>
    [Theory]
    [MemberData(nameof(HostileBodies))]
    public void Embeds_hostile_text_without_throwing(string because, string body)
    {
        var embedder = TextEmbedder.Load(TinyModel.Write(_directory));
        var quantized = new sbyte[embedder.Dimensions];

        var scale = embedder.Embed(TextEmbedder.Compose("Roof repair", body), quantized);

        Assert.True(float.IsFinite(scale), $"{because} produced a scale of {scale}");
        Assert.True(scale >= 0f, $"{because} produced a negative scale");
    }

    [Fact]
    public void Embeds_a_body_far_longer_than_the_token_budget_without_throwing()
    {
        var embedder = TextEmbedder.Load(TinyModel.Write(_directory));
        var quantized = new sbyte[embedder.Dimensions];
        var body = string.Join(' ', Enumerable.Repeat("the quarterly report invoice roof", 100_000));

        var scale = embedder.Embed(body, quantized);

        Assert.True(float.IsFinite(scale));
    }
}
