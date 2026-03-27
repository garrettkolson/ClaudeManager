using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class MaxContextEstimatorTests
{
    // Llama-3.1-8B-Instruct: 32 layers, 8 KV heads, head_dim=128, native ctx=131072
    private static readonly ModelArchConfig Llama8B = new(
        ModelId:               "meta-llama/Llama-3.1-8B-Instruct",
        NumHiddenLayers:       32,
        NumKvHeads:            8,
        HeadDim:               128,
        MaxPositionEmbeddings: 131072);

    // Llama-3.1-70B-Instruct: 80 layers, 8 KV heads, head_dim=128, native ctx=131072
    private static readonly ModelArchConfig Llama70B = new(
        ModelId:               "meta-llama/Llama-3.1-70B-Instruct",
        NumHiddenLayers:       80,
        NumKvHeads:            8,
        HeadDim:               128,
        MaxPositionEmbeddings: 131072);

    // ── Estimate ──────────────────────────────────────────────────────────────

    [Test]
    public void Estimate_InsufficientVram_ReturnsNull()
    {
        // 8B fp16 weights ~20 GB, only 16 GB available → no budget
        var result = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 16, paramsBillions: 8, quantization: "none");

        result.Should().BeNull();
    }

    [Test]
    public void Estimate_SufficientVram_ReturnsPositiveValue()
    {
        var result = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 80, paramsBillions: 8, quantization: "none");

        result.Should().BeGreaterThan(0);
    }

    [Test]
    public void Estimate_CapsAtNativeMax()
    {
        // Enormous VRAM — result must be capped at max_position_embeddings
        var result = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 10000, paramsBillions: 8, quantization: "none");

        result.Should().Be(Llama8B.MaxPositionEmbeddings);
    }

    [Test]
    public void Estimate_AwqQuantization_HasMoreBudget()
    {
        // AWQ uses less VRAM for weights → more budget for KV cache → higher context
        var fp16   = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 24, paramsBillions: 8, quantization: "none");
        var awq    = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 24, paramsBillions: 8, quantization: "awq");

        // fp16 8B on 24 GB may be null (weights ~20 GB), AWQ should succeed
        awq.Should().NotBeNull();
        if (fp16.HasValue)
            awq!.Value.Should().BeGreaterThan(fp16.Value);
    }

    [Test]
    public void Estimate_MultiGpu_ScalesWithTotalVram()
    {
        // 8B fp16 weights ~20 GB; test single 24 GB vs dual 48 GB
        var single = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 24, paramsBillions: 8, quantization: "none");
        var dual   = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 48, paramsBillions: 8, quantization: "none");

        dual.Should().NotBeNull();
        if (single.HasValue)
            dual!.Value.Should().BeGreaterThan(single.Value);
    }

    [Test]
    public void Estimate_ZeroVramBudget_ReturnsNull()
    {
        var result = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 0, paramsBillions: 8, quantization: "none");

        result.Should().BeNull();
    }

    [Test]
    public void Estimate_KvBytesPerToken_Formula()
    {
        // kvBytesPerToken = 2 * layers * kvHeads * headDim * 2 bytes (fp16)
        // For Llama8B: 2 * 32 * 8 * 128 * 2 = 131072 bytes/token
        // kvBudget: (80 * 0.90 - 20) GB = (72 - 20) = 52 GB = 55834574848 bytes
        // maxFromVram = 55834574848 / 131072 ≈ 426134 tokens → capped at 131072
        var result = MaxContextEstimator.Estimate(Llama8B, totalVramGb: 80, paramsBillions: 8, quantization: "none");

        result.Should().Be(131072);
    }

    // ── FormatTokens ──────────────────────────────────────────────────────────

    [Test]
    public void FormatTokens_LessThan1024_ShowsRawCount()
    {
        MaxContextEstimator.FormatTokens(512).Should().Be("~512 tokens");
    }

    [Test]
    public void FormatTokens_Exactly1024_ShowsKFormat()
    {
        MaxContextEstimator.FormatTokens(1024).Should().Be("~1K tokens");
    }

    [Test]
    public void FormatTokens_128K_ShowsExpectedString()
    {
        MaxContextEstimator.FormatTokens(131072).Should().Be("~128K tokens");
    }

    [Test]
    public void FormatTokens_32K_ShowsExpectedString()
    {
        MaxContextEstimator.FormatTokens(32768).Should().Be("~32K tokens");
    }
}
