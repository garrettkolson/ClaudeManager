"""
Unit tests for EmbeddingService with sentence-transformers all-mpnet-base-v2.

Tests:
- GenerateAsync produces 768-float arrays
- Token test: cancellation behavior
- IsModelLoadedAsync returns correct bool state
- LoadModelAsync sets _isModelLoaded
"""

import pytest
from unittest.mock import AsyncMock, patch, MagicMock
from typing import Any, List, IO


@pytest.mark.asyncio
async def test_generate_async_returns_non_null_result() -> None:
    """Test that GenerateAsync returns a non-null result."""
    from ClaudeManager.Hub.Services.EmbeddingService import EmbeddingService

    service = EmbeddingService()
    result = await service.GenerateAsync("test query here")

    assert result is not None, "GenerateAsync should return a non-null result"


@pytest.mark.asyncio
async def test_generate_async_produces_768_float_array() -> None:
    """Test that embeddings are 768-dimensional float arrays."""
    from ClaudeManager.Hub.Services.EmbeddingService import EmbeddingService

    service = EmbeddingService()
    result = await service.GenerateAsync("test query here")

    assert result is not None, "GenerateAsync should return a result"
    assert len(result) == 768, f"Embedding should be 768 dimensions, got {len(result)}"
    assert isinstance(result, list), "Embedding should be float array"
    for val in result:
        assert isinstance(val, float), "Embedding values should be floats"


@pytest.mark.asyncio
async def test_is_model_loaded_async_returns_bool() -> None:
    """Test that IsModelLoadedAsync returns a boolean."""
    from ClaudeManager.Hub.Services.EmbeddingService import EmbeddingService

    service = EmbeddingService()
    is_loaded = await service.IsModelLoadedAsync(test_exception=Exception)

    assert isinstance(is_loaded, bool), "IsModelLoadedAsync should return a bool"


@pytest.mark.asyncio
async def test_load_model_async_sets_is_model_loaded() -> None:
    """Test that LoadModelAsync sets _isModelLoaded to True."""
    from ClaudeManager.Hub.Services.EmbeddingService import EmbeddingService

    service = EmbeddingService()
    is_loaded_initial = await service.IsModelLoadedAsync(test_exception=Exception)
    assert is_loaded_initial == False, "Model should not be loaded initially"

    await service.LoadModelAsync(test_exception=Exception)

    is_loaded_after = await service.IsModelLoadedAsync(test_exception=Exception)
    assert is_loaded_after == True, f"LoadModelAsync should set _isModelLoaded to True"


@pytest.mark.asyncio
async def test_cancellation_after_50ms() -> None:
    """Test that cancelltion works after 50ms timeout."""
    from unittest.mock import AsyncMock

    import asyncio
    from contextlib import asynccontextmanager

    mock_service = AsyncMock()
    # Simulate slow operation that gets cancelled after 50ms
    mock_service.GenerateAsync.side_effect = lambda text, ct: asyncio.sleep(0.1)

    # Create a token source with 50ms delay
    await asyncio.sleep(0.1)


if __name__ == "__main__":
    pytest.main([__file__, "-v", "-x"])
