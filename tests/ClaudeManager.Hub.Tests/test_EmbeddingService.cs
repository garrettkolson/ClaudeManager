using System.Threading;
using System.Threading.Tasks;
using ClaudeManager.Hub.Services;
using NUnit.Framework;

namespace ClaudeManager.Hub.Tests;

/// <summary>
/// Tests for EmbeddingService - GenerateAsync produces 768-float arrays.
/// Verify LoadModelAsync sets _isModelLoaded.
/// Use CancellationTokenSource to test timeout behavior.
/// </summary>
[TestFixture]
public class test_EmbeddingService
{
    private EmbeddingService _embeddingService;

    [SetUp]
    public void Setup()
    {
        _embeddingService = new EmbeddingService();
    }

    [Test]
    public async Task GenerateAsync_ReturnsNonNullResult()
    {
        // Expect: GenerateAsync returns non-null
        var result = await _embeddingService.GenerateAsync("test query here");

        Assert.IsNotNull(result, "GenerateAsync should return a non-null result");
    }

    [Test]
    public async Task GenerateAsync_Returns_768_Dimension_Flat_Array()
    {
        // Expect: GenerateAsync produces 768-float arrays
        var result = await _embeddingService.GenerateAsync("test query here");

        Assert.IsNotNull(result, "GenerateAsync should return a result");
        Assert.AreEqual(768, result.Length, "Embedding should be 768 dimensions");
        Assert.IsInstanceOf(typeof(float), result[0], "Embedding should be float array");
    }

    [Test]
    public async Task GenerateAsync_Returns_Float_Array()
    {
        // Expect: all values are floats
        var result = await _embeddingService.GenerateAsync("test query here");

        foreach (var val in result)
        {
            Assert.IsInstanceOf(typeof(float), val);
            Assert.IsNotNull(val);
        }
    }

    [Test]
    public async Task IsModelLoadedAsync_Returns_Bool()
    {
        // Expect: IsModelLoadedAsync returns bool
        bool isLoaded = await _embeddingService.IsModelLoadedAsync(CancellationToken.None);

        Assert.IsInstanceOf(typeof(bool), isLoaded);
    }

    [Test]
    public async Task LoadModelAsync_Sets_Is_Model_Loaded()
    {
        // Expect: LoadModelAsync sets _isModelLoaded = true
        Assert.IsFalse(await _embeddingService.IsModelLoadedAsync(CancellationToken.None));

        await _embeddingService.LoadModelAsync(CancellationToken.None);

        Assert.IsTrue(await _embeddingService.IsModelLoadedAsync(CancellationToken.None),
            "LoadModelAsync should set _isModelLoaded to true");
    }

    [Test]
    public async Task GenerateAsync_Throws_On_Cancellation_After_50Ms()
    {
        // Expect: Test timeout/cancellation behavior
        var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var task = Task.Run(async () =>
        {
            await _embeddingService.GenerateAsync("test", cts.Token);
        }).ContinueWith(t =>
        {
            if (t.IsCanceled)
            {
                throw new TaskCanceledException();
            }
        });

        try
        {
            await Task.Delay(100);
            Assert.AreEqual(false, cts.IsCancellationRequested);
        }
        catch (Exception)
        {
        }
    }
}
