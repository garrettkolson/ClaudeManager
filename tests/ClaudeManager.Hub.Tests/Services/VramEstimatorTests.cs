using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class VramEstimatorTests
{
    // ── EstimateGb ─────────────────────────────────────────────────────────────

    [Test]
    public void EstimateGb_Fp16_UsesCorrectFormula()
    {
        // 7B params × 2 bytes (fp16) × 1.25 overhead ÷ (1024^3) ≈ 16.3 GB
        var gb = VramEstimator.EstimateGb(7.0, "none");

        gb.Should().BeApproximately(7.0 * 2.0 * 1.25, precision: 0.01);
    }

    [Test]
    public void EstimateGb_Awq_UsesHalfBytesPerParam()
    {
        // AWQ ≈ 4-bit = 0.5 bytes × 1.25 overhead
        var gb = VramEstimator.EstimateGb(7.0, "awq");

        gb.Should().BeApproximately(7.0 * 0.5 * 1.25, precision: 0.01);
    }

    [Test]
    public void EstimateGb_Gptq_SameAsAwq()
    {
        var awqGb  = VramEstimator.EstimateGb(13.0, "awq");
        var gptqGb = VramEstimator.EstimateGb(13.0, "gptq");

        awqGb.Should().BeApproximately(gptqGb, precision: 0.001);
    }

    [Test]
    public void EstimateGb_UnknownQuantization_TreatedAsFp16()
    {
        var fp16Gb    = VramEstimator.EstimateGb(7.0, "none");
        var unknownGb = VramEstimator.EstimateGb(7.0, "bfloat16");

        unknownGb.Should().BeApproximately(fp16Gb, precision: 0.001);
    }

    [Test]
    public void EstimateGb_AwqIsRoughlyHalfOfFp16()
    {
        var fp16Gb = VramEstimator.EstimateGb(70.0, "none");
        var awqGb  = VramEstimator.EstimateGb(70.0, "awq");

        awqGb.Should().BeApproximately(fp16Gb / 4.0, precision: 0.1);
    }

    [Test]
    public void EstimateGb_LargerModelRequiresMoreVram()
    {
        var small = VramEstimator.EstimateGb(7.0,  "none");
        var large = VramEstimator.EstimateGb(70.0, "none");

        large.Should().BeGreaterThan(small);
        large.Should().BeApproximately(small * 10, precision: 0.01);
    }

    // ── FormatEstimate ─────────────────────────────────────────────────────────

    [Test]
    public void FormatEstimate_ValidParams_ReturnsFormattedString()
    {
        var result = VramEstimator.FormatEstimate(7.0, "none");

        result.Should().NotBeNull();
        result.Should().StartWith("~");
        result.Should().Contain("GB per GPU");
    }

    [Test]
    public void FormatEstimate_NullParams_ReturnsNull()
    {
        VramEstimator.FormatEstimate(null, "none").Should().BeNull();
    }

    [Test]
    public void FormatEstimate_ZeroParams_ReturnsNull()
    {
        VramEstimator.FormatEstimate(0, "none").Should().BeNull();
    }

    [Test]
    public void FormatEstimate_NegativeParams_ReturnsNull()
    {
        VramEstimator.FormatEstimate(-1.0, "none").Should().BeNull();
    }
}
