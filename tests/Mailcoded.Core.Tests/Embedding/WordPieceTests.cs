using Mailcoded.Core.Embedding;
using Xunit;

namespace Mailcoded.Core.Tests.Embedding;

public sealed class WordPieceTests
{
    private static readonly string[] Tokens =
    [
        "[PAD]", "[UNK]", "[CLS]", "[SEP]",
        "quarterly", "report", "invoice", "the", "roof",
        "un", "##aff", "##able", "##s", "##ing",
        "e", "##t", "##e", "café",
        ".", ",", "!", "-", "中", "文",
    ];

    private static WordPieceTokenizer Tokenizer(bool lowercase = true, int maxTokens = 256) =>
        new(WordPieceVocabulary.FromTokens(Tokens), lowercase, maxTokens);

    private static string[] Pieces(WordPieceTokenizer tokenizer, string text)
    {
        var names = new string[tokenizer.Tokenize(text).Length];
        var ids = tokenizer.Tokenize(text);
        for (var i = 0; i < ids.Length; i++) names[i] = Tokens[ids[i]];
        return names;
    }

    [Fact]
    public void Wraps_every_sequence_in_the_classifier_and_separator()
    {
        Assert.Equal(["[CLS]", "quarterly", "report", "[SEP]"], Pieces(Tokenizer(), "quarterly report"));
    }

    [Fact]
    public void An_empty_input_is_still_a_valid_two_token_sequence()
    {
        Assert.Equal(["[CLS]", "[SEP]"], Pieces(Tokenizer(), "   "));
    }

    [Fact]
    public void Splits_a_word_into_the_longest_pieces_the_vocabulary_has()
    {
        Assert.Equal(["[CLS]", "un", "##aff", "##able", "[SEP]"], Pieces(Tokenizer(), "unaffable"));
    }

    [Fact]
    public void A_word_with_no_covering_pieces_becomes_one_unknown()
    {
        Assert.Equal(["[CLS]", "[UNK]", "[SEP]"], Pieces(Tokenizer(), "zzzz"));
    }

    [Fact]
    public void Punctuation_is_its_own_token()
    {
        Assert.Equal(["[CLS]", "invoice", ",", "the", "roof", "!", "[SEP]"], Pieces(Tokenizer(), "invoice, the roof!"));
    }

    [Fact]
    public void Lower_cases_and_strips_accents_for_an_uncased_vocabulary()
    {
        Assert.Equal(["[CLS]", "e", "##t", "##e", "[SEP]"], Pieces(Tokenizer(), "ÉTÉ"));
    }

    [Fact]
    public void Leaves_case_and_accents_alone_when_told_to()
    {
        Assert.Equal(["[CLS]", "café", "[SEP]"], Pieces(Tokenizer(lowercase: false), "café"));
    }

    /// <summary>Each CJK character is its own token, which is what makes a Chinese sentence tokenize
    /// at all against a vocabulary that holds single characters.</summary>
    [Fact]
    public void Every_cjk_character_is_its_own_token()
    {
        Assert.Equal(["[CLS]", "中", "文", "[SEP]"], Pieces(Tokenizer(), "中文"));
    }

    [Fact]
    public void Control_characters_become_separators_rather_than_part_of_a_word()
    {
        var text = "invoice" + (char)0x1b + "roof";

        var pieces = Pieces(Tokenizer(), text);

        Assert.Equal(["[CLS]", "invoice", "roof", "[SEP]"], pieces);
        Assert.DoesNotContain(pieces, piece => piece.Contains((char)0x1b));
    }

    [Fact]
    public void Truncates_to_the_token_budget_and_still_closes_the_sequence()
    {
        var tokenizer = Tokenizer(maxTokens: 6);
        var text = string.Join(' ', Enumerable.Repeat("invoice", 50));

        var ids = tokenizer.Tokenize(text);

        Assert.Equal(6, ids.Length);
        Assert.Equal("[CLS]", Tokens[ids[0]]);
        Assert.Equal("[SEP]", Tokens[ids[^1]]);
    }

    [Fact]
    public void A_word_longer_than_the_per_word_cap_is_one_unknown()
    {
        var pieces = Pieces(Tokenizer(), new string('a', 200));

        Assert.Equal(["[CLS]", "[UNK]", "[SEP]"], pieces);
    }

    [Fact]
    public void Refuses_a_destination_too_small_for_the_budget()
    {
        var tokenizer = Tokenizer(maxTokens: 16);

        Assert.Throws<ArgumentException>(() => tokenizer.Tokenize("invoice", new int[4]));
    }

    [Fact]
    public void Refuses_a_vocabulary_without_the_special_tokens()
    {
        Assert.Throws<InvalidDataException>(() => WordPieceVocabulary.FromTokens(["a", "b"]));
        Assert.Throws<InvalidDataException>(() => WordPieceVocabulary.FromTokens([]));
    }

    [Fact]
    public void Reports_the_ids_of_the_special_tokens_it_found()
    {
        var vocabulary = WordPieceVocabulary.FromTokens(Tokens);

        Assert.Equal(0, vocabulary.Padding);
        Assert.Equal(1, vocabulary.Unknown);
        Assert.Equal(2, vocabulary.Classifier);
        Assert.Equal(3, vocabulary.Separator);
        Assert.Equal(Tokens.Length, vocabulary.Count);
    }

    [Fact]
    public void Tokenizing_the_same_text_twice_gives_the_same_ids()
    {
        var tokenizer = Tokenizer();
        var text = "The quarterly report, unaffable — 中文 café!";

        Assert.Equal(tokenizer.Tokenize(text), tokenizer.Tokenize(text));
    }

    /// <summary>A body arrives as attacker-chosen length. Normalizing all of it to keep 256 ids would
    /// allocate several copies of a megabyte, so only the words that can reach the budget are kept.</summary>
    [Fact]
    public void Fills_its_budget_from_a_body_far_longer_than_the_budget()
    {
        var tokenizer = Tokenizer();
        var body = string.Join(' ', Enumerable.Repeat("quarterly report invoice", 200_000));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var ids = tokenizer.Tokenize(body);
        elapsed.Stop();

        Assert.Equal(tokenizer.MaxTokens, ids.Length);
        Assert.True(elapsed.ElapsedMilliseconds < 500, $"tokenizing took {elapsed.ElapsedMilliseconds} ms");
    }

    /// <summary>Whitespace is collapsed while clipping, so a body padded with blank lines — which is
    /// what stripping HTML tends to leave behind — still reaches real words.</summary>
    [Fact]
    public void Reaches_words_buried_behind_a_wall_of_whitespace()
    {
        var tokenizer = Tokenizer();
        var body = new string('\n', 100_000) + "quarterly report";

        Assert.Equal(["[CLS]", "quarterly", "report", "[SEP]"], Pieces(tokenizer, body));
    }

    [Fact]
    public void Clipping_leaves_a_short_body_exactly_as_it_was()
    {
        var tokenizer = Tokenizer();

        Assert.Equal(["[CLS]", "the", "quarterly", "report", "[SEP]"], Pieces(tokenizer, "the  quarterly\n\nreport"));
    }
}
