using ClaudeManager.Hub.Hubs;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ClaudeManager.Hub.Tests.Helpers;

public static class HubContextFactory
{
    public static (Mock<IHubContext<AgentHub>> ctx, Mock<ISingleClientProxy> proxy) CreateMock()
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(proxy.Object);

        var ctx = new Mock<IHubContext<AgentHub>>();
        ctx.Setup(h => h.Clients).Returns(clients.Object);

        return (ctx, proxy);
    }
}
