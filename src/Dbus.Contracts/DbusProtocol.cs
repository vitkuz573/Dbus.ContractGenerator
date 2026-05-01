using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dbus.Contracts;

internal enum DbusMessageType : byte
{
    MethodCall = 1,
    MethodReturn = 2,
    Error = 3,
    Signal = 4
}

internal enum DbusHeaderField : byte
{
    Path = 1,
    Interface = 2,
    Member = 3,
    ErrorName = 4,
    ReplySerial = 5,
    Destination = 6,
    Sender = 7,
    Signature = 8,
    UnixFds = 9
}

internal sealed class DbusMessage
{
    public DbusMessageType Type { get; init; }

    public byte Flags { get; init; }

    public uint Serial { get; init; }

    public uint ReplySerial { get; init; }

    public string? Path { get; init; }

    public string? Interface { get; init; }

    public string? Member { get; init; }

    public string? Destination { get; init; }

    public string? Sender { get; init; }

    public string? ErrorName { get; init; }

    public string Signature { get; init; } = string.Empty;

    public object?[] Body { get; init; } = [];

    public int[] UnixFileDescriptors { get; init; } = [];
}

internal abstract class DbusTypeNode
{
    private DbusTypeNode()
    {
    }

    public abstract int Alignment { get; }

    public abstract string Signature { get; }

    public sealed class Primitive(char code) : DbusTypeNode
    {
        public char Code { get; } = code;

        public override int Alignment => Code switch
        {
            'y' or 'g' or 'v' => 1,
            'n' or 'q' => 2,
            'x' or 't' or 'd' => 8,
            _ => 4
        };

        public override string Signature => Code.ToString();
    }

    public sealed class Array(DbusTypeNode element) : DbusTypeNode
    {
        public DbusTypeNode Element { get; } = element;

        public override int Alignment => 4;

        public override string Signature => "a" + Element.Signature;
    }

    public sealed class Dict(DbusTypeNode key, DbusTypeNode value) : DbusTypeNode
    {
        public DbusTypeNode Key { get; } = key;

        public DbusTypeNode Value { get; } = value;

        public override int Alignment => 4;

        public override string Signature => "a{" + Key.Signature + Value.Signature + "}";
    }

    public sealed class Struct(IReadOnlyList<DbusTypeNode> fields) : DbusTypeNode
    {
        public IReadOnlyList<DbusTypeNode> Fields { get; } = fields;

        public override int Alignment => 8;

        public override string Signature => "(" + string.Concat(Fields.Select(static item => item.Signature)) + ")";
    }
}

internal static class DbusSignature
{
    public static IReadOnlyList<DbusTypeNode> ParseMany(string signature)
    {
        if (signature.Length > 255)
        {
            throw new DbusException("D-Bus signature exceeds 255 bytes.");
        }

        var parser = new Parser(signature);
        var nodes = new List<DbusTypeNode>();
        while (!parser.IsAtEnd)
        {
            nodes.Add(parser.ParseSingle());
        }

        return nodes;
    }

    public static DbusTypeNode ParseSingle(string signature)
    {
        var parser = new Parser(signature);
        var node = parser.ParseSingle();
        if (!parser.IsAtEnd)
        {
            throw new DbusException($"D-Bus signature '{signature}' contains more than one root type.");
        }

        return node;
    }

    private sealed class Parser(string signature)
    {
        private int _position;

        public bool IsAtEnd => _position >= signature.Length;

        public DbusTypeNode ParseSingle()
        {
            if (IsAtEnd)
            {
                throw new DbusException("D-Bus signature ended unexpectedly.");
            }

            var current = signature[_position++];
            return current switch
            {
                'a' when !IsAtEnd && signature[_position] == '{' => ParseDict(),
                'a' => new DbusTypeNode.Array(ParseSingle()),
                '(' => ParseStruct(),
                'y' or 'b' or 'n' or 'q' or 'i' or 'u' or 'x' or 't' or 'd' or 's' or 'o' or 'g' or 'h' or 'v'
                    => new DbusTypeNode.Primitive(current),
                _ => throw new DbusException($"Unsupported D-Bus signature token '{current}'.")
            };
        }

        private DbusTypeNode ParseDict()
        {
            _position++;
            var key = ParseSingle();
            var value = ParseSingle();
            Expect('}');
            return new DbusTypeNode.Dict(key, value);
        }

        private DbusTypeNode ParseStruct()
        {
            var fields = new List<DbusTypeNode>();
            while (true)
            {
                if (IsAtEnd)
                {
                    throw new DbusException("Unterminated D-Bus struct signature.");
                }

                if (signature[_position] == ')')
                {
                    _position++;
                    break;
                }

                fields.Add(ParseSingle());
            }

            return new DbusTypeNode.Struct(fields);
        }

        private void Expect(char expected)
        {
            if (IsAtEnd || signature[_position] != expected)
            {
                throw new DbusException($"Expected '{expected}' in D-Bus signature.");
            }

            _position++;
        }
    }
}

internal sealed class DbusWriter
{
    private readonly MemoryStream _stream = new();
    private readonly List<int> _unixFileDescriptors;

    public DbusWriter()
        : this([])
    {
    }

    public DbusWriter(List<int> unixFileDescriptors)
    {
        _unixFileDescriptors = unixFileDescriptors;
    }

    public int Position => checked((int)_stream.Position);

    public IReadOnlyList<int> UnixFileDescriptors => _unixFileDescriptors;

    public byte[] ToArray()
    {
        return _stream.ToArray();
    }

    public void Align(int alignment)
    {
        var padding = Padding(Position, alignment);
        for (var i = 0; i < padding; i++)
        {
            _stream.WriteByte(0);
        }
    }

    public static int Align(int value, int alignment)
    {
        return value + Padding(value, alignment);
    }

    public static int Padding(int value, int alignment)
    {
        return (alignment - value % alignment) % alignment;
    }

    public void WriteByte(byte value)
    {
        _stream.WriteByte(value);
    }

    public void WriteUInt32(uint value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 24));
    }

    public void WriteInt32(int value)
    {
        WriteUInt32(unchecked((uint)value));
    }

    public void WriteUInt16(ushort value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
    }

    public void WriteInt16(short value)
    {
        WriteUInt16(unchecked((ushort)value));
    }

    public void WriteUInt64(ulong value)
    {
        WriteUInt32((uint)value);
        WriteUInt32((uint)(value >> 32));
    }

    public void WriteInt64(long value)
    {
        WriteUInt64(unchecked((ulong)value));
    }

    public void WriteDouble(double value)
    {
        WriteUInt64(BitConverter.DoubleToUInt64Bits(value));
    }

    public void WriteUInt32At(int offset, uint value)
    {
        var position = _stream.Position;
        _stream.Position = offset;
        WriteUInt32(value);
        _stream.Position = position;
    }

    public void WriteStringLike(string value, bool signature)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value)));
        if (signature)
        {
            if (bytes.Length > 255)
            {
                throw new DbusException("D-Bus signature value exceeds 255 bytes.");
            }

            WriteByte((byte)bytes.Length);
        }
        else
        {
            WriteUInt32(checked((uint)bytes.Length));
        }

        _stream.Write(bytes, 0, bytes.Length);
        WriteByte(0);
    }

    public void WriteValue(DbusTypeNode type, object? value)
    {
        switch (type)
        {
            case DbusTypeNode.Primitive primitive:
                WritePrimitive(primitive.Code, value);
                break;

            case DbusTypeNode.Array array:
                WriteArray(array.Element, value);
                break;

            case DbusTypeNode.Dict dict:
                WriteDictionary(dict.Key, dict.Value, value);
                break;

            case DbusTypeNode.Struct structure:
                WriteStruct(structure.Fields, value);
                break;

            default:
                throw new DbusException("Unexpected D-Bus type node.");
        }
    }

    private void WritePrimitive(char code, object? value)
    {
        var type = new DbusTypeNode.Primitive(code);
        Align(type.Alignment);

        switch (code)
        {
            case 'y':
                WriteByte(Convert.ToByte(value, CultureInfo.InvariantCulture));
                break;
            case 'b':
                WriteUInt32(Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1u : 0u);
                break;
            case 'n':
                WriteInt16(Convert.ToInt16(value, CultureInfo.InvariantCulture));
                break;
            case 'q':
                WriteUInt16(Convert.ToUInt16(value, CultureInfo.InvariantCulture));
                break;
            case 'i':
                WriteInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case 'u':
                WriteUInt32(Convert.ToUInt32(value, CultureInfo.InvariantCulture));
                break;
            case 'x':
                WriteInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case 't':
                WriteUInt64(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                break;
            case 'd':
                WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case 's':
                WriteStringLike(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, signature: false);
                break;
            case 'o':
                WriteStringLike(value is DbusObjectPath path ? path.Value : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "/", signature: false);
                break;
            case 'g':
                WriteStringLike(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, signature: true);
                break;
            case 'h':
                WriteUnixFileDescriptor(value);
                break;
            case 'v':
                WriteVariant(value);
                break;
            default:
                throw new DbusException($"Unsupported D-Bus primitive '{code}'.");
        }
    }

    private void WriteUnixFileDescriptor(object? value)
    {
        if (value is not CloseSafeHandle handle)
        {
            throw new DbusException("D-Bus unix fd value must be CloseSafeHandle.");
        }

        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new DbusException("Cannot send an invalid D-Bus unix fd handle.");
        }

        var index = _unixFileDescriptors.Count;
        _unixFileDescriptors.Add(handle.DangerousFileDescriptor);
        WriteUInt32(checked((uint)index));
    }

    private void WriteVariant(object? value)
    {
        var variant = DbusVariantFactory.Create(value);
        WriteStringLike(variant.Signature, signature: true);
        WriteValue(DbusSignature.ParseSingle(variant.Signature), variant.Value);
    }

    private void WriteArray(DbusTypeNode elementType, object? value)
    {
        Align(4);
        var lengthOffset = Position;
        WriteUInt32(0);
        Align(elementType.Alignment);
        var start = Position;

        if (value is not null)
        {
            foreach (var item in EnumerateValues(value))
            {
                WriteValue(elementType, item);
            }
        }

        var length = checked((uint)(Position - start));
        WriteUInt32At(lengthOffset, length);
    }

    private void WriteDictionary(DbusTypeNode keyType, DbusTypeNode valueType, object? value)
    {
        Align(4);
        var lengthOffset = Position;
        WriteUInt32(0);
        Align(8);
        var start = Position;

        if (value is not null)
        {
            foreach (DictionaryEntry entry in ToDictionaryEntries(value))
            {
                Align(8);
                WriteValue(keyType, entry.Key);
                WriteValue(valueType, entry.Value);
            }
        }

        var length = checked((uint)(Position - start));
        WriteUInt32At(lengthOffset, length);
    }

    private void WriteStruct(IReadOnlyList<DbusTypeNode> fields, object? value)
    {
        Align(8);
        var values = DeconstructValue(value, fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            WriteValue(fields[i], values[i]);
        }
    }

    private static IEnumerable<object?> EnumerateValues(object value)
    {
        if (value is string)
        {
            throw new DbusException("String cannot be encoded as a D-Bus array.");
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        throw new DbusException($"Value of type '{value.GetType()}' cannot be encoded as a D-Bus array.");
    }

    private static IEnumerable<DictionaryEntry> ToDictionaryEntries(object value)
    {
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                yield return entry;
            }

            yield break;
        }

        var enumerable = value as IEnumerable ?? throw new DbusException($"Value of type '{value.GetType()}' cannot be encoded as a D-Bus dictionary.");
        foreach (var item in enumerable)
        {
            var values = DeconstructValue(item, 2);
            yield return new DictionaryEntry(values[0]!, values[1]);
        }
    }

    private static object?[] DeconstructValue(object? value, int count)
    {
        if (count == 0)
        {
            return [];
        }

        if (value is null)
        {
            throw new DbusException("Cannot encode null as a D-Bus struct.");
        }

        if (value is ITuple tuple)
        {
            if (tuple.Length != count)
            {
                throw new DbusException($"Tuple field count {tuple.Length} does not match D-Bus struct field count {count}.");
            }

            var values = new object?[count];
            for (var i = 0; i < count; i++)
            {
                values[i] = tuple[i];
            }

            return values;
        }

        var fields = value
            .GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(static field => field.Name.StartsWith("Item", StringComparison.Ordinal))
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .ToArray();
        if (fields.Length == count)
        {
            return fields.Select(field => field.GetValue(value)).ToArray();
        }

        throw new DbusException($"Value of type '{value.GetType()}' cannot be encoded as a D-Bus struct.");
    }
}

internal sealed class DbusReader
{
    private readonly byte[] _buffer;
    private readonly int[] _unixFileDescriptors;
    private int _position;

    public DbusReader(byte[] buffer, int start, int[] unixFileDescriptors)
    {
        _buffer = buffer;
        _position = start;
        _unixFileDescriptors = unixFileDescriptors;
    }

    public int Position => _position;

    public void Align(int alignment)
    {
        _position = DbusWriter.Align(_position, alignment);
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        var value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
        _position += 2;
        return value;
    }

    public short ReadInt16()
    {
        return unchecked((short)ReadUInt16());
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        var value = (uint)(_buffer[_position] |
                           (_buffer[_position + 1] << 8) |
                           (_buffer[_position + 2] << 16) |
                           (_buffer[_position + 3] << 24));
        _position += 4;
        return value;
    }

    public int ReadInt32()
    {
        return unchecked((int)ReadUInt32());
    }

    public ulong ReadUInt64()
    {
        var low = ReadUInt32();
        var high = ReadUInt32();
        return low | ((ulong)high << 32);
    }

    public long ReadInt64()
    {
        return unchecked((long)ReadUInt64());
    }

    public double ReadDouble()
    {
        return BitConverter.UInt64BitsToDouble(ReadUInt64());
    }

    public string ReadStringLike(bool signature)
    {
        var length = signature ? ReadByte() : checked((int)ReadUInt32());
        EnsureAvailable(length + 1);
        var value = Encoding.UTF8.GetString(_buffer, _position, length);
        _position += length;
        if (_buffer[_position++] != 0)
        {
            throw new DbusException("D-Bus string is not nul-terminated.");
        }

        return value;
    }

    public object? ReadValue(DbusTypeNode type, Type targetType)
    {
        switch (type)
        {
            case DbusTypeNode.Primitive primitive:
                return ReadPrimitive(primitive.Code, targetType);
            case DbusTypeNode.Array array:
                return ReadArray(array.Element, targetType);
            case DbusTypeNode.Dict dict:
                return ReadDictionary(dict.Key, dict.Value, targetType);
            case DbusTypeNode.Struct structure:
                return ReadStruct(structure.Fields, targetType);
            default:
                throw new DbusException("Unexpected D-Bus type node.");
        }
    }

    private object? ReadPrimitive(char code, Type targetType)
    {
        Align(new DbusTypeNode.Primitive(code).Alignment);

        object? value = code switch
        {
            'y' => ReadByte(),
            'b' => ReadUInt32() != 0,
            'n' => ReadInt16(),
            'q' => ReadUInt16(),
            'i' => ReadInt32(),
            'u' => ReadUInt32(),
            'x' => ReadInt64(),
            't' => ReadUInt64(),
            'd' => ReadDouble(),
            's' => ReadStringLike(signature: false),
            'o' => new DbusObjectPath(ReadStringLike(signature: false)),
            'g' => ReadStringLike(signature: true),
#pragma warning disable CA2000
            'h' => ReadUnixFileDescriptor(),
#pragma warning restore CA2000
            'v' => ReadVariant(),
            _ => throw new DbusException($"Unsupported D-Bus primitive '{code}'.")
        };

        return DbusValueConverter.ConvertTo(value, targetType);
    }

    private CloseSafeHandle ReadUnixFileDescriptor()
    {
        var index = checked((int)ReadUInt32());
        if (index < 0 || index >= _unixFileDescriptors.Length)
        {
            throw new DbusException($"D-Bus unix fd index {index} is outside the received fd table.");
        }

        return new CloseSafeHandle((IntPtr)_unixFileDescriptors[index], ownsHandle: true);
    }

    private DbusVariant ReadVariant()
    {
        var signature = ReadStringLike(signature: true);
        var value = ReadValue(DbusSignature.ParseSingle(signature), typeof(object));
        return new DbusVariant(signature, value);
    }

    private object ReadArray(DbusTypeNode elementType, Type targetType)
    {
        Align(4);
        var length = checked((int)ReadUInt32());
        Align(elementType.Alignment);
        var end = checked(_position + length);
        var elementTargetType = GetElementType(targetType) ?? typeof(object);
        var values = new List<object?>();
        while (_position < end)
        {
            values.Add(ReadValue(elementType, elementTargetType));
        }

        if (_position != end)
        {
            throw new DbusException("D-Bus array reader overran the declared array length.");
        }

        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementTargetType, values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                array.SetValue(DbusValueConverter.ConvertTo(values[i], elementTargetType), i);
            }

            return array;
        }

        return values.ToArray();
    }

    private object ReadDictionary(DbusTypeNode keyType, DbusTypeNode valueType, Type targetType)
    {
        Align(4);
        var length = checked((int)ReadUInt32());
        Align(8);
        var end = checked(_position + length);
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (_position < end)
        {
            Align(8);
            var key = ReadValue(keyType, typeof(string));
            var value = ReadValue(valueType, typeof(object));
            dictionary[Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty] = value is DbusVariant variant ? variant.Value : value;
        }

        if (_position != end)
        {
            throw new DbusException("D-Bus dictionary reader overran the declared dictionary length.");
        }

        return DbusValueConverter.ConvertTo(dictionary, targetType) ?? dictionary;
    }

    private object? ReadStruct(IReadOnlyList<DbusTypeNode> fields, Type targetType)
    {
        Align(8);
        var tupleTypes = GetTupleElementTypes(targetType, fields.Count);
        var values = new object?[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            values[i] = ReadValue(fields[i], tupleTypes[i]);
        }

        return DbusValueConverter.CreateTuple(targetType, values);
    }

    private void EnsureAvailable(int length)
    {
        if (_position + length > _buffer.Length)
        {
            throw new DbusException("D-Bus message ended unexpectedly.");
        }
    }

    private static Type? GetElementType(Type targetType)
    {
        if (targetType.IsArray)
        {
            return targetType.GetElementType();
        }

        if (targetType.IsGenericType &&
            targetType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return targetType.GetGenericArguments()[0];
        }

        return null;
    }

    private static Type[] GetTupleElementTypes(Type targetType, int count)
    {
        if (targetType.IsGenericType && targetType.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
        {
            var arguments = targetType.GetGenericArguments();
            if (arguments.Length == count)
            {
                return arguments;
            }
        }

        return Enumerable.Repeat(typeof(object), count).ToArray();
    }
}

internal static class DbusVariantFactory
{
    public static DbusVariant Create(object? value)
    {
        if (value is DbusVariant variant)
        {
            return variant;
        }

        if (value is null)
        {
            return new DbusVariant("s", string.Empty);
        }

        return new DbusVariant(InferSignature(value), value);
    }

    public static string InferSignature(object value)
    {
        return value switch
        {
            byte => "y",
            bool => "b",
            short => "n",
            ushort => "q",
            int => "i",
            uint => "u",
            long => "x",
            ulong => "t",
            double => "d",
            string => "s",
            DbusObjectPath => "o",
            CloseSafeHandle => "h",
            IDictionary dictionary => InferDictionarySignature(dictionary),
            IEnumerable enumerable when value is not string => InferArraySignature(enumerable),
            ITuple tuple => "(" + string.Concat(Enumerable.Range(0, tuple.Length).Select(index => InferSignature(tuple[index] ?? string.Empty))) + ")",
            _ => throw new DbusException($"Cannot infer D-Bus signature for CLR type '{value.GetType()}'.")
        };
    }

    private static string InferDictionarySignature(IDictionary dictionary)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            return "a{" + InferSignature(entry.Key ?? string.Empty) + "v}";
        }

        return "a{sv}";
    }

    private static string InferArraySignature(IEnumerable enumerable)
    {
        foreach (var item in enumerable)
        {
            return "a" + InferSignature(item ?? string.Empty);
        }

        var type = enumerable.GetType();
        var elementType = type.IsArray ? type.GetElementType() : null;
        return elementType is null ? "av" : "a" + InferSignatureForType(elementType);
    }

    private static string InferSignatureForType(Type type)
    {
        if (type == typeof(byte))
        {
            return "y";
        }

        if (type == typeof(bool))
        {
            return "b";
        }

        if (type == typeof(short))
        {
            return "n";
        }

        if (type == typeof(ushort))
        {
            return "q";
        }

        if (type == typeof(int))
        {
            return "i";
        }

        if (type == typeof(uint))
        {
            return "u";
        }

        if (type == typeof(long))
        {
            return "x";
        }

        if (type == typeof(ulong))
        {
            return "t";
        }

        if (type == typeof(double))
        {
            return "d";
        }

        if (type == typeof(string))
        {
            return "s";
        }

        if (type == typeof(DbusObjectPath))
        {
            return "o";
        }

        if (type == typeof(CloseSafeHandle))
        {
            return "h";
        }

        return "v";
    }
}

internal static class DbusValueConverter
{
    public static object? ConvertTo(object? value, Type targetType)
    {
        if (targetType == typeof(void) || targetType == typeof(object))
        {
            return value is DbusVariant variant ? variant.Value : value;
        }

        if (value is DbusVariant boxed)
        {
            value = boxed.Value;
        }

        if (value is null)
        {
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType == typeof(string))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (targetType == typeof(DbusObjectPath))
        {
            return value is string path ? new DbusObjectPath(path) : value;
        }

        if (targetType.IsArray && value is object?[] values)
        {
            var elementType = targetType.GetElementType() ?? typeof(object);
            var array = Array.CreateInstance(elementType, values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                array.SetValue(ConvertTo(values[i], elementType), i);
            }

            return array;
        }

        if (targetType.IsGenericType &&
            targetType.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal) &&
            value is object?[] tupleValues)
        {
            return CreateTuple(targetType, tupleValues);
        }

        if (IsDictionaryType(targetType) && value is IReadOnlyDictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        if (targetType.IsEnum)
        {
            return Enum.ToObject(targetType, value);
        }

        return Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture);
    }

    public static object? CreateTuple(Type targetType, object?[] values)
    {
        if (targetType.IsGenericType && targetType.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
        {
            var arguments = targetType.GetGenericArguments();
            for (var i = 0; i < values.Length && i < arguments.Length; i++)
            {
                values[i] = ConvertTo(values[i], arguments[i]);
            }

            return Activator.CreateInstance(targetType, values);
        }

        return values;
    }

    private static bool IsDictionaryType(Type targetType)
    {
        if (!targetType.IsGenericType)
        {
            return false;
        }

        var definition = targetType.GetGenericTypeDefinition();
        return definition == typeof(IDictionary<,>) ||
               definition == typeof(IReadOnlyDictionary<,>) ||
               definition == typeof(Dictionary<,>);
    }
}
