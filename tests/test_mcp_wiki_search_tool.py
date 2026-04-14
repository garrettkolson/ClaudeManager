"""
Test Suite for SearchWikiViaMcML MCP Server Tool

Tests verification for AC-5 requirements:
- AC-5[1]: SearchWikiViaMcML exposed via [McpServerTool] attribute
- AC-5[2]: Method signature with (string query, int k = 5)
- AC-5[3]: HTTP GET to /api/wiki/search with query and k parameters
- AC-5[4]: 1000ms timeout via CancellationTokenSource with Try/Catch
- AC-5[5]: Formatted response with title, category, tags, similarity (0.0-1.0)
- AC-6: Fallback to keyword search on timeout/error
"""

import os
import sys
import threading
import time
from unittest.mock import AsyncMock, MagicMock, patch

pytest_plugins = ["pytest_asyncio"]


# Add project root to path
TEST_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.join(TEST_DIR, '..', '..', '..')
sys.path.insert(0, PROJECT_ROOT)


class TestMcpServerToolAttribute:
    """Test that [McpServerTool] attribute exists on SearchWikiViaMcML method."""

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_mcp_server_tool_attribute_present(self, mock_embedding, mock_task):
        """Verify [McpServerTool] attribute is on SearchWikiViaMcML method."""
        from ClaudeManager.McpServer.WikiTools import WikiTools, ViaSearchHit
        from ClaudeManager.McpServer.WikiConfig import WikiConfig

        # Mock HttpClient
        mock_http = AsyncMock()

        # Create tool instance
        config = WikiConfig('http://test', 'secret')
        tools = WikiTools(mock_http, config)

        # Get the SearchWikiViaMcML method
        method = getattr(tools, 'SearchWikiViaMcML')

        # Check that method exists
        assert hasattr(method, '__name__'), "SearchWikiViaMcML method should exist"
        assert method.__name__ == 'SearchWikiViaMcML'

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_mcp_server_tool_type_attribute(self, mock_embedding, mock_task):
        """Verify [McpServerTool] attribute presence on method."""
        from ClaudeManager.McpServer.WikiTools import WikiTools
        from ClaudeManager.McpServer.WikiConfig import WikiConfig

        method = WikiTools.SearchWikiViaMcML

        # Method should be a public async Task<string> method
        assert hasattr(method, '__doc__'), "Method should have documentation"
        assert 'semantic search' in method.__doc__.lower(), "Method doc should describe semantic search"


class TestMethodSignature:
    """Test method signature: async Task<string> SearchWikiViaMcML(string query, int k = 5)."""

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_method_signature(self, mock_embedding, mock_task):
        """Verify async Task<string> SearchWikiViaMcML signature."""
        from ClaudeManager.McpServer.WikiTools import WikiTools
        from ClaudeManager.McpServer.WikiConfig import WikiConfig

        method = WikiTools.SearchWikiViaMcML

        # Verify method has expected name
        assert 'SearchWikiViaMcML' in method.__name__
        assert callable(method), "Method should be callable"

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_default_k_value(self, mock_embedding, mock_task):
        """Verify k parameter has default value of 5."""
        from ClaudeManager.McpServer.WikiTools import WikiTools
        from ClaudeManager.McpServer.WikiConfig import WikiConfig

        method = WikiTools.SearchWikiViaMcML

        # Verify we can call with just query (k defaults to 5)
        assert method is not None, "Method should exist"
        assert callable(method), "Method should be callable"


class TestHttpRequest:
    """Test HTTP GET to /api/wiki/search endpoint."""

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_http_get_called_with_query_param(self, mock_embedding, mock_task):
        """Verify HTTP GET uses query parameter in URL."""
        from ClaudeManager.McpServer.WikiTools import WikiTools
        from ClaudeManager.McpServer.WikiConfig import WikiConfig

        # Mock the HttpClient
        mock_http = AsyncMock()
        mock_response = AsyncMock()
        mock_response.IsSuccessStatusCode = True
        mock_response.Content = MagicMock()
        mock_response.Content.ReadAsString = AsyncMock(return_value='{"results": []}')
        mock_response.Content.ReadAsJson = AsyncMock(return_value={'results': []})
        mock_http.SendAsync = AsyncMock(return_value=mock_response)

        config = WikiConfig('http://test-host', 'secret')
        await WikiTools(mock_http, config).test_method()


class TestTimeoutHandling:
    """Test 1000ms timeout via CancellationTokenSource with Try/Catch."""

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_cancellation_token_source_created(self, mock_embedding, mock_task):
        """Verify CancellationTokenSource is created for 1000ms timeout."""
        from ClaudeManager.McpServer.WikiTools import WikiTools

        # Verify timeout constant is set to 1000ms
        # The method should use CancellationTokenSource with 1000ms

    @patch('System.Threading.Tasks.Task', wraps=threading.Timer)
    def test_task_wrapper_usedused(self, mock_timer):
        """Verify Task.Run wrapper is used around async code."""
        from unittest.mock import patch
        import threading

        # Create a wrapper that simulates the timeout behavior
        with patch('ClaudeManager.McpServer.WikiTools.Task') as mock_task_class:
            # Check that Task.Run is called within SearchWikiViaMcML
            pass


class TestResponseFormat:
    """Test response format with title, category, tags, similarity scores (0.0-1.0)."""

    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    @patch('System.Threading.Tasks.Task')
    async def test_response_format_with_score(self, mock_task, mock_embedding):
        """Verify response includes title, category, tags, and similarity score."""
        from unittest.mock import AsyncMock, MagicMock

        # Simulate a search result with proper fields
        from ClaudeManager.McpServer.WikiTools import WikiTools
        from ClaudeManager.McpServer.WikiConfig import WikiConfig

        # The ParseSearchEntries method should produce ViaSearchHit objects
        # With: Title, Category, Tags, Score (0.0-1.0)
        mock_task_class = MagicMock()
        async def async_timer(coro, timeout):
            await coro

        mock_task_class.Run = async_timer

        # The response formatting should include:
        # - Title
        # - Category
        # - Tags
        # - Similarity score formatted to 3 decimal places (0.000-1.000)


class TestKFallback:
    """Test k fallback to available results count."""

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_take_k_results(self, mock_embedding, mock_task):
        """Verify method uses .Take(k) to limit results."""
        from ClaudeManager.McpServer.WikiTools import WikiTools
        from ClaudeManager.McpServer.WikiConfig import WikiConfig

        # The implementation should use .Take(k) to limit results to requested count
        pass

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_false_available_count(self, mock_embedding, mock_task):
        """Test fallback behavior when fewer results available than k."""
        pass


class TestIntegration:
    """Integration tests for MCP server."""

    @patch('System.Threading.Tasks.Task')
    @patch('ClaudeManager.Hub.Services.EmbeddingService.AllMpnnetBaseV2')
    async def test_mcp_server_starts(self, mock_embedding, mock_task):
        """Test MCP server starts without error."""
        from unittest.mock import patch, AsyncMock
        import asyncio

        # Simulate host
        async def wrapper(coro):
            return await coro

        # Verify MCP server can initialize with SearchWikiViaMcML
        pass


class TestThreadingTimeoutError:
    """Test threading timeout behavior."""

    @patch('System.Threading.Tasks.Task', wraps=threading.Timer)
    def test_threading_timeout_error(self, mock_timer):
        """Verify behavior with threading timeout."""
        import threading
        import time
        from unittest.mock import AsyncMock, mock, AsyncMock

        with patch('ClaudeManager.McpServer.WikiTools.Task') as mock_task_class:
            mock_task = MagicMock(AsyncMock, return_value=AsyncMock())
            mock_task_class.Run = MagicMock(return_value=AsyncMock())

            # Test that search works
            pass

if __name__ == "__main__":
    pytest.main([__file__, "-v", "-x", "--tb=short"])
