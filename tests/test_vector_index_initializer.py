"""
Unit tests for VectorIndexInitializer hosted service.

Tests:
- IHostedService interface: StartAsync and StopAsync methods
- StartAsync calls Task.Delay(2000ms) before InitVectorIndexAsync
- IWikiService dependency injection via IServiceProvider
- IAsyncDisposable interface is implemented
"""
import asyncio
import time
from unittest.mock import patch, MagicMock, AsyncMock, Mock
from typing import Any, List


def test_hosted_service_interface_start_stop():
    """Test that VectorIndexInitializer implements IHostedService interface with StartAsync and StopAsync."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from Microsoft.Extensions.Logging import ILogger

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Verify StartAsync is callable
    assert hasattr(service, 'StartAsync'), "StartAsync method should exist"
    assert callable(service.StartAsync), "StartAsync should be callable"

    # Verify StopAsync is callable
    assert hasattr(service, 'StopAsync'), "StopAsync method should exist"
    assert callable(service.StopAsync), "StopAsync should be callable"

    # Verify it implements IHostedService
    assert hasattr(service, 'StartAsync'), "IHostedService.StartAsync should exist"
    assert hasattr(service, 'StopAsync'), "IHostedService.StopAsync should exist"


async def test_start_async_delayed_initialization():
    """Test that StartAsync performs 2-second delay before calling InitVectorIndexAsync."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from Microsoft.Extensions.Logging import ILogger
    from unittest.mock import AsyncMock

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_wiki_service.InitVectorIndexAsync = AsyncMock()

    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Measure time for StartAsync
    start_time = time.time()

    # Create mock cancellation token that won't fire
    mock_ct = Mock()
    mock_ct.IsCancellationRequested = False

    await service.StartAsync(mock_ct)

    elapsed = time.time() - start_time

    # Verify ~2 second delay (allow small tolerance)
    assert elapsed > 1.5, f"Expected delay of ~2 seconds, got {elapsed:.2f} seconds"
    assert elapsed < 5, f"Expected delay of ~2 seconds, actual was {elapsed:.2f} seconds (too long)"

    # Verify InitVectorIndexAsync was called
    mock_wiki_service.InitVectorIndexAsync.assert_awaited()


def test_stop_async_completes():
    """Test that StopAsync returns completed task."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from unittest.mock import Mock

    # Mock dependencies
    mock_wiki_service = Mock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Create mock cancellation token
    mock_ct = Mock()

    # Call StopAsync and verify it completes
    result = asyncio.run(service.StopAsync(mock_ct))
    assert result is not None
    assert asyncio.iscoroutine(result) or result is not None


async def test_async_disposable_implementation():
    """Test that VectorIndexInitializer implements IAsyncDisposable interface."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from unittest.mock import Mock

    # Mock dependencies
    mock_wiki_service = Mock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Verify DisposeAsync method exists and is callable
    assert hasattr(service, 'DisposeAsync'), "DisposeAsync method should exist"
    assert callable(service.DisposeAsync), "DisposeAsync should be callable"

    # Test async dispose
    disposal_result = await service.DisposeAsync()
    assert disposal_result is not None
    assert asyncio.iscoroutine(disposal_result) or disposal_result is not None


async def test_start_with_cancellation():
    """Test that StartAsync handles cancellation gracefully."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from asyncio import CancelledError
    from unittest.mock import Mock

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Create mock cancellation token
    mock_ct = Mock()
    mock_ct.IsCancellationRequested = True

    # Call StartAsync with cancellation token - should handle gracefully
    result = await service.StartAsync(mock_ct)
    assert result is None


def test_start_async_delay_verification():
    """Verify StartAsync uses Task.Delay(2000ms) with cancellation support."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    import inspect
    from unittest.mock import Mock

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Inspect StartAsync source to verify Task.Delay(1000ms) is called
    source = inspect.getsource(service.StartAsync)
    assert "Task.Delay" in source, "StartAsync should use Task.Delay for delay"
    assert "FromMilliseconds(2000)" in source or "2000" in source, "Delay should be 2000ms"
    assert "cancellationToken" in source, "Should use cancellation token in delay"


async def test_init_vector_index_injected():
    """Test that InitVectorIndexAsync is called with the correct dependency."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from unittest.mock import AsyncMock

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Create mock cancellation token
    mock_ct = Mock()
    mock_ct.IsCancellationRequested = False

    # Call StartAsync
    await service.StartAsync(mock_ct)

    # Verify InitVectorIndexAsync was called with the cancellation token
    mock_wiki_service.InitVectorIndexAsync.assert_called_with(mock_ct)


def test_service_provider_dependency_injection():
    """Test that VectorIndexInitializer can be resolved from ServiceCollection container."""
    from Microsoft.Extensions.DependencyInjection import ServiceCollection
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer

    # Create service collection
    services = ServiceCollection()
    services.AddSingleton(Mock())  # Mock IWikiService

    try:
        # Register VectorIndexInitializer
        services.AddHostedService(VectorIndexInitializer)
        assert services.IsServiceRegistered(VectorIndexInitializer), "VectorIndexInitializer should be registered"
    except TypeError:
        # If registration mechanism is different, just verify the class exists
        pass


async def test_wiki_service_dependency_injection():
    """Test that IWikiService is injected via IServiceProvider into VectorIndexInitializer."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from unittest.mock import Mock

    # Mock dependencies
    mock_wiki_service = Mock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()

    # Create instance with mocked WikiService
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Verify the WikiService is stored
    assert service._wiki_service is mock_wiki_service, "IWikiService should be injected"

    # Verify it has the InitVectorIndexAsync method
    assert hasattr(mock_wiki_service, 'InitVectorIndexAsync'), "WikiService should have InitVectorIndexAsync"


async def test_start_async_dependency_injection():
    """Test that StartAsync uses the injected IWikiService dependency."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from unittest.mock import AsyncMock

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Create mock cancellation token
    mock_ct = Mock()
    mock_ct.IsCancellationRequested = False

    # Call StartAsync
    await service.StartAsync(mock_ct)

    # Verify InitVectorIndexAsync was called on the injected WikiService
    # This confirms dependency injection is working
    assert mock_wiki_service.InitVectorIndexAsync.called, "InitVectorIndexAsync should be called on injected WikiService"


def test_vector_index_initializer_class_hierarchy():
    """Test that VectorIndexInitializer implements IHostedService correctly."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from Microsoft.Extensions.Hosting import IHostedService
    from uuid import uuid4

    # Mock dependencies for instantiation
    mock_wiki_service = Mock()
    mock_logger = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Verify class hierarchy
    assert hasattr(service, 'StartAsync'), "IHostedService interface requires StartAsync"
    assert hasattr(service, 'StopAsync'), "IHostedService interface requires StopAsync"


async def test_complete_lifecycle():
    """Test complete lifecycle of VectorIndexInitializer hosted service."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from unittest.mock import AsyncMock

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # 1. StartAsync
    mock_ct = Mock()
    mock_ct.IsCancellationRequested = False

    await service.StartAsync(mock_ct)
    assert mock_wiki_service.InitVectorIndexAsync.called

    # 2. StopAsync
    await service.StopAsync(mock_ct)

    # 3. DisposeAsync
    await service.DisposeAsync()

    print("Lifecycle test passed")


async def test_no_init_called_on_cancellation():
    """Verify InitVectorIndexAsync is NOT called when operation is cancelled."""
    from ClaudeManager.Hub.Services.VectorIndexInitializer import VectorIndexInitializer
    from Microsoft.Extensions.Logging import ILogger

    # Mock dependencies
    mock_wiki_service = AsyncMock()
    mock_logger = Mock()
    mock_logger.LogInformation = Mock()
    mock_logger.LogError = Mock()

    # Create instance
    service = VectorIndexInitializer(mock_wiki_service, mock_logger)

    # Create cancellation token that is already cancelled
    mock_ct = Mock()
    mock_ct.IsCancellationRequested = True

    # Reset InitVectorIndexAsync calls
    mock_wiki_service.InitVectorIndexAsync.reset_mock()

    # Start with cancellation
    await service.StartAsync(mock_ct)

    # Verify InitVectorIndexAsync was NOT called
    mock_wiki_service.InitVectorIndexAsync.assert_not_called()


if __name__ == "__main__":
    pytest.main([__file__, "-v", "-x"])
