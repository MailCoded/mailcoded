namespace Mailcoded.Core.Embedding;

/// <summary>A BERT-family sentence encoder: token ids in, one mean-pooled unit vector out. One sequence
/// at a time and never padded, so there is no attention mask to get wrong.</summary>
public sealed class BertEncoder
{
    private readonly EncoderConfig _config;
    private readonly float[] _wordEmbeddings;
    private readonly float[] _positionEmbeddings;
    private readonly float[] _tokenTypeEmbeddings;
    private readonly float[] _embeddingGamma;
    private readonly float[] _embeddingBeta;
    private readonly Layer[] _layers;

    private BertEncoder(
        EncoderConfig config,
        float[] wordEmbeddings,
        float[] positionEmbeddings,
        float[] tokenTypeEmbeddings,
        float[] embeddingGamma,
        float[] embeddingBeta,
        Layer[] layers)
    {
        _config = config;
        _wordEmbeddings = wordEmbeddings;
        _positionEmbeddings = positionEmbeddings;
        _tokenTypeEmbeddings = tokenTypeEmbeddings;
        _embeddingGamma = embeddingGamma;
        _embeddingBeta = embeddingBeta;
        _layers = layers;
    }

    public int Dimensions => _config.Hidden;

    public int MaxPositions => _config.MaxPositions;

    public static BertEncoder Load(SafetensorsFile weights, EncoderConfig config)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        var prefix = Prefix(weights);
        var hidden = config.Hidden;

        var words = Read(weights, prefix + "embeddings.word_embeddings.weight", config.VocabularySize * hidden);
        var positions = Read(weights, prefix + "embeddings.position_embeddings.weight", config.MaxPositions * hidden);
        var types = Read(weights, prefix + "embeddings.token_type_embeddings.weight", -1);
        if (types.Length % hidden != 0 || types.Length == 0)
        {
            throw new InvalidDataException("The token-type embedding does not match the hidden size.");
        }

        var layers = new Layer[config.Layers];
        for (var i = 0; i < config.Layers; i++) layers[i] = Layer.Read(weights, $"{prefix}encoder.layer.{i}.", config);

        return new BertEncoder(
            config,
            words,
            positions,
            types,
            Read(weights, prefix + "embeddings.LayerNorm.weight", hidden),
            Read(weights, prefix + "embeddings.LayerNorm.bias", hidden),
            layers);
    }

    /// <summary>Encodes one sequence of token ids into a unit vector of <see cref="Dimensions"/>.</summary>
    public void Encode(ReadOnlySpan<int> tokenIds, Span<float> destination)
    {
        if (destination.Length != _config.Hidden)
        {
            throw new ArgumentException($"A {_config.Hidden}-dimension destination is needed.", nameof(destination));
        }
        if (tokenIds.Length == 0) throw new ArgumentException("A sequence needs at least one token.", nameof(tokenIds));
        if (tokenIds.Length > _config.MaxPositions)
        {
            throw new ArgumentException(
                $"{tokenIds.Length} tokens is beyond the {_config.MaxPositions} this model was trained for.",
                nameof(tokenIds));
        }

        var hidden = _config.Hidden;
        var tokens = tokenIds.Length;
        var states = new float[tokens * hidden];

        for (var t = 0; t < tokens; t++)
        {
            var id = tokenIds[t];
            if ((uint)id >= (uint)_config.VocabularySize)
            {
                throw new ArgumentException($"Token id {id} is outside the vocabulary.", nameof(tokenIds));
            }

            var state = states.AsSpan(t * hidden, hidden);
            _wordEmbeddings.AsSpan(id * hidden, hidden).CopyTo(state);
            Kernels.Add(state, _positionEmbeddings.AsSpan(t * hidden, hidden));
            Kernels.Add(state, _tokenTypeEmbeddings.AsSpan(0, hidden));
            Kernels.LayerNorm(state, _embeddingGamma, _embeddingBeta, _config.LayerNormEpsilon);
        }

        foreach (var layer in _layers) layer.Apply(states, tokens, _config);

        destination.Clear();
        for (var t = 0; t < tokens; t++) Kernels.Add(destination, states.AsSpan(t * hidden, hidden));
        Kernels.Scale(destination, 1f / tokens);
        Kernels.L2Normalize(destination);
    }

    /// <summary>Some exports nest everything under <c>bert.</c> and some do not.</summary>
    private static string Prefix(SafetensorsFile weights) =>
        weights.TryGet("embeddings.word_embeddings.weight", out _) ? string.Empty : "bert.";

    private static float[] Read(SafetensorsFile weights, string name, int expected)
    {
        var tensor = weights.Get(name);
        if (expected >= 0 && tensor.Count != expected)
        {
            throw new InvalidDataException(
                $"'{name}' holds {tensor.Count} values where the config implies {expected}.");
        }

        var values = new float[tensor.Count];
        weights.Read(tensor, values);
        return values;
    }

    private sealed record Layer
    {
        public required float[] QueryWeight { get; init; }

        public required float[] QueryBias { get; init; }

        public required float[] KeyWeight { get; init; }

        public required float[] KeyBias { get; init; }

        public required float[] ValueWeight { get; init; }

        public required float[] ValueBias { get; init; }

        public required float[] AttentionOutWeight { get; init; }

        public required float[] AttentionOutBias { get; init; }

        public required float[] AttentionGamma { get; init; }

        public required float[] AttentionBeta { get; init; }

        public required float[] IntermediateWeight { get; init; }

        public required float[] IntermediateBias { get; init; }

        public required float[] OutputWeight { get; init; }

        public required float[] OutputBias { get; init; }

        public required float[] OutputGamma { get; init; }

        public required float[] OutputBeta { get; init; }

        public static Layer Read(SafetensorsFile weights, string prefix, EncoderConfig config)
        {
            var hidden = config.Hidden;
            var square = hidden * hidden;

            return new Layer
            {
                QueryWeight = BertEncoder.Read(weights, prefix + "attention.self.query.weight", square),
                QueryBias = BertEncoder.Read(weights, prefix + "attention.self.query.bias", hidden),
                KeyWeight = BertEncoder.Read(weights, prefix + "attention.self.key.weight", square),
                KeyBias = BertEncoder.Read(weights, prefix + "attention.self.key.bias", hidden),
                ValueWeight = BertEncoder.Read(weights, prefix + "attention.self.value.weight", square),
                ValueBias = BertEncoder.Read(weights, prefix + "attention.self.value.bias", hidden),
                AttentionOutWeight = BertEncoder.Read(weights, prefix + "attention.output.dense.weight", square),
                AttentionOutBias = BertEncoder.Read(weights, prefix + "attention.output.dense.bias", hidden),
                AttentionGamma = BertEncoder.Read(weights, prefix + "attention.output.LayerNorm.weight", hidden),
                AttentionBeta = BertEncoder.Read(weights, prefix + "attention.output.LayerNorm.bias", hidden),
                IntermediateWeight = BertEncoder.Read(weights, prefix + "intermediate.dense.weight", config.Intermediate * hidden),
                IntermediateBias = BertEncoder.Read(weights, prefix + "intermediate.dense.bias", config.Intermediate),
                OutputWeight = BertEncoder.Read(weights, prefix + "output.dense.weight", hidden * config.Intermediate),
                OutputBias = BertEncoder.Read(weights, prefix + "output.dense.bias", hidden),
                OutputGamma = BertEncoder.Read(weights, prefix + "output.LayerNorm.weight", hidden),
                OutputBeta = BertEncoder.Read(weights, prefix + "output.LayerNorm.bias", hidden),
            };
        }

        public void Apply(float[] states, int tokens, EncoderConfig config)
        {
            var hidden = config.Hidden;
            var heads = config.Heads;
            var headSize = config.HeadSize;
            var scale = 1f / MathF.Sqrt(headSize);

            var queries = new float[tokens * hidden];
            var keys = new float[tokens * hidden];
            var values = new float[tokens * hidden];

            for (var t = 0; t < tokens; t++)
            {
                var state = states.AsSpan(t * hidden, hidden);
                Kernels.Linear(state, QueryWeight, QueryBias, queries.AsSpan(t * hidden, hidden));
                Kernels.Linear(state, KeyWeight, KeyBias, keys.AsSpan(t * hidden, hidden));
                Kernels.Linear(state, ValueWeight, ValueBias, values.AsSpan(t * hidden, hidden));
            }

            var context = new float[tokens * hidden];
            var scores = new float[tokens];

            for (var h = 0; h < heads; h++)
            {
                var offset = h * headSize;
                for (var t = 0; t < tokens; t++)
                {
                    var query = queries.AsSpan((t * hidden) + offset, headSize);
                    for (var s = 0; s < tokens; s++)
                    {
                        scores[s] = Kernels.Dot(query, keys.AsSpan((s * hidden) + offset, headSize)) * scale;
                    }

                    Kernels.Softmax(scores);

                    var target = context.AsSpan((t * hidden) + offset, headSize);
                    for (var s = 0; s < tokens; s++)
                    {
                        var weight = scores[s];
                        var source = values.AsSpan((s * hidden) + offset, headSize);
                        for (var i = 0; i < headSize; i++) target[i] += weight * source[i];
                    }
                }
            }

            var projected = new float[hidden];
            var intermediate = new float[config.Intermediate];

            for (var t = 0; t < tokens; t++)
            {
                var state = states.AsSpan(t * hidden, hidden);

                Kernels.Linear(context.AsSpan(t * hidden, hidden), AttentionOutWeight, AttentionOutBias, projected);
                Kernels.Add(state, projected);
                Kernels.LayerNorm(state, AttentionGamma, AttentionBeta, config.LayerNormEpsilon);

                Kernels.Linear(state, IntermediateWeight, IntermediateBias, intermediate);
                Kernels.Gelu(intermediate);
                Kernels.Linear(intermediate, OutputWeight, OutputBias, projected);
                Kernels.Add(state, projected);
                Kernels.LayerNorm(state, OutputGamma, OutputBeta, config.LayerNormEpsilon);
            }
        }
    }
}
