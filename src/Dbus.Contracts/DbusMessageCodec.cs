namespace Dbus.Contracts;

internal static class DbusMessageCodec
{
    public static byte[] Encode(DbusMessage message, out int[] unixFileDescriptors)
    {
        var bodyWriter = new DbusWriter();
        var bodyTypes = DbusSignature.ParseMany(message.Signature);
        for (var i = 0; i < bodyTypes.Count; i++)
        {
            bodyWriter.WriteValue(bodyTypes[i], message.Body[i]);
        }

        var body = bodyWriter.ToArray();
        var headerWriter = new DbusWriter(bodyWriter.UnixFileDescriptors.ToList());
        headerWriter.WriteByte((byte)'l');
        headerWriter.WriteByte((byte)message.Type);
        headerWriter.WriteByte(message.Flags);
        headerWriter.WriteByte(1);
        headerWriter.WriteUInt32(checked((uint)body.Length));
        headerWriter.WriteUInt32(message.Serial);

        var fieldsLengthOffset = headerWriter.Position;
        headerWriter.WriteUInt32(0);
        headerWriter.Align(8);
        var fieldsStart = headerWriter.Position;

        WriteHeaderField(headerWriter, DbusHeaderField.Path, "o", message.Path);
        WriteHeaderField(headerWriter, DbusHeaderField.Interface, "s", message.Interface);
        WriteHeaderField(headerWriter, DbusHeaderField.Member, "s", message.Member);
        WriteHeaderField(headerWriter, DbusHeaderField.ErrorName, "s", message.ErrorName);
        if (message.ReplySerial != 0)
        {
            WriteHeaderField(headerWriter, DbusHeaderField.ReplySerial, "u", message.ReplySerial);
        }

        WriteHeaderField(headerWriter, DbusHeaderField.Destination, "s", message.Destination);
        WriteHeaderField(headerWriter, DbusHeaderField.Sender, "s", message.Sender);
        if (!string.IsNullOrEmpty(message.Signature))
        {
            WriteHeaderField(headerWriter, DbusHeaderField.Signature, "g", message.Signature);
        }

        if (headerWriter.UnixFileDescriptors.Count > 0)
        {
            WriteHeaderField(headerWriter, DbusHeaderField.UnixFds, "u", checked((uint)headerWriter.UnixFileDescriptors.Count));
        }

        headerWriter.WriteUInt32At(fieldsLengthOffset, checked((uint)(headerWriter.Position - fieldsStart)));
        headerWriter.Align(8);

        var header = headerWriter.ToArray();
        var encoded = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, encoded, 0, header.Length);
        Buffer.BlockCopy(body, 0, encoded, header.Length, body.Length);
        unixFileDescriptors = headerWriter.UnixFileDescriptors.ToArray();

        return encoded;
    }

    public static bool TryDecode(ReadOnlySpan<byte> input, int[] unixFileDescriptors, out DbusMessage message, out int consumed)
    {
        message = null!;
        consumed = 0;

        if (input.Length < 16)
        {
            return false;
        }

        if (input[0] != (byte)'l')
        {
            throw new DbusException("Only little-endian D-Bus messages are supported.");
        }

        var bodyLength = ReadUInt32(input, 4);
        var serial = ReadUInt32(input, 8);
        var fieldsLength = ReadUInt32(input, 12);
        var fieldsStart = DbusWriter.Align(16, 8);
        var fieldsEnd = checked(fieldsStart + (int)fieldsLength);
        var bodyStart = DbusWriter.Align(fieldsEnd, 8);
        var totalLength = checked(bodyStart + (int)bodyLength);
        if (input.Length < totalLength)
        {
            return false;
        }

        var buffer = input[..totalLength].ToArray();
        var reader = new DbusReader(buffer, 12, unixFileDescriptors);
        var headerFields = ReadHeaderFields(reader);
        headerFields.TryGetValue(DbusHeaderField.Signature, out var signatureValue);
        var signature = signatureValue as string ?? string.Empty;
        var bodyReader = new DbusReader(buffer, bodyStart, unixFileDescriptors);
        var bodyTypes = DbusSignature.ParseMany(signature);
        var body = new object?[bodyTypes.Count];
        for (var i = 0; i < bodyTypes.Count; i++)
        {
            body[i] = bodyReader.ReadValue(bodyTypes[i], typeof(object));
        }

        message = new DbusMessage
        {
            Type = (DbusMessageType)buffer[1],
            Flags = buffer[2],
            Serial = serial,
            ReplySerial = headerFields.TryGetValue(DbusHeaderField.ReplySerial, out var replySerial) ? Convert.ToUInt32(replySerial) : 0,
            Path = headerFields.TryGetValue(DbusHeaderField.Path, out var path) ? Convert.ToString(path) : null,
            Interface = headerFields.TryGetValue(DbusHeaderField.Interface, out var iface) ? Convert.ToString(iface) : null,
            Member = headerFields.TryGetValue(DbusHeaderField.Member, out var member) ? Convert.ToString(member) : null,
            Destination = headerFields.TryGetValue(DbusHeaderField.Destination, out var destination) ? Convert.ToString(destination) : null,
            Sender = headerFields.TryGetValue(DbusHeaderField.Sender, out var sender) ? Convert.ToString(sender) : null,
            ErrorName = headerFields.TryGetValue(DbusHeaderField.ErrorName, out var errorName) ? Convert.ToString(errorName) : null,
            Signature = signature,
            Body = body,
            UnixFileDescriptors = unixFileDescriptors
        };
        consumed = totalLength;

        return true;
    }

    private static void WriteHeaderField(DbusWriter writer, DbusHeaderField field, string signature, object? value)
    {
        if (value is null)
        {
            return;
        }

        writer.Align(8);
        writer.WriteByte((byte)field);
        writer.WriteValue(new DbusTypeNode.Primitive('v'), new DbusVariant(signature, value));
    }

    private static Dictionary<DbusHeaderField, object?> ReadHeaderFields(DbusReader reader)
    {
        var fields = new Dictionary<DbusHeaderField, object?>();
        reader.Align(4);
        var length = checked((int)reader.ReadUInt32());
        reader.Align(8);
        var end = reader.Position + length;
        while (reader.Position < end)
        {
            reader.Align(8);
            var field = (DbusHeaderField)reader.ReadByte();
            var value = reader.ReadValue(new DbusTypeNode.Primitive('v'), typeof(object));
            fields[field] = value is DbusVariant variant ? variant.Value : value;
        }

        if (reader.Position != end)
        {
            throw new DbusException("D-Bus header field reader overran the declared field length.");
        }

        return fields;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
    {
        return (uint)(buffer[offset] |
                      (buffer[offset + 1] << 8) |
                      (buffer[offset + 2] << 16) |
                      (buffer[offset + 3] << 24));
    }
}
