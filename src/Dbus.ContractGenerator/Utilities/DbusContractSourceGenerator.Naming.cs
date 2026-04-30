
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Dbus.ContractGenerator;

internal static class DbusGeneratorNaming
{
    internal static string ToPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var upperNext = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                upperNext = true;
                continue;
            }

            builder.Append(upperNext ? char.ToUpperInvariant(character) : character);
            upperNext = false;
        }

        return builder.ToString();
    }

    internal static string ToCamelCaseIdentifier(string value, string fallback)
    {
        var pascal = ToPascalCase(value);
        if (string.IsNullOrWhiteSpace(pascal))
        {
            return fallback;
        }

        if (pascal.Length == 1)
        {
            return char.ToLowerInvariant(pascal[0]).ToString();
        }

        var builder = new StringBuilder(pascal.Length);
        builder.Append(char.ToLowerInvariant(pascal[0]));
        builder.Append(pascal, 1, pascal.Length - 1);
        return ToSafeIdentifier(builder.ToString(), fallback);
    }

    internal static string EnsureAsyncSuffix(string methodName)
    {
        if (methodName.EndsWith("Async", StringComparison.Ordinal))
        {
            return methodName;
        }

        return methodName + "Async";
    }

    internal static string ToSafeIdentifier(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(character);
            }
        }

        if (builder.Length == 0)
        {
            return fallback;
        }

        if (!char.IsLetter(builder[0]) && builder[0] != '_')
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    internal static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None
            ? $"@{identifier}"
            : identifier;
    }

    internal static string GetHintName(string interfaceName)
    {
        var sanitized = new string(interfaceName.Select(static character =>
            char.IsLetterOrDigit(character) ? character : '_').ToArray());
        return $"{sanitized}.DBus.g.cs";
    }

    internal static string MakeUniqueIdentifier(string preferredName, ISet<string> usedNames, string fallback)
    {
        var candidate = ToSafeIdentifier(preferredName, fallback);
        if (!usedNames.Contains(candidate))
        {
            usedNames.Add(candidate);
            return candidate;
        }

        var suffix = 2;
        while (true)
        {
            var suffixed = ToSafeIdentifier(candidate + suffix, fallback);
            if (!usedNames.Contains(suffixed))
            {
                usedNames.Add(suffixed);
                return suffixed;
            }

            suffix++;
        }
    }

    internal static string BuildIdentifierSuffix(string value, string fallback)
    {
        var pascal = ToPascalCase(value);
        if (string.IsNullOrWhiteSpace(pascal))
        {
            pascal = fallback;
        }

        return ToSafeIdentifier(pascal, fallback);
    }

    internal static bool IsDbusXml(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedPath = path.Replace('\\', '/');
        return normalizedPath.Contains("/Dbus/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsGeneratorConfigurationFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path.Replace('\\', '/');
        return normalizedPath.EndsWith($"/Dbus/{DbusGeneratorConstants.DefaultConfigurationFileName}", StringComparison.OrdinalIgnoreCase);
    }
}
