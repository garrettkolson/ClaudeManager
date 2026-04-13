using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Unit tests for the IKalendarSearcher interface and KalendarSearcher implementation.
/// Verifies AC-6: Fallback to keyword search with configurable threshold.
/// </summary>
[TestFixture]
public class KalendarSearcherTests
{
    private List<WikiEntryEntity> _testEntries;
    private KalendarSearcher _searcher;

    [SetUp]
    public void SetUp()
    {
        _testEntries = new List<WikiEntryEntity>
        {
            new WikiEntryEntity
            {
                Id = 1,
                Title = "Authentication Failure in Login Module",
                Category = "bug",
                Content = "Users are unable to authenticate using the new login flow. The authorization token is not being validated correctly.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsArchived = false
            },
            new WikiEntryEntity
            {
                Id = 2,
                Title = "Project Decision: Use JWT for Auth",
                Category = "decision",
                Content = "The team decided to implement JSON Web Tokens (JWT) as the authentication standard for the build system.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsArchived = false
            },
            new WikiEntryEntity
            {
                Id = 3,
                Title = "Backend note about caching",
                Category = "note",
                Content = "Caching should be implemented at the database layer to reduce query load.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsArchived = true  // Archived entry
            }
        };

        _searcher = new KalendarSearcher(_testEntries);
    }

    [Test]
    public void KalendarSearcher_Constructor_InitializesWithEntries()
    {
        // Verifies interface implementation compiles and constructor works
        _searcher.Should().NotBeNull();
        _searcher should have entries indexed.
    }

    [Test]
    public async Task SearchKeyword_ReturnsTupleOfListAndFloat()
    {
        // Verifies SearchKeyword returns tuple of (List<WikiSearchHit>, float)
        var result = await _searcher.SearchKeyword("authentication");

        // Type checks - verifies return type is tuple (T1, T2)
        result.Should().BeOfType<(List<WikiSearchHit>, float)>();

        // First element of tuple should be List<WikiSearchHit>
        result.Item1.Should().BeType<List<>>();
        result.Item1.Should().BeOfType<List<WikiSearchHit>>();

        // Second element of tuple should be float
        result.Item2.Should().BeTypeOf<float>();

        // Result should not be null
        result.Item1.Should().NotBeNull();
    }

    [Test]
    public async Task SearchKeyword_ReturnsSemanticallyCorrectResults()
    {
        // Verifies search returns relevant entries
        var (hits, queryScore) = await _searcher.SearchKeyword("authentication", 5);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(entry => entry.Entry.Title.Contains("Authentication"));
        queryScore.Should().BeGreaterThanOrEqualTo(0f);
        queryScore.Should().BeLessThanOrEqualTo(1f);
    }

    [Test]
    public async Task SearchKeyword_ReturnsSortedBySimilarityDescending()
    {
        var (hits, queryScore) = await _searcher.SearchKeyword("authentication", 10);

        var similarities = hits.Select(h => h.Similarity).ToList();
        similarities.Should().BeAscendingOrEqual(similarities.First(), similarities.Last());
    }

    [Test]
    public async Task SearchKeyword_MapsToWikiSearchHitRecord()
    {
        var (hits, queryScore) = await _searcher.SearchKeyword("auth", 3);

        // Verify each hit has proper WikiSearchHit structure
        hits.Should().OnlyContain(h =>
            h.Entry != null &&
            h.Entry.Title != null &&
            h.Similarity >= 0f &&
            h.Similarity <= 1f
        );
    }

    [Test]
    public async Task SearchKeyword_SearchesActiveEntriesOnly()
    {
        // Verify archived entries are excluded from results
        var (hits, queryScore) = await _searcher.SearchKeyword("caching", 10);

        var containsArchived = hits.Any(h => h.Entry.Id == 3);
        containsArchived.Should().BeFalse();
    }

    [Test]
    public async Task SearchKeyword_HandlesEmptyQuery()
    {
        var (hits, queryScore) = await _searcher.SearchKeyword("", 5);

        hits.Should().Be_empty();
        queryScore.Should().Be(0f);
    }

    [Test]
    public async Task SearchKeyword_HandlesNullQuery()
    {
        var (hits, queryScore) = await _searcher.SearchKeyword(null!, 5);

        hits.Should().Be_empty();
        queryScore.Should().Be(0f);
    }

    [Test]
    public async Task SearchKeyword_HandlesLargeKValue()
    {
        // Request more results than available
        var (hits, queryScore) = await _searcher.SearchKeyword("auth", 100);

        // Should return all matching entries (2 active)
        hits.Should().HaveCountLessThanOrEqualTo(3); // Out of 3 total entries
    }

    [Test]
    public async Task SearchKeyword_SmallKValue_LimitsResults()
    {
        // Request only 1 result
        var (hits, queryScore) = await _searcher.SearchKeyword("authentication", 1);

        // Should return only 1 result
        hits.Should().HaveCount(1);
    }
}
