
using System.Collections.Immutable;

namespace Dbus.ContractGenerator;

internal static class DbusSignatureParser
{
    private const int MaxSignatureLength = 255;
    private const int MaxArrayNestingDepth = 32;
    private const int MaxStructNestingDepth = 32;

    internal static DbusType ParseType(string signature)
    {
        if (signature.Length > MaxSignatureLength)
        {
            throw new DbusSignatureParseException(
                signature,
                $"Signature length {signature.Length} exceeds the D-Bus maximum of {MaxSignatureLength} characters.");
        }

        var parser = new Parser(signature);
        var parsed = parser.ParseSingleType(arrayDepth: 0, structDepth: 0);
        if (!parser.IsAtEnd())
        {
            throw new DbusSignatureParseException(signature, $"Unexpected trailing characters at position {parser.Position}.");
        }

        return parsed;
    }

    private sealed class Parser
    {
        private readonly string signature;
        private int position;

        internal int Position => position;

        internal Parser(string signature)
        {
            this.signature = signature;
        }

        internal DbusType ParseSingleType(int arrayDepth, int structDepth)
        {
            if (IsAtEnd())
            {
                throw new DbusSignatureParseException(signature, "Signature ended unexpectedly.");
            }

            var current = signature[position];
            position++;

            switch (current)
            {
                case 'a':
                {
                    var nextArrayDepth = arrayDepth + 1;
                    if (nextArrayDepth > MaxArrayNestingDepth)
                    {
                        throw new DbusSignatureParseException(
                            signature,
                            $"Array nesting depth exceeds the D-Bus maximum of {MaxArrayNestingDepth}.");
                    }

                    if (!IsAtEnd() && signature[position] == '{')
                    {
                        position++;
                        var keyType = ParseSingleType(nextArrayDepth, structDepth);
                        if (!IsValidDictionaryKeyType(keyType))
                        {
                            throw new DbusSignatureParseException(
                                signature,
                                "Dictionary entry key type must be a basic non-variant D-Bus type.");
                        }

                        var valueType = ParseSingleType(nextArrayDepth, structDepth);
                        Expect('}');
                        return new DictDbusType(keyType, valueType);
                    }

                    return new ArrayDbusType(ParseSingleType(nextArrayDepth, structDepth));
                }

                case '(':
                {
                    var nextStructDepth = structDepth + 1;
                    if (nextStructDepth > MaxStructNestingDepth)
                    {
                        throw new DbusSignatureParseException(
                            signature,
                            $"Struct nesting depth exceeds the D-Bus maximum of {MaxStructNestingDepth}.");
                    }

                    var elements = ImmutableArray.CreateBuilder<DbusType>();
                    while (true)
                    {
                        if (IsAtEnd())
                        {
                            throw new DbusSignatureParseException(signature, "Unterminated struct signature.");
                        }

                        if (signature[position] == ')')
                        {
                            position++;
                            break;
                        }

                        elements.Add(ParseSingleType(arrayDepth, nextStructDepth));
                    }

                    return new StructDbusType(elements.ToImmutable());
                }

                case 'y':
                case 'b':
                case 'n':
                case 'q':
                case 'i':
                case 'u':
                case 'x':
                case 't':
                case 'd':
                case 's':
                case 'o':
                case 'g':
                case 'h':
                case 'v':
                    return new PrimitiveDbusType(current);

                default:
                    throw new DbusSignatureParseException(signature, $"Unsupported signature token '{current}' at position {position - 1}.");
            }
        }

        private void Expect(char token)
        {
            if (IsAtEnd() || signature[position] != token)
            {
                throw new DbusSignatureParseException(signature, $"Expected '{token}' at position {position}.");
            }

            position++;
        }

        internal bool IsAtEnd()
        {
            return position >= signature.Length;
        }

        private static bool IsValidDictionaryKeyType(DbusType type)
        {
            return type is PrimitiveDbusType primitiveType &&
                   primitiveType.Code is 'y' or 'b' or 'n' or 'q' or 'i' or 'u' or 'x' or 't' or 'd' or 's' or 'o' or 'g' or 'h';
        }
    }
}
