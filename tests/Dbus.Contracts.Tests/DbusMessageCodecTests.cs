using Dbus.Contracts;
using Xunit;

namespace Dbus.Contracts.Tests;

public sealed class DbusMessageCodecTests
{
    [Fact]
    public void EncodeDecode_WithLoginSessionsArray_RoundTripsTupleArray()
    {
        var sessions = new[]
        {
            ("31", 1000u, "vitaly", "seat0", new DbusObjectPath("/org/freedesktop/login1/session/_31"))
        };
        var message = new DbusMessage
        {
            Type = DbusMessageType.MethodReturn,
            Serial = 2,
            ReplySerial = 1,
            Signature = "a(susso)",
            Body = [sessions]
        };

        var payload = DbusMessageCodec.Encode(message, out var fds);

        Assert.Empty(fds);
        Assert.True(DbusMessageCodec.TryDecode(payload, [], out var decoded, out var consumed));
        Assert.Equal(payload.Length, consumed);

        var decodedSessions = Assert.IsType<object?[]>(decoded.Body[0]);
        var typedSessions = (ValueTuple<string, uint, string, string, DbusObjectPath>[])DbusValueConverter.ConvertTo(
            decodedSessions,
            typeof(ValueTuple<string, uint, string, string, DbusObjectPath>[]))!;
        Assert.Equal("31", typedSessions[0].Item1);
        Assert.Equal((uint)1000, typedSessions[0].Item2);
        Assert.Equal("/org/freedesktop/login1/session/_31", typedSessions[0].Item5.Value);
    }

    [Fact]
    public void EncodeDecode_WithSystemdTransientProperties_RoundTripsNestedVariants()
    {
        var properties = new (string, object)[]
        {
            ("Description", "Example readiness probe"),
            ("ExecStart", new[] { ("/usr/bin/true", new[] { "/usr/bin/true" }, false) }),
            ("Restart", "no")
        };
        var message = new DbusMessage
        {
            Type = DbusMessageType.MethodCall,
            Serial = 9,
            Destination = "org.freedesktop.systemd1",
            Path = "/org/freedesktop/systemd1",
            Interface = "org.freedesktop.systemd1.Manager",
            Member = "StartTransientUnit",
            Signature = "ssa(sv)a(sa(sv))",
            Body =
            [
                "example-test.service",
                "replace",
                properties,
                Array.Empty<(string, (string, object)[])>()
            ]
        };

        var payload = DbusMessageCodec.Encode(message, out var fds);

        Assert.Empty(fds);
        Assert.True(DbusMessageCodec.TryDecode(payload, [], out var decoded, out _));
        var decodedProperties = Assert.IsType<object?[]>(decoded.Body[2]);
        var description = Assert.IsType<object?[]>(decodedProperties[0]);
        var execStart = Assert.IsType<object?[]>(decodedProperties[1]);
        var restart = Assert.IsType<object?[]>(decodedProperties[2]);
        Assert.Equal("Description", description[0]);
        Assert.Equal("Example readiness probe", description[1]);
        Assert.Equal("ExecStart", execStart[0]);
        Assert.NotNull(execStart[1]);
        Assert.Equal("Restart", restart[0]);
        Assert.Equal("no", restart[1]);
    }

    [Fact]
    public void VariantFactory_WithKWinOptions_UsesStringVariantDictionary()
    {
        var variant = DbusVariantFactory.Create(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["include-cursor"] = false
        });

        Assert.Equal("a{sv}", variant.Signature);
    }
}
