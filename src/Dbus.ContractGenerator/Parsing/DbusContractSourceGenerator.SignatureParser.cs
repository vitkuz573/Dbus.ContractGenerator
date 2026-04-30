
using System.Collections.Immutable;

namespace Dbus.ContractGenerator;

public sealed partial class DbusContractSourceGenerator
{
    private sealed class DbusSignatureParser
    {
        private readonly string signature;
        private int position;

        private DbusSignatureParser(string signature)
        {
            this.signature = signature;
        }

        public static DbusType ParseType(string signature)
        {
            var parser = new DbusSignatureParser(signature);
            var parsed = parser.ParseSingleType();
            if (!parser.IsAtEnd())
            {
                throw new DbusSignatureParseException(signature, $"Unexpected trailing characters at position {parser.position}.");
            }

            return parsed;
        }

        private DbusType ParseSingleType()
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
                    if (!IsAtEnd() && signature[position] == '{')
                    {
                        position++;
                        var keyType = ParseSingleType();
                        var valueType = ParseSingleType();
                        Expect('}');
                        return new DictDbusType(keyType, valueType);
                    }

                    return new ArrayDbusType(ParseSingleType());

                case '(':
                {
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

                        elements.Add(ParseSingleType());
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

        private bool IsAtEnd()
        {
            return position >= signature.Length;
        }
    }
}
