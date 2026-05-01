using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dbus.Contracts;

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class DbusInterfaceAttribute(string interfaceName) : Attribute
{
    public string InterfaceName { get; } = string.IsNullOrWhiteSpace(interfaceName)
        ? throw new ArgumentException("D-Bus interface name cannot be empty.", nameof(interfaceName))
        : interfaceName;
}

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class DbusPropertiesAttribute(Type propertiesType) : Attribute
{
    public Type PropertiesType { get; } = propertiesType ?? throw new ArgumentNullException(nameof(propertiesType));
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DbusMethodAttribute(string memberName, string inputSignature, string outputSignature, bool noReply = false) : Attribute
{
    public string MemberName { get; } = string.IsNullOrWhiteSpace(memberName)
        ? throw new ArgumentException("D-Bus method name cannot be empty.", nameof(memberName))
        : memberName;

    public string InputSignature { get; } = inputSignature ?? throw new ArgumentNullException(nameof(inputSignature));

    public string OutputSignature { get; } = outputSignature ?? throw new ArgumentNullException(nameof(outputSignature));

    public bool NoReply { get; } = noReply;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DbusSignalAttribute(string memberName, string signature) : Attribute
{
    public string MemberName { get; } = string.IsNullOrWhiteSpace(memberName)
        ? throw new ArgumentException("D-Bus signal name cannot be empty.", nameof(memberName))
        : memberName;

    public string Signature { get; } = signature ?? throw new ArgumentNullException(nameof(signature));
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class DbusPropertyAttribute(string memberName, string signature) : Attribute
{
    public string MemberName { get; } = string.IsNullOrWhiteSpace(memberName)
        ? throw new ArgumentException("D-Bus property name cannot be empty.", nameof(memberName))
        : memberName;

    public string Signature { get; } = signature ?? throw new ArgumentNullException(nameof(signature));
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class DbusDictionaryAttribute : Attribute;

public interface IDbusObject;

public enum DbusBusKind
{
    System,
    Session
}

public readonly record struct DbusObjectPath
{
    public DbusObjectPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("D-Bus object path cannot be empty.", nameof(value));
        }

        if (value[0] != '/')
        {
            throw new ArgumentException("D-Bus object path must start with '/'.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }

    public static implicit operator DbusObjectPath(string value)
    {
        return new DbusObjectPath(value);
    }

    public static implicit operator string(DbusObjectPath value)
    {
        return value.Value;
    }
}

public sealed class CloseSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public CloseSafeHandle()
        : base(ownsHandle: true)
    {
    }

    public CloseSafeHandle(IntPtr preexistingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    internal int DangerousFileDescriptor => (int)handle;

    protected override bool ReleaseHandle()
    {
        return close(handle) == 0;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int close(IntPtr fd);
}

public sealed class DbusPropertyChanges
{
    public DbusPropertyChanges()
        : this(new Dictionary<string, object?>(), Array.Empty<string>())
    {
    }

    public DbusPropertyChanges(
        IReadOnlyDictionary<string, object?> changed,
        IReadOnlyCollection<string> invalidated)
    {
        Changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Invalidated = invalidated ?? throw new ArgumentNullException(nameof(invalidated));
    }

    public IReadOnlyDictionary<string, object?> Changed { get; }

    public IReadOnlyCollection<string> Invalidated { get; }
}

public sealed record DbusVariant(string Signature, object? Value);

public class DbusException : Exception
{
    public DbusException(string message)
        : base(message)
    {
    }

    public DbusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DbusRemoteException(string errorName, string message) : DbusException(message)
{
    public string ErrorName { get; } = errorName;
}
