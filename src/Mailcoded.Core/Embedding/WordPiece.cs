using System.Globalization;
using System.Text;

namespace Mailcoded.Core.Embedding;

/// <summary>A BERT vocabulary: one token per line, the line number being the id.</summary>
public sealed class WordPieceVocabulary
{
    public const string ClassifierToken = "[CLS]";
    public const string SeparatorToken = "[SEP]";
    public const string PaddingToken = "[PAD]";
    public const string UnknownToken = "[UNK]";

    private const int MaxTokens = 1_000_000;

    private readonly Dictionary<string, int> _ids;

    private WordPieceVocabulary(Dictionary<string, int> ids, int classifier, int separator, int padding, int unknown)
    {
        _ids = ids;
        Classifier = classifier;
        Separator = separator;
        Padding = padding;
        Unknown = unknown;
    }

    public int Count => _ids.Count;

    public int Classifier { get; }

    public int Separator { get; }

    public int Padding { get; }

    public int Unknown { get; }

    public static WordPieceVocabulary Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path, Encoding.UTF8);

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        while (reader.ReadLine() is { } line)
        {
            if (index >= MaxTokens) throw new InvalidDataException($"A vocabulary of more than {MaxTokens} tokens is not read.");

            // A duplicate keeps its first id, which is what the reference tokenizer does.
            ids.TryAdd(line.Trim(), index);
            index++;
        }

        return FromTokens(ids);
    }

    public static WordPieceVocabulary FromTokens(IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var token in tokens)
        {
            ids.TryAdd(token, index);
            index++;
        }

        return FromTokens(ids);
    }

    internal Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> Lookup =>
        _ids.GetAlternateLookup<ReadOnlySpan<char>>();

    private static WordPieceVocabulary FromTokens(Dictionary<string, int> ids)
    {
        if (ids.Count == 0) throw new InvalidDataException("The vocabulary is empty.");

        return new WordPieceVocabulary(
            ids,
            Required(ids, ClassifierToken),
            Required(ids, SeparatorToken),
            Required(ids, PaddingToken),
            Required(ids, UnknownToken));
    }

    private static int Required(Dictionary<string, int> ids, string token) =>
        ids.TryGetValue(token, out var id)
            ? id
            : throw new InvalidDataException($"The vocabulary has no '{token}'; it is not a BERT vocabulary.");
}

/// <summary>The BERT tokenizer: clean, split, lower-case, strip accents, then greedy longest-match
/// WordPiece. A word longer than the per-word cap becomes one unknown rather than many pieces.</summary>
public sealed class WordPieceTokenizer
{
    public const int DefaultMaxTokens = 256;

    private const int MaxCharsPerWord = 100;
    private const string ContinuationPrefix = "##";

    private readonly WordPieceVocabulary _vocabulary;
    private readonly bool _lowercase;

    public WordPieceTokenizer(WordPieceVocabulary vocabulary, bool lowercase = true, int maxTokens = DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 2);

        _vocabulary = vocabulary;
        _lowercase = lowercase;
        MaxTokens = maxTokens;
    }

    public int MaxTokens { get; }

    /// <summary>Writes ids into <paramref name="ids"/> and returns how many, always opening with the
    /// classifier token and closing with the separator.</summary>
    public int Tokenize(string text, Span<int> ids)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (ids.Length < MaxTokens) throw new ArgumentException($"Room for {MaxTokens} ids is needed.", nameof(ids));

        var lookup = _vocabulary.Lookup;
        var count = 0;
        ids[count++] = _vocabulary.Classifier;

        foreach (var word in Words(Clip(text)))
        {
            if (count >= MaxTokens - 1) break;
            count = AppendPieces(word, lookup, ids, count);
        }

        ids[count++] = _vocabulary.Separator;
        return count;
    }

    public int[] Tokenize(string text)
    {
        var ids = new int[MaxTokens];
        var count = Tokenize(text, ids);
        return ids[..count];
    }

    private int AppendPieces(
        string word,
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup,
        Span<int> ids,
        int count)
    {
        if (word.Length > MaxCharsPerWord)
        {
            ids[count++] = _vocabulary.Unknown;
            return count;
        }

        Span<char> piece = stackalloc char[MaxCharsPerWord + ContinuationPrefix.Length];
        var start = 0;
        var pieces = new List<int>(4);

        while (start < word.Length)
        {
            var end = word.Length;
            var matched = -1;
            while (end > start)
            {
                var length = Write(piece, word.AsSpan(start, end - start), start > 0);
                if (lookup.TryGetValue(piece[..length], out var id))
                {
                    matched = id;
                    break;
                }

                end--;
            }

            if (matched < 0)
            {
                ids[count++] = _vocabulary.Unknown;
                return count;
            }

            pieces.Add(matched);
            start = end;
        }

        foreach (var id in pieces)
        {
            if (count >= MaxTokens - 1) break;
            ids[count++] = id;
        }

        return count;
    }

    private static int Write(Span<char> destination, ReadOnlySpan<char> source, bool continuation)
    {
        var length = 0;
        if (continuation)
        {
            ContinuationPrefix.AsSpan().CopyTo(destination);
            length = ContinuationPrefix.Length;
        }

        source.CopyTo(destination[length..]);
        return length + source.Length;
    }

    /// <summary>Each whitespace-separated word yields at least one token, so no word past the token
    /// budget can reach it. Dropping them here is what keeps a megabyte-long body from being
    /// lowercased, normalized and stripped in full before all but 256 ids are discarded.</summary>
    private string Clip(string text)
    {
        if (text.Length <= MaxTokens) return text;

        var ceiling = MaxTokens * MaxCharsPerWord;
        var clipped = new StringBuilder(Math.Min(text.Length, ceiling));
        var words = 0;
        var inWord = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                if (words == MaxTokens) break;
                if (clipped.Length > 0) clipped.Append(' ');
                words++;
                inWord = true;
            }

            if (clipped.Length >= ceiling) break;
            clipped.Append(rune.ToString());
        }

        return clipped.ToString();
    }

    private IEnumerable<string> Words(string text)
    {
        var builder = new StringBuilder();
        var words = new List<string>();

        void Flush()
        {
            if (builder.Length == 0) return;
            words.Add(builder.ToString());
            builder.Clear();
        }

        foreach (var rune in Prepare(text).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                Flush();
                continue;
            }

            // Punctuation and CJK are their own tokens, which is what makes '[CLS]' and a Chinese
            // sentence tokenize the way the reference implementation does.
            if (IsPunctuation(rune) || IsCjk(rune))
            {
                Flush();
                words.Add(rune.ToString());
                continue;
            }

            builder.Append(rune.ToString());
        }

        Flush();
        return words;
    }

    private string Prepare(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is 0 or 0xFFFD) continue;
            cleaned.Append(Rune.IsControl(rune) ? ' ' : rune.ToString());
        }

        var result = cleaned.ToString();
        if (!_lowercase) return result;

        result = result.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(result.Length);
        foreach (var character in result)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(character);
            }
        }

        return stripped.ToString();
    }

    private static bool IsPunctuation(Rune rune)
    {
        var value = rune.Value;
        if (value is (>= 33 and <= 47) or (>= 58 and <= 64) or (>= 91 and <= 96) or (>= 123 and <= 126))
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsCjk(Rune rune) => rune.Value is
        (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x20000 and <= 0x2A6DF)
        or (>= 0x2A700 and <= 0x2B73F) or (>= 0x2B740 and <= 0x2B81F) or (>= 0x2B820 and <= 0x2CEAF)
        or (>= 0xF900 and <= 0xFAFF) or (>= 0x2F800 and <= 0x2FA1F);
}
