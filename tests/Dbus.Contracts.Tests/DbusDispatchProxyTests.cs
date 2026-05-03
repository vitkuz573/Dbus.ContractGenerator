using System.Reflection;
using Dbus.Contracts;
using Xunit;

namespace Dbus.Contracts.Tests;

public sealed class DbusDispatchProxyTests
{
    [Fact]
    public void Create_WithDbusDispatchProxy_CreatesInterfaceProxy()
    {
        var proxy = DispatchProxy.Create<ITestDbusObject, DbusDispatchProxy>();

        Assert.NotNull(proxy);
        Assert.IsAssignableFrom<ITestDbusObject>(proxy);
    }

    [DbusInterface("org.example.Test")]
    private interface ITestDbusObject : IDbusObject
    {
        [DbusMethod("Ping", "", "")]
        Task PingAsync();
    }
}
