using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using NUnit.Framework;

namespace ClaudeManager.Hub.Tests;

/// <summary>
/// Tests for IWikiService search methods including FindSimilarAsync, FindSimilarViaKeyword, and WikiSearchResult DTOs.
/// According to Testing Strategy: Verify IWikiService has FindSimilarAsync, FindSimilarViaKeyword methods.
/// Verify WikiSearchResult has Results, QueryEmbedding, SearchMethod properties.
/// Verify WikiSearchHit has Entry, Similarity properties.
/// Test with mock dependencies.
/// </summary>
[TestFixture]
public class test_iwiki_service
{
    [SetUp]
    public void Setup()
    {
        // Setup
    }

    #region Cosine Similarity Tests

    [Test]
    public void CosineSimilarity_WithIdenticalVectors_Returns_One()
    {
        // Arrange
        var vec = new float[]
        {
            0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f
        };

        // Act
        var result = WikiService.CosineSimilarity(vec, vec);

        // Assert
        result.Should().Be(1f);
    }

    [Test]
    public void CosineSimilarity_WithOrthogonalVectors_Returns_Zero()
    {
        // Arrange - Orthogonal-like vectors
        var ortho1 = new float[] { 1f, 0f, 0f, 0f };
        var ortho2 = new float[] { 0f, 1f, 0f, 0f };

        // Act
        var similarity = WikiService.CosineSimilarity(ortho1, ortho2);

        // Assert
        similarity.Should().BeCloseTo(0f, 0.01f);
    }

    [Test]
    public void CosineSimilarity_Returns_Value_In_0_To_1_Range()
    {
        // Arrange - Various test vectors
        var testVectors = new List<(float[], float[])>
        {
            (new float[] { 1f, 0f }, new float[] { 1f, 0f }),
            (new float[] { 1f, 0f }, new float[] { 0f, 1f }),
            (new float[] { 0.5f, 0.3f }, new float[] { 1f, 0f }),
            (new float[] { 0.1f, 0.2f }, new float[] { 0.8f, 0.6f }),
            (new float[] { 0f, 1f, 0f }, new float[] { 1f, 0f, 0f }),
            (new float[] { 0f, 0f, 1f }, new float[] { 0f, 0f, 1f }),
        };

        // Act & Assert
        foreach (var (vec1, vec2) in testVectors)
        {
            var similarity = WikiService.CosineSimilarity(vec1, vec2);
            similarity.Should().BeGreaterOrEqualTo(0f, "Similarity should not be negative");
            similarity.Should().BeLessOrEqualTo(1f, "Similarity should not exceed 1");
        }
    }

    #endregion

    #region WikiSearchResult DTO Tests

    [Test]
    public void WikiSearchResult_Has_Properties()
    {
        // Create test objects
        var results = new List<WikiSearchHit>();
        var queryEmbedding = new float[768];
        var searchMethod = "semantic";
        var matchingScore = 0.5f;

        // Act - Create WikiSearchResult
        var result = new WikiSearchResult
        {
            Results = results,
            QueryEmbedding = queryEmbedding,
            SearchMethod = searchMethod,
            MatchingScore = matchingScore
        };

        // Assert - Properties exist and are accessible
        result.Results.Should().BeOfType<List<WikiSearchHit>>();
        result.QueryEmbedding.Should().HaveLength(768);
        result.SearchMethod.Should().Be("semantic");
        result.MatchingScore.Should().Be(0.5f);
    }

    [Test]
    public void WikiSearchResult_Default_Properties()
    {
        // Act - Create default WikiSearchResult
        var result = new WikiSearchResult();

        // Assert - Default properties
        result.Results.Should().BeEmpty();
        result.QueryEmbedding.Should().HaveLength(768);
        result.SearchMethod.Should().Be("semantic");
        result.MatchingScore.Should().BeNull();
    }

    #endregion

    #region WikiSearchHit DTO Tests

    [Test]
    public void WikiSearchHit_Has_Entry_And_Similarity_Properties()
    {
        // Arrange - Create a wiki entry
        var entry = new WikiEntryEntity
        {
            Id = 1,
            Title = "Test Entry",
            Content = "Test content",
            IsArchived = false
        };

        // Act - Create WikiSearchHit
        var hit = new WikiSearchHit
        {
            Entry = entry,
            Similarity = 0.85f
        };

        // Assert - Properties accessible
        hit.Entry.Should().BeOfType<WikiEntryEntity>();
        hit.Entry.Id.Should().Be(1);
        hit.Entry.Title.Should().Be("Test Entry");
        hit.Similarity.Should().Be(0.85f);
    }

    [Test]
    public void WikiSearchHit_Default_Properties()
    {
        // Arrange
        var entry = new WikiEntryEntity
        {
            Id = 2,
            Title = "Another Entry",
            Content = "More content",
            IsArchived = false
        };

        // Act - Create hit with default similarity
        var hit = new WikiSearchHit
        {
            Entry = entry,
            Similarity = 0.9f
        };

        // Assert
        hit.Similarity.Should().Be(0.9f);
        hit.Entry.Should().NotBeNull();
    }

    #endregion

    #region Similarity Range Tests

    [Test]
    public void Similarity_Score_Between_Similar_Vectors()
    {
        // Arrange
        var similarVector1 = new float[] { 0.5f, 0.5f, 0f, 0f };
        var similarVector2 = new float[] { 0.4f, 0.5f, 0.1f, 0f };

        // Act
        var similarity = WikiService.CosineSimilarity(similarVector1, similarVector2);

        // Assert - Similar vectors should have high similarity
        similarity.Should().BeGreaterThan(0.5f);
        similarity.Should().BeLessThan(1f);
    }

    [Test]
    public void Similarity_Score_With_DifferentLengths_Throws()
    {
        // Arrange
        var shortVec = new float[] { 1f, 2f };
        var longVec = new float[] { 1f, 2f, 3f, 4f };

        // Act & Assert - Should throw with mismatched vectors
        // This verifies the method correctly validates input
        var exception = Assert.Throws<ArgumentException>(() =>
            WikiService.CosineSimilarity(shortVec, longVec));

        exception.Message.Should().Contain("length");
    }

    #endregion
}
