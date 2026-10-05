namespace Mailcoded.Core.Domain.Identities;

public sealed record NamedSignature(string Name, string Text);

public enum SignatureUse
{
    New,
    Reply,
}

public enum SignatureChoiceKind
{
    None,
    Default,
    Named,
}

/// <summary>Which signature a draft asks for. The default value asks for none, so existing callers are unchanged.</summary>
public readonly record struct SignatureChoice(SignatureChoiceKind Kind, string? Name = null)
{
    public static SignatureChoice None => default;

    public static SignatureChoice UseDefault => new(SignatureChoiceKind.Default);

    public static SignatureChoice Named(string name) => new(SignatureChoiceKind.Named, name);
}

/// <summary>An Account's named signatures with its defaults for new messages and for replies. Immutable.</summary>
public sealed record SignatureBook
{
    public const int MaxSignatures = 16;

    public static readonly SignatureBook Empty = new();

    public IReadOnlyList<NamedSignature> Items { get; init; } = [];

    public string? DefaultForNew { get; init; }

    public string? DefaultForReply { get; init; }

    public bool IsEmpty => Items.Count == 0;

    public NamedSignature? Find(string? name)
    {
        if (!SignatureText.TryNormalizeName(name, out var normalized)) return null;

        foreach (var item in Items)
            if (item.Name == normalized) return item;

        return null;
    }

    public string? DefaultName(SignatureUse use) => use == SignatureUse.Reply ? DefaultForReply : DefaultForNew;

    public SignatureBook Set(string? name, string? text)
    {
        if (!SignatureText.TryNormalizeName(name, out var normalized))
        {
            throw new ArgumentException(
                $"A signature name is 1 to {SignatureText.MaxNameLength} characters of letters, digits, '.', '_' and '-'.");
        }

        if (!SignatureText.TryNormalize(text, out var clean, out var error)) throw new ArgumentException(error);

        var items = new List<NamedSignature>(Items.Count + 1);
        var replaced = false;
        foreach (var item in Items)
        {
            if (item.Name == normalized)
            {
                items.Add(new NamedSignature(normalized, clean));
                replaced = true;
            }
            else
            {
                items.Add(item);
            }
        }

        if (!replaced)
        {
            if (items.Count >= MaxSignatures)
                throw new ArgumentException($"An account can have at most {MaxSignatures} signatures.");

            items.Add(new NamedSignature(normalized, clean));
        }

        return this with { Items = items };
    }

    /// <summary>Drops the signature and any default that pointed at it.</summary>
    public SignatureBook Forget(string? name, out bool removed)
    {
        removed = false;
        if (!SignatureText.TryNormalizeName(name, out var normalized)) return this;

        var items = new List<NamedSignature>(Items.Count);
        foreach (var item in Items)
            if (item.Name != normalized) items.Add(item);

        removed = items.Count != Items.Count;
        if (!removed) return this;

        return this with
        {
            Items = items,
            DefaultForNew = DefaultForNew == normalized ? null : DefaultForNew,
            DefaultForReply = DefaultForReply == normalized ? null : DefaultForReply,
        };
    }

    /// <summary>A null name clears the default. A name that matches no signature is an error.</summary>
    public SignatureBook WithDefault(SignatureUse use, string? name)
    {
        string? chosen = null;

        if (name is not null)
        {
            chosen = (Find(name) ?? throw new ArgumentException("There is no signature with that name.")).Name;
        }

        return use == SignatureUse.Reply ? this with { DefaultForReply = chosen } : this with { DefaultForNew = chosen };
    }

    /// <summary>The signature a draft asks for, or null for none. An unknown name is an error; an unset default is not.</summary>
    public NamedSignature? Select(SignatureChoice choice, SignatureUse use)
    {
        switch (choice.Kind)
        {
            case SignatureChoiceKind.Default:
                return Find(DefaultName(use));

            case SignatureChoiceKind.Named:
                return Find(choice.Name) ?? throw new ArgumentException("There is no signature with that name.");

            default:
                return null;
        }
    }
}
