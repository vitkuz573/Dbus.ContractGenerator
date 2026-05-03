using System.Net.Sockets;
using Dbus.Contracts;
using Xunit;

namespace Dbus.Contracts.Tests;

public sealed class UnixSocketInteropTests
{
    [Fact]
    public async Task Receive_WithNonBlockingSocketAndDelayedPayload_WaitsForReadableData()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"dbus-contracts-{Guid.NewGuid():N}.sock");

        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);

            using var receiver = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await receiver.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            using var sender = await listener.AcceptAsync();

            receiver.Blocking = false;
            byte[] expected = [1, 2, 3, 4];

            var sendTask = Task.Run(async () =>
            {
                await Task.Delay(50);
                sender.Send(expected);
            });

            var buffer = new byte[16];
            var (bytesReceived, fileDescriptors) = UnixSocketInterop.Receive(receiver, buffer);
            await sendTask;

            Assert.Equal(expected.Length, bytesReceived);
            Assert.Empty(fileDescriptors);
            Assert.Equal(expected, buffer[..bytesReceived]);
        }
        finally
        {
            File.Delete(socketPath);
        }
    }
}
