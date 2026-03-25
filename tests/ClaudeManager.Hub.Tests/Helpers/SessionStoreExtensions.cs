using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Services;

namespace ClaudeManager.Hub.Tests.Helpers;

public static class SessionStoreExtensions
{
    public static MachineAgent RegisterTestAgent(
        this SessionStore store,
        string machineId    = TestData.MachineId,
        string connectionId = TestData.ConnectionId) =>
        store.RegisterAgent(machineId, connectionId, "Test Machine", "win32");
}
