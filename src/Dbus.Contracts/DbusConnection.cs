using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Dbus.Contracts;

public sealed class DbusConnection : IAsyncDisposable, IDisposable
{
    private const string DbusServiceName = "org.freedesktop.DBus";
    private const string DbusObjectPathValue = "/org/freedesktop/DBus";
    private const string DbusInterfaceName = "org.freedesktop.DBus";
    private const byte NoReplyExpectedFlag = 1;

    private readonly Socket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DbusMessage>> _pendingCalls = new();
    private readonly List<DbusSignalSubscription> _subscriptions = [];
    private readonly object _subscriptionSync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<byte> _receiveBuffer = [];
    private readonly Queue<int[]> _receivedFileDescriptorBatches = new();
    private int _nextSerial;
    private Task? _receiveLoop;

    private DbusConnection(Socket socket)
    {
        _socket = socket;
    }

    public string? UniqueName { get; private set; }

    public static async Task<DbusConnection> ConnectAsync(DbusBusKind busKind, CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveEndpoint(busKind);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(endpoint, cancellationToken);

        var connection = new DbusConnection(socket);
        await connection.AuthenticateAsync(cancellationToken);
        connection.StartReceiveLoop();
        connection.UniqueName = await connection.CallMethodAsync<string>(
            DbusServiceName,
            DbusObjectPathValue,
            DbusInterfaceName,
            "Hello",
            string.Empty,
            [],
            "s",
            noReply: false,
            cancellationToken);
        return connection;
    }

    public static Task<DbusConnection> ConnectSystemAsync(CancellationToken cancellationToken = default)
    {
        return ConnectAsync(DbusBusKind.System, cancellationToken);
    }

    public static Task<DbusConnection> ConnectSessionAsync(CancellationToken cancellationToken = default)
    {
        return ConnectAsync(DbusBusKind.Session, cancellationToken);
    }

    public TProxy CreateProxy<TProxy>(string serviceName, DbusObjectPath objectPath)
        where TProxy : class, IDbusObject
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("D-Bus service name cannot be empty.", nameof(serviceName));
        }

        var proxy = DispatchProxy.Create<TProxy, DbusDispatchProxy>();
        ((DbusDispatchProxy)(object)proxy).Initialize(this, typeof(TProxy), serviceName, objectPath);

        return proxy;
    }

    internal async Task<object?> InvokeMethodAsync(
        string destination,
        string path,
        string interfaceName,
        string member,
        string inputSignature,
        object?[] body,
        string outputSignature,
        Type returnType,
        bool noReply,
        CancellationToken cancellationToken)
    {
        var reply = await CallMethodAsync(
            destination,
            path,
            interfaceName,
            member,
            inputSignature,
            body,
            outputSignature,
            noReply,
            cancellationToken);

        if (returnType == typeof(void))
        {
            return null;
        }

        return ConvertReplyBody(reply.Body, outputSignature, returnType);
    }

    internal async Task<T> CallMethodAsync<T>(
        string destination,
        string path,
        string interfaceName,
        string member,
        string inputSignature,
        object?[] body,
        string outputSignature,
        bool noReply,
        CancellationToken cancellationToken)
    {
        var reply = await CallMethodAsync(
            destination,
            path,
            interfaceName,
            member,
            inputSignature,
            body,
            outputSignature,
            noReply,
            cancellationToken);
        return (T)DbusValueConverter.ConvertTo(ConvertReplyBody(reply.Body, outputSignature, typeof(T)), typeof(T))!;
    }

    internal async Task<DbusMessage> CallMethodAsync(
        string destination,
        string path,
        string interfaceName,
        string member,
        string inputSignature,
        object?[] body,
        string outputSignature,
        bool noReply,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposeCts.IsCancellationRequested, this);

        var serial = NextSerial();
        var message = new DbusMessage
        {
            Type = DbusMessageType.MethodCall,
            Flags = noReply ? NoReplyExpectedFlag : (byte)0,
            Serial = serial,
            Path = path,
            Interface = interfaceName,
            Member = member,
            Destination = destination,
            Signature = inputSignature,
            Body = body
        };

        if (noReply)
        {
            await SendAsync(message, cancellationToken);
            return new DbusMessage { Type = DbusMessageType.MethodReturn, ReplySerial = serial };
        }

        var completion = new TaskCompletionSource<DbusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCalls.TryAdd(serial, completion))
        {
            throw new DbusException($"Duplicate D-Bus serial {serial}.");
        }

        try
        {
            await SendAsync(message, cancellationToken);
            using var registration = cancellationToken.Register(static state =>
            {
                ((TaskCompletionSource<DbusMessage>)state!).TrySetCanceled();
            }, completion);

            var reply = await completion.Task.ConfigureAwait(false);
            if (reply.Type == DbusMessageType.Error)
            {
                var errorText = reply.Body.Length > 0 ? Convert.ToString(reply.Body[0]) ?? reply.ErrorName ?? "D-Bus error" : reply.ErrorName ?? "D-Bus error";
                throw new DbusRemoteException(reply.ErrorName ?? "org.freedesktop.DBus.Error.Failed", errorText);
            }

            return reply;
        }
        finally
        {
            _pendingCalls.TryRemove(serial, out _);
        }
    }

    internal async Task<IDisposable> AddSignalSubscriptionAsync(
        string path,
        string interfaceName,
        string member,
        string signature,
        Action<DbusMessage> handler,
        Action<Exception>? onError,
        string? firstArgument = null,
        CancellationToken cancellationToken = default)
    {
        var rule = BuildMatchRule(path, interfaceName, member, firstArgument);
        await CallMethodAsync(
            DbusServiceName,
            DbusObjectPathValue,
            DbusInterfaceName,
            "AddMatch",
            "s",
            [rule],
            string.Empty,
            noReply: false,
            cancellationToken);

        var subscription = new DbusSignalSubscription(this, rule, path, interfaceName, member, signature, handler, onError);
        lock (_subscriptionSync)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    private async Task RemoveSignalSubscriptionAsync(DbusSignalSubscription subscription)
    {
        lock (_subscriptionSync)
        {
            _subscriptions.Remove(subscription);
        }

        if (_disposeCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await CallMethodAsync(
                DbusServiceName,
                DbusObjectPathValue,
                DbusInterfaceName,
                "RemoveMatch",
                "s",
                [subscription.Rule],
                string.Empty,
                noReply: false,
                CancellationToken.None);
        }
        catch
        {
            // Removing a match is best-effort during disposal; the bus drops matches when the connection closes.
        }
    }

    private async Task SendAsync(DbusMessage message, CancellationToken cancellationToken)
    {
        var payload = DbusMessageCodec.Encode(message, out var unixFileDescriptors);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            UnixSocketInterop.Send(_socket, payload, unixFileDescriptors);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void StartReceiveLoop()
    {
        _receiveLoop = Task.Run(ReceiveLoop);
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                var (received, descriptors) = UnixSocketInterop.Receive(_socket, buffer);
                if (received == 0)
                {
                    throw new DbusException("D-Bus connection closed by peer.");
                }

                if (descriptors.Length > 0)
                {
                    _receivedFileDescriptorBatches.Enqueue(descriptors);
                }

                for (var i = 0; i < received; i++)
                {
                    _receiveBuffer.Add(buffer[i]);
                }

                DrainMessages();
            }
        }
        catch (Exception ex) when (!_disposeCts.IsCancellationRequested)
        {
            FailPendingCalls(ex);
        }
    }

    private void DrainMessages()
    {
        while (_receiveBuffer.Count > 0)
        {
            var descriptors = _receivedFileDescriptorBatches.Count > 0 ? _receivedFileDescriptorBatches.Peek() : [];
            if (!DbusMessageCodec.TryDecode(CollectionsMarshal.AsSpan(_receiveBuffer), descriptors, out var message, out var consumed))
            {
                return;
            }

            _receiveBuffer.RemoveRange(0, consumed);
            if (_receivedFileDescriptorBatches.Count > 0)
            {
                _receivedFileDescriptorBatches.Dequeue();
            }

            DispatchMessage(message);
        }
    }

    private void DispatchMessage(DbusMessage message)
    {
        switch (message.Type)
        {
            case DbusMessageType.MethodReturn:
            case DbusMessageType.Error:
                if (_pendingCalls.TryGetValue(message.ReplySerial, out var pending))
                {
                    pending.TrySetResult(message);
                }

                break;

            case DbusMessageType.Signal:
                DispatchSignal(message);
                break;
        }
    }

    private void DispatchSignal(DbusMessage message)
    {
        DbusSignalSubscription[] subscriptions;
        lock (_subscriptionSync)
        {
            subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            if (!subscription.Matches(message))
            {
                continue;
            }

            try
            {
                subscription.Handler(message);
            }
            catch (Exception ex)
            {
                subscription.OnError?.Invoke(ex);
            }
        }
    }

    private void FailPendingCalls(Exception exception)
    {
        foreach (var pending in _pendingCalls.Values)
        {
            pending.TrySetException(exception);
        }
    }

    private uint NextSerial()
    {
        var serial = Interlocked.Increment(ref _nextSerial);
        if (serial <= 0)
        {
            return NextSerial();
        }

        return checked((uint)serial);
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        await SendAuthLineAsync("\0AUTH EXTERNAL " + ToHexAscii(getuid().ToString()) + "\r\n", cancellationToken);
        var line = await ReadAuthLineAsync(cancellationToken);
        if (!line.StartsWith("OK ", StringComparison.Ordinal))
        {
            throw new DbusException($"D-Bus authentication failed: {line}");
        }

        await SendAuthLineAsync("NEGOTIATE_UNIX_FD\r\n", cancellationToken);
        line = await ReadAuthLineAsync(cancellationToken);
        if (!line.StartsWith("AGREE_UNIX_FD", StringComparison.Ordinal) &&
            !line.StartsWith("ERROR", StringComparison.Ordinal))
        {
            throw new DbusException($"D-Bus unix-fd negotiation failed: {line}");
        }

        await SendAuthLineAsync("BEGIN\r\n", cancellationToken);
    }

    private async Task SendAuthLineAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(line);
        await _socket.SendAsync(bytes, SocketFlags.None, cancellationToken);
    }

    private async Task<string> ReadAuthLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var received = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (received == 0)
            {
                throw new DbusException("D-Bus authentication stream closed.");
            }

            if (buffer[0] == (byte)'\n')
            {
                break;
            }

            if (buffer[0] != (byte)'\r')
            {
                bytes.Add(buffer[0]);
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static object? ConvertReplyBody(object?[] body, string outputSignature, Type returnType)
    {
        var outputTypes = DbusSignature.ParseMany(outputSignature);
        if (outputTypes.Count == 0 || returnType == typeof(void))
        {
            return null;
        }

        if (outputTypes.Count == 1)
        {
            return DbusValueConverter.ConvertTo(body[0], returnType);
        }

        return DbusValueConverter.CreateTuple(returnType, body);
    }

    private static string BuildMatchRule(string path, string interfaceName, string member, string? firstArgument)
    {
        var builder = new StringBuilder();
        builder.Append("type='signal'");
        builder.Append(",path='").Append(EscapeMatchValue(path)).Append('\'');
        builder.Append(",interface='").Append(EscapeMatchValue(interfaceName)).Append('\'');
        builder.Append(",member='").Append(EscapeMatchValue(member)).Append('\'');
        if (!string.IsNullOrEmpty(firstArgument))
        {
            builder.Append(",arg0='").Append(EscapeMatchValue(firstArgument)).Append('\'');
        }

        return builder.ToString();
    }

    private static string EscapeMatchValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static UnixDomainSocketEndPoint ResolveEndpoint(DbusBusKind busKind)
    {
        var address = busKind == DbusBusKind.System
            ? Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS")
            : Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");

        if (string.IsNullOrWhiteSpace(address) && busKind == DbusBusKind.System)
        {
            return new UnixDomainSocketEndPoint("/run/dbus/system_bus_socket");
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new DbusException("DBUS_SESSION_BUS_ADDRESS is not set.");
        }

        foreach (var candidate in address.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!candidate.StartsWith("unix:", StringComparison.Ordinal))
            {
                continue;
            }

            var values = ParseAddressProperties(candidate["unix:".Length..]);
            if (values.TryGetValue("path", out var path))
            {
                return new UnixDomainSocketEndPoint(path);
            }

            if (values.TryGetValue("abstract", out var abstractPath))
            {
                return new UnixDomainSocketEndPoint("\0" + abstractPath);
            }
        }

        throw new DbusException($"Unsupported D-Bus address '{address}'.");
    }

    private static Dictionary<string, string> ParseAddressProperties(string address)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in address.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            values[segment[..equalsIndex]] = Uri.UnescapeDataString(segment[(equalsIndex + 1)..]);
        }

        return values;
    }

    private static string ToHexAscii(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        return string.Concat(bytes.Select(static item => item.ToString("x2")));
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposeCts.IsCancellationRequested)
        {
            return;
        }

        _disposeCts.Cancel();
        _socket.Dispose();
        _sendLock.Dispose();
        _disposeCts.Dispose();
    }

    [DllImport("libc")]
    private static extern uint getuid();

    private sealed class DbusSignalSubscription(
        DbusConnection connection,
        string rule,
        string path,
        string interfaceName,
        string member,
        string signature,
        Action<DbusMessage> handler,
        Action<Exception>? onError) : IDisposable
    {
        private int _disposed;

        public string Rule { get; } = rule;

        public Action<DbusMessage> Handler { get; } = handler;

        public Action<Exception>? OnError { get; } = onError;

        public bool Matches(DbusMessage message)
        {
            return string.Equals(message.Path, path, StringComparison.Ordinal) &&
                   string.Equals(message.Interface, interfaceName, StringComparison.Ordinal) &&
                   string.Equals(message.Member, member, StringComparison.Ordinal) &&
                   string.Equals(message.Signature, signature, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ = connection.RemoveSignalSubscriptionAsync(this);
        }
    }
}

internal sealed class DbusDispatchProxy : DispatchProxy
{
    private DbusConnection _connection = null!;
    private Type _interfaceType = null!;
    private string _serviceName = string.Empty;
    private DbusObjectPath _objectPath;
    private string _interfaceName = string.Empty;
    private Type _propertiesType = typeof(object);

    public void Initialize(DbusConnection connection, Type interfaceType, string serviceName, DbusObjectPath objectPath)
    {
        _connection = connection;
        _interfaceType = interfaceType;
        _serviceName = serviceName;
        _objectPath = objectPath;
        _interfaceName = interfaceType.GetCustomAttribute<DbusInterfaceAttribute>()?.InterfaceName
            ?? throw new DbusException($"Interface '{interfaceType}' is missing DbusInterfaceAttribute.");
        _propertiesType = interfaceType.GetCustomAttribute<DbusPropertiesAttribute>()?.PropertiesType ?? typeof(object);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new DbusException("D-Bus proxy method is missing.");
        }

        args ??= [];
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task))
        {
            return InvokeTaskAsync(targetMethod, args);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            return InvokeGenericTaskMethod
                .MakeGenericMethod(resultType)
                .Invoke(this, [targetMethod, args]);
        }

        throw new DbusException($"D-Bus proxy method '{targetMethod.Name}' must return Task or Task<T>.");
    }

    private async Task InvokeTaskAsync(MethodInfo method, object?[] args)
    {
        await InvokeCoreAsync(method, args, typeof(void)).ConfigureAwait(false);
    }

    private async Task<TResult> InvokeTaskAsync<TResult>(MethodInfo method, object?[] args)
    {
        var result = await InvokeCoreAsync(method, args, typeof(TResult)).ConfigureAwait(false);
        return (TResult)DbusValueConverter.ConvertTo(result, typeof(TResult))!;
    }

    private async Task<object?> InvokeCoreAsync(MethodInfo method, object?[] args, Type resultType)
    {
        if (method.Name == "GetAsync" && method.IsGenericMethod)
        {
            return await GetPropertyAsync((string)args[0]!, resultType).ConfigureAwait(false);
        }

        if (method.Name == "GetAllAsync")
        {
            return await GetAllPropertiesAsync(resultType).ConfigureAwait(false);
        }

        if (method.Name == "SetAsync")
        {
            await SetPropertyAsync((string)args[0]!, args[1]).ConfigureAwait(false);
            return null;
        }

        if (method.Name == "WatchPropertiesAsync")
        {
            return await WatchPropertiesAsync((Action<DbusPropertyChanges>)args[0]!).ConfigureAwait(false);
        }

        var signal = method.GetCustomAttribute<DbusSignalAttribute>();
        if (signal is not null)
        {
            return await WatchSignalAsync(method, signal, args).ConfigureAwait(false);
        }

        var dbusMethod = method.GetCustomAttribute<DbusMethodAttribute>()
            ?? throw new DbusException($"D-Bus method '{method.Name}' is missing DbusMethodAttribute.");
        return await _connection.InvokeMethodAsync(
            _serviceName,
            _objectPath.Value,
            _interfaceName,
            dbusMethod.MemberName,
            dbusMethod.InputSignature,
            args,
            dbusMethod.OutputSignature,
            resultType,
            dbusMethod.NoReply,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<object?> GetPropertyAsync(string propertyName, Type resultType)
    {
        var reply = await _connection.CallMethodAsync(
            _serviceName,
            _objectPath.Value,
            "org.freedesktop.DBus.Properties",
            "Get",
            "ss",
            [_interfaceName, propertyName],
            "v",
            noReply: false,
            CancellationToken.None).ConfigureAwait(false);
        return DbusValueConverter.ConvertTo(reply.Body[0], resultType);
    }

    private async Task<object> GetAllPropertiesAsync(Type resultType)
    {
        var reply = await _connection.CallMethodAsync(
            _serviceName,
            _objectPath.Value,
            "org.freedesktop.DBus.Properties",
            "GetAll",
            "s",
            [_interfaceName],
            "a{sv}",
            noReply: false,
            CancellationToken.None).ConfigureAwait(false);
        var values = (IReadOnlyDictionary<string, object?>)DbusValueConverter.ConvertTo(reply.Body[0], typeof(IReadOnlyDictionary<string, object?>))!;
        return CreatePropertiesObject(resultType, values);
    }

    private async Task SetPropertyAsync(string propertyName, object? value)
    {
        var signature = FindPropertySignature(propertyName) ?? DbusVariantFactory.Create(value).Signature;
        await _connection.CallMethodAsync(
            _serviceName,
            _objectPath.Value,
            "org.freedesktop.DBus.Properties",
            "Set",
            "ssv",
            [_interfaceName, propertyName, new DbusVariant(signature, value)],
            string.Empty,
            noReply: false,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IDisposable> WatchPropertiesAsync(Action<DbusPropertyChanges> handler)
    {
        return await _connection.AddSignalSubscriptionAsync(
            _objectPath.Value,
            "org.freedesktop.DBus.Properties",
            "PropertiesChanged",
            "sa{sv}as",
            message =>
            {
                if (message.Body.Length != 3 ||
                    !string.Equals(Convert.ToString(message.Body[0]), _interfaceName, StringComparison.Ordinal))
                {
                    return;
                }

                var changed = (IReadOnlyDictionary<string, object?>)DbusValueConverter.ConvertTo(message.Body[1], typeof(IReadOnlyDictionary<string, object?>))!;
                var invalidated = (string[])DbusValueConverter.ConvertTo(message.Body[2], typeof(string[]))!;
                handler(new DbusPropertyChanges(changed, invalidated));
            },
            null,
            _interfaceName).ConfigureAwait(false);
    }

    private async Task<IDisposable> WatchSignalAsync(MethodInfo method, DbusSignalAttribute signal, object?[] args)
    {
        var handler = (Delegate)args[0]!;
        var onError = args.Length > 1 ? args[1] as Action<Exception> : null;
        return await _connection.AddSignalSubscriptionAsync(
            _objectPath.Value,
            _interfaceName,
            signal.MemberName,
            signal.Signature,
            message => InvokeSignalHandler(method, handler, message),
            onError).ConfigureAwait(false);
    }

    private static void InvokeSignalHandler(MethodInfo method, Delegate handler, DbusMessage message)
    {
        var invoke = handler.GetType().GetMethod("Invoke")
            ?? throw new DbusException($"Signal handler for '{method.Name}' has no Invoke method.");
        var parameters = invoke.GetParameters();
        if (parameters.Length == 0)
        {
            handler.DynamicInvoke();
            return;
        }

        if (parameters.Length == 1)
        {
            var value = message.Body.Length == 1
                ? DbusValueConverter.ConvertTo(message.Body[0], parameters[0].ParameterType)
                : DbusValueConverter.CreateTuple(parameters[0].ParameterType, message.Body);
            handler.DynamicInvoke(value);
            return;
        }

        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            arguments[i] = DbusValueConverter.ConvertTo(message.Body[i], parameters[i].ParameterType);
        }

        handler.DynamicInvoke(arguments);
    }

    private static object CreatePropertiesObject(Type resultType, IReadOnlyDictionary<string, object?> values)
    {
        var instance = Activator.CreateInstance(resultType)
            ?? throw new DbusException($"Unable to create D-Bus properties object '{resultType}'.");
        foreach (var property in resultType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var attribute = property.GetCustomAttribute<DbusPropertyAttribute>();
            if (attribute is null || !property.CanWrite || !values.TryGetValue(attribute.MemberName, out var value))
            {
                continue;
            }

            property.SetValue(instance, DbusValueConverter.ConvertTo(value, property.PropertyType));
        }

        return instance;
    }

    private string? FindPropertySignature(string propertyName)
    {
        foreach (var property in _propertiesType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var attribute = property.GetCustomAttribute<DbusPropertyAttribute>();
            if (attribute is not null && string.Equals(attribute.MemberName, propertyName, StringComparison.Ordinal))
            {
                return attribute.Signature;
            }
        }

        return null;
    }

    private static readonly MethodInfo InvokeGenericTaskMethod =
        typeof(DbusDispatchProxy).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static method => method.Name == nameof(InvokeTaskAsync) && method.IsGenericMethodDefinition);
}
