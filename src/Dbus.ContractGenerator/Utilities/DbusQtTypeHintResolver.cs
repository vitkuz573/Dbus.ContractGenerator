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

        var normalizedHint = NormalizeHintName(qtTypeHint!);
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
        if (IsSequenceTypeHint(typeName))
        {
            if (arguments.Length == 1 && TryResolveClrType(arguments[0], qtTypeHintMappings, out var elementType))
            {
                return $"{elementType}[]";
            }

            return string.Empty;
        }

        if (IsSetTypeHint(typeName))
        {
            if (arguments.Length == 1 && TryResolveClrType(arguments[0], qtTypeHintMappings, out var elementType))
            {
                return $"System.Collections.Generic.ISet<{elementType}>";
            }

            return string.Empty;
        }

        if (IsDictionaryTypeHint(typeName))
        {
            if (arguments.Length == 2 &&
                TryResolveClrType(arguments[0], qtTypeHintMappings, out var keyType) &&
                TryResolveClrType(arguments[1], qtTypeHintMappings, out var valueType))
            {
                return $"System.Collections.Generic.IDictionary<{keyType}, {valueType}>";
            }

            return string.Empty;
        }

        if (IsPairTypeHint(typeName))
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

    internal static string NormalizeHintName(string qtTypeHint)
    {
        var decoratedHint = StripCppDecorations(qtTypeHint);
        if (TryParseGenericHint(decoratedHint, out var typeName, out var arguments))
        {
            var normalizedTypeName = NormalizeSimpleTypeName(typeName);
            var builder = new StringBuilder(decoratedHint.Length);
            builder.Append(normalizedTypeName);
            builder.Append('<');

            for (var index = 0; index < arguments.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(NormalizeHintName(arguments[index]));
            }

            builder.Append('>');
            return builder.ToString();
        }

        return NormalizeSimpleTypeName(decoratedHint);
    }

    private static string NormalizeSimpleTypeName(string qtTypeHint)
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

    private static string StripCppDecorations(string value)
    {
        var current = value.Trim();
        while (!string.IsNullOrWhiteSpace(current))
        {
            var previous = current;
            current = RemoveLeadingWord(current, "const");
            current = RemoveLeadingWord(current, "volatile");
            current = RemoveLeadingWord(current, "class");
            current = RemoveLeadingWord(current, "struct");
            current = RemoveTrailingWord(current, "const");
            current = RemoveTrailingWord(current, "volatile");

            while (current.EndsWith("&", StringComparison.Ordinal) ||
                   current.EndsWith("*", StringComparison.Ordinal))
            {
                current = current.Substring(0, current.Length - 1).Trim();
                current = RemoveTrailingWord(current, "const");
                current = RemoveTrailingWord(current, "volatile");
            }

            if (string.Equals(previous, current, StringComparison.Ordinal))
            {
                return current;
            }
        }

        return current;
    }

    private static string RemoveLeadingWord(string value, string word)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(word, StringComparison.Ordinal) ||
            trimmed.Length == word.Length ||
            IsIdentifierPart(trimmed[word.Length]))
        {
            return trimmed;
        }

        return trimmed.Substring(word.Length).Trim();
    }

    private static string RemoveTrailingWord(string value, string word)
    {
        var trimmed = value.Trim();
        if (!trimmed.EndsWith(word, StringComparison.Ordinal) ||
            trimmed.Length == word.Length)
        {
            return trimmed;
        }

        var wordStart = trimmed.Length - word.Length;
        if (wordStart > 0 && IsIdentifierPart(trimmed[wordStart - 1]))
        {
            return trimmed;
        }

        return trimmed.Substring(0, wordStart).Trim();
    }

    private static bool IsIdentifierPart(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_';
    }

    private static bool IsSequenceTypeHint(string typeName)
    {
        return string.Equals(typeName, "QList", StringComparison.Ordinal) ||
               string.Equals(typeName, "QVector", StringComparison.Ordinal) ||
               string.Equals(typeName, "QLinkedList", StringComparison.Ordinal) ||
               string.Equals(typeName, "QQueue", StringComparison.Ordinal) ||
               string.Equals(typeName, "QStack", StringComparison.Ordinal) ||
               string.Equals(typeName, "QVarLengthArray", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::vector", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::list", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::deque", StringComparison.Ordinal);
    }

    private static bool IsSetTypeHint(string typeName)
    {
        return string.Equals(typeName, "QSet", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::set", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::unordered_set", StringComparison.Ordinal);
    }

    private static bool IsDictionaryTypeHint(string typeName)
    {
        return string.Equals(typeName, "QMap", StringComparison.Ordinal) ||
               string.Equals(typeName, "QHash", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::map", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::unordered_map", StringComparison.Ordinal);
    }

    private static bool IsPairTypeHint(string typeName)
    {
        return string.Equals(typeName, "QPair", StringComparison.Ordinal) ||
               string.Equals(typeName, "std::pair", StringComparison.Ordinal);
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
