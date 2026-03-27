using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Tests for ModelConfigFetcher.ParseConfig — the internal static parser
/// is tested directly without needing an HTTP connection.
/// </summary>
[TestFixture]
public class ModelConfigFetcherTests
{
    // ── Happy paths ───────────────────────────────────────────────────────────

    [Test]
    public void ParseConfig_FullGqaModel_ParsesAllFields()
    {
        // Llama-3.1-8B-Instruct style: GQA with 32 Q heads, 8 KV heads
        const string json = """
        {
            "num_hidden_layers": 32,
            "num_attention_heads": 32,
            "num_key_value_heads": 8,
            "hidden_size": 4096,
            "max_position_embeddings": 131072
        }
        """;

        var config = ModelConfigFetcher.ParseConfig("model/test", json);

        config.Should().NotBeNull();
        config!.NumHiddenLayers.Should().Be(32);
        config.NumKvHeads.Should().Be(8);
        config.HeadDim.Should().Be(128);   // 4096 / 32
        config.MaxPositionEmbeddings.Should().Be(131072);
        config.ModelId.Should().Be("model/test");
    }

    [Test]
    public void ParseConfig_NoKvHeadsField_FallsBackToAttentionHeads()
    {
        // MHA model: no GQA, num_key_value_heads absent → use num_attention_heads
        const string json = """
        {
            "num_hidden_layers": 24,
            "num_attention_heads": 16,
            "hidden_size": 2048,
            "max_position_embeddings": 4096
        }
        """;

        var config = ModelConfigFetcher.ParseConfig("model/mha", json);

        config.Should().NotBeNull();
        config!.NumKvHeads.Should().Be(16);
        config.HeadDim.Should().Be(128);   // 2048 / 16
    }

    [Test]
    public void ParseConfig_HeadDim_CalculatedFromHiddenSizeAndAttentionHeads()
    {
        // Mistral-7B: hidden_size=4096, num_attention_heads=32 → head_dim=128
        const string json = """
        {
            "num_hidden_layers": 32,
            "num_attention_heads": 32,
            "num_key_value_heads": 8,
            "hidden_size": 4096,
            "max_position_embeddings": 32768
        }
        """;

        var config = ModelConfigFetcher.ParseConfig("mistral/m", json);

        config!.HeadDim.Should().Be(128);
    }

    [Test]
    public void ParseConfig_LargeModel_HandlesLargeMaxPositionEmbeddings()
    {
        const string json = """
        {
            "num_hidden_layers": 80,
            "num_attention_heads": 64,
            "num_key_value_heads": 8,
            "hidden_size": 8192,
            "max_position_embeddings": 1048576
        }
        """;

        var config = ModelConfigFetcher.ParseConfig("model/large", json);

        config.Should().NotBeNull();
        config!.MaxPositionEmbeddings.Should().Be(1048576);
        config.HeadDim.Should().Be(128);  // 8192 / 64
    }

    [Test]
    public void ParseConfig_ExtraFieldsIgnored_StillParses()
    {
        const string json = """
        {
            "model_type": "llama",
            "architectures": ["LlamaForCausalLM"],
            "num_hidden_layers": 32,
            "num_attention_heads": 32,
            "hidden_size": 4096,
            "max_position_embeddings": 131072,
            "vocab_size": 128256,
            "some_unknown_field": true
        }
        """;

        var config = ModelConfigFetcher.ParseConfig("model/extra", json);

        config.Should().NotBeNull();
    }

    // ── Missing required fields → null ────────────────────────────────────────

    [Test]
    public void ParseConfig_MissingNumHiddenLayers_ReturnsNull()
    {
        const string json = """
        {
            "num_attention_heads": 32,
            "hidden_size": 4096,
            "max_position_embeddings": 131072
        }
        """;

        ModelConfigFetcher.ParseConfig("model/m", json).Should().BeNull();
    }

    [Test]
    public void ParseConfig_MissingHiddenSize_ReturnsNull()
    {
        const string json = """
        {
            "num_hidden_layers": 32,
            "num_attention_heads": 32,
            "max_position_embeddings": 131072
        }
        """;

        ModelConfigFetcher.ParseConfig("model/m", json).Should().BeNull();
    }

    [Test]
    public void ParseConfig_MissingMaxPositionEmbeddings_ReturnsNull()
    {
        const string json = """
        {
            "num_hidden_layers": 32,
            "num_attention_heads": 32,
            "hidden_size": 4096
        }
        """;

        ModelConfigFetcher.ParseConfig("model/m", json).Should().BeNull();
    }

    [Test]
    public void ParseConfig_InvalidJson_ReturnsNull()
    {
        ModelConfigFetcher.ParseConfig("model/m", "not json at all").Should().BeNull();
    }

    [Test]
    public void ParseConfig_EmptyJson_ReturnsNull()
    {
        ModelConfigFetcher.ParseConfig("model/m", "{}").Should().BeNull();
    }

    [Test]
    public void ParseConfig_ZeroAttentionHeads_ReturnsNull()
    {
        // Division by zero guard: num_attention_heads = 0 should be rejected
        const string json = """
        {
            "num_hidden_layers": 32,
            "num_attention_heads": 0,
            "hidden_size": 4096,
            "max_position_embeddings": 131072
        }
        """;

        ModelConfigFetcher.ParseConfig("model/m", json).Should().BeNull();
    }
}
