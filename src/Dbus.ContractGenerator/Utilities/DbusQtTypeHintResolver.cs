using System.Collections.Immutable;
using System.Text;

namespace Dbus.ContractGenerator;

internal static class DbusQtTypeHintResolver
{
    internal static bool CanResolveHint(
        string qtTypeHint,
        ImmutableDictionary<string, string> qtTypeHintMappings)
    {
        return TryResolveClrType(qtTypeHint, qtTypeHintMappings, out _);
    }

    internal static bool TryResolveClrType(
        string? qtTypeHint,
        ImmutableDictionary<string, string> qtTypeHintMappings,
        out string clrType)
    {
        if (string.IsNullOrWhiteSpace(qtTypeHint))
        {
            clrType = string.Empty;
            return false;
        }

        var normalizedHint = NormalizeHint(qtTypeHint!);
        if (qtTypeHintMappings.TryGetValue(normalizedHint, out clrType!) && !string.IsNullOrWhiteSpace(clrType))
        {
            return true;
        }

        if (!TryParseGenericHint(normalizedHint, out var typeName, out var arguments))
        {
            clrType = string.Empty;
            return false;
        }

        clrType = ResolveGenericHint(typeName, arguments, qtTypeHintMappings);
        return !string.IsNullOrWhiteSpace(clrType);
    }

    private static string ResolveGenericHint(
        string typeName,
        ImmutableArray<string> arguments,
        ImmutableDictionary<string, string> qtTypeHintMappings)
    {
        if (string.Equals(typeName, "QList", StringComparison.Ordinal) ||
            string.Equals(typeName, "QVector", StringComparison.Ordinal) ||
            string.Equals(typeName, "QLinkedList", StringComparison.Ordinal))
        {
            if (arguments.Length == 1 && TryResolveClrType(arguments[0], qtTypeHintMappings, out var elementType))
            {
                return $"{elementType}[]";
            }

            return string.Empty;
        }

        if (string.Equals(typeName, "QSet", StringComparison.Ordinal))
        {
            if (arguments.Length == 1 && TryResolveClrType(arguments[0], qtTypeHintMappings, out var elementType))
            {
                return $"System.Collections.Generic.ISet<{elementType}>";
            }

            return string.Empty;
        }

        if (string.Equals(typeName, "QMap", StringComparison.Ordinal) ||
            string.Equals(typeName, "QHash", StringComparison.Ordinal))
        {
            if (arguments.Length == 2 &&
                TryResolveClrType(arguments[0], qtTypeHintMappings, out var keyType) &&
                TryResolveClrType(arguments[1], qtTypeHintMappings, out var valueType))
            {
                return $"System.Collections.Generic.IDictionary<{keyType}, {valueType}>";
            }

            return string.Empty;
        }

        if (string.Equals(typeName, "QPair", StringComparison.Ordinal))
        {
            if (arguments.Length == 2 &&
                TryResolveClrType(arguments[0], qtTypeHintMappings, out var leftType) &&
                TryResolveClrType(arguments[1], qtTypeHintMappings, out var rightType))
            {
                return $"({leftType}, {rightType})";
            }

            return string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeHint(string qtTypeHint)
    {
        var builder = new StringBuilder(qtTypeHint.Length);
        foreach (var character in qtTypeHint)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    private static bool TryParseGenericHint(
        string hint,
        out string typeName,
        out ImmutableArray<string> arguments)
    {
        typeName = string.Empty;
        arguments = [];
        if (string.IsNullOrWhiteSpace(hint))
        {
            return false;
        }

        var openIndex = hint.IndexOf('<');
        if (openIndex <= 0 || !hint.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var candidateTypeName = hint.Substring(0, openIndex);
        if (string.IsNullOrWhiteSpace(candidateTypeName))
        {
            return false;
        }

        var inner = hint.Substring(openIndex + 1, hint.Length - openIndex - 2);
        var parts = SplitGenericArguments(inner);
        if (parts.IsDefaultOrEmpty)
        {
            return false;
        }

        typeName = candidateTypeName;
        arguments = parts;
        return true;
    }

    private static ImmutableArray<string> SplitGenericArguments(string inner)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        var tokenBuilder = new StringBuilder(inner.Length);
        var depth = 0;

        foreach (var character in inner)
        {
            if (character == '<')
            {
                depth++;
                tokenBuilder.Append(character);
                continue;
            }

            if (character == '>')
            {
                depth--;
                if (depth < 0)
                {
                    return [];
                }

                tokenBuilder.Append(character);
                continue;
            }

            if (character == ',' && depth == 0)
            {
                var token = tokenBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return [];
                }

                builder.Add(token);
                tokenBuilder.Clear();
                continue;
            }

            tokenBuilder.Append(character);
        }

        if (depth != 0)
        {
            return [];
        }

        var lastToken = tokenBuilder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(lastToken))
        {
            return [];
        }

        builder.Add(lastToken);
        return builder.ToImmutable();
    }
}
