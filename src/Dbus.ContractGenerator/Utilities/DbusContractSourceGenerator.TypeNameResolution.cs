
using System.Collections.Immutable;
using System.Text;

namespace Dbus.ContractGenerator;

public sealed partial class DbusContractSourceGenerator
{
    private static ImmutableArray<DbusInterfaceModel> ResolveUniqueTypeNames(
        IEnumerable<DbusInterfaceModel> interfaceModels,
        TypeNamingStrategy namingStrategy)
    {
        var orderedInterfaces = interfaceModels
            .OrderBy(static item => item.InterfaceName, StringComparer.Ordinal)
            .ToImmutableArray();
        if (orderedInterfaces.IsDefaultOrEmpty)
        {
            return [];
        }

        var usedTypeNames = new HashSet<string>(StringComparer.Ordinal);
        var resolvedInterfaces = ImmutableArray.CreateBuilder<DbusInterfaceModel>(orderedInterfaces.Length);

        foreach (var interfaceModel in orderedInterfaces)
        {
            var preferredTypeName = DecorateTypeNameCandidate(interfaceModel.InterfaceTypeName, namingStrategy);
            var resolvedTypeName = ResolveUniqueInterfaceTypeName(
                interfaceModel.InterfaceName,
                preferredTypeName,
                namingStrategy,
                usedTypeNames);
            usedTypeNames.Add(resolvedTypeName);

            resolvedInterfaces.Add(
                string.Equals(resolvedTypeName, interfaceModel.InterfaceTypeName, StringComparison.Ordinal)
                    ? interfaceModel
                    : interfaceModel.WithInterfaceTypeName(resolvedTypeName));
        }

        return resolvedInterfaces.ToImmutable();
    }

    private static string ResolveUniqueInterfaceTypeName(
        string interfaceName,
        string preferredTypeName,
        TypeNamingStrategy namingStrategy,
        ISet<string> usedTypeNames)
    {
        var candidates = BuildInterfaceTypeNameCandidates(interfaceName, preferredTypeName, namingStrategy);
        foreach (var candidate in candidates)
        {
            if (!usedTypeNames.Contains(candidate))
            {
                return candidate;
            }
        }

        var baseCandidate = candidates[candidates.Length - 1];
        return namingStrategy.CollisionPolicy switch
        {
            TypeNameCollisionPolicy.Suffix => ResolveWithNumericSuffix(baseCandidate, usedTypeNames),
            TypeNameCollisionPolicy.Prefix => ResolveWithStablePrefix(baseCandidate, interfaceName, namingStrategy.HashLength, usedTypeNames),
            _ => ResolveWithStableHashSuffix(baseCandidate, interfaceName, namingStrategy.HashLength, usedTypeNames)
        };
    }

    private static string ResolveWithNumericSuffix(string baseCandidate, ISet<string> usedTypeNames)
    {
        var index = 2;
        while (true)
        {
            var candidate = $"{baseCandidate}_{index}";
            if (!usedTypeNames.Contains(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static string ResolveWithStablePrefix(
        string baseCandidate,
        string interfaceName,
        int hashLength,
        ISet<string> usedTypeNames)
    {
        var hashPrefix = "X" + ComputeStableHexHash(interfaceName, hashLength).TrimStart('_');
        var candidate = $"{hashPrefix}_{baseCandidate}";
        if (!usedTypeNames.Contains(candidate))
        {
            return candidate;
        }

        var index = 2;
        while (true)
        {
            var indexedCandidate = $"{hashPrefix}_{index}_{baseCandidate}";
            if (!usedTypeNames.Contains(indexedCandidate))
            {
                return indexedCandidate;
            }

            index++;
        }
    }

    private static string ResolveWithStableHashSuffix(
        string baseCandidate,
        string interfaceName,
        int hashLength,
        ISet<string> usedTypeNames)
    {
        var hashSuffix = ComputeStableHexHash(interfaceName, hashLength);
        var resolved = baseCandidate + hashSuffix;
        var index = 1;
        while (usedTypeNames.Contains(resolved))
        {
            resolved = $"{baseCandidate}{hashSuffix}_{index}";
            index++;
        }

        return resolved;
    }

    private static ImmutableArray<string> BuildInterfaceTypeNameCandidates(
        string interfaceName,
        string preferredTypeName,
        TypeNamingStrategy namingStrategy)
    {
        var candidates = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static void AddCandidate(ImmutableArray<string>.Builder list, ISet<string> set, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var normalized = candidate.Trim();
            if (!IsValidIdentifier(normalized))
            {
                normalized = ToSafeIdentifier(normalized, "IDbusInterface");
            }

            if (set.Add(normalized))
            {
                list.Add(normalized);
            }
        }

        AddCandidate(candidates, seen, preferredTypeName);

        var segments = interfaceName
            .Split(['.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static item => ToPascalCase(item))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToImmutableArray();
        if (segments.IsDefaultOrEmpty)
        {
            AddCandidate(candidates, seen, DecorateTypeNameCandidate("IDbusInterface", namingStrategy));
            return candidates.ToImmutable();
        }

        for (var take = 1; take <= segments.Length; take++)
        {
            var slice = segments.Skip(segments.Length - take);
            AddCandidate(candidates, seen, DecorateTypeNameCandidate("I" + string.Concat(slice), namingStrategy));
        }

        AddCandidate(candidates, seen, DecorateTypeNameCandidate("I" + string.Concat(segments), namingStrategy));
        return candidates.ToImmutable();
    }

    private static string DecorateTypeNameCandidate(string baseTypeName, TypeNamingStrategy namingStrategy)
    {
        var prefix = NormalizeTypeNameFragment(namingStrategy.InterfacePrefix);
        var suffix = NormalizeTypeNameFragment(namingStrategy.InterfaceSuffix);
        var decorated = prefix + baseTypeName + suffix;
        return ToSafeIdentifier(decorated, baseTypeName);
    }

    private static string NormalizeTypeNameFragment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value!.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string ComputeStableHexHash(string value, int hashLength)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }

        var rawHash = hash.ToString("X8");
        if (hashLength <= 0)
        {
            hashLength = 8;
        }

        var normalizedHashLength = Math.Min(Math.Max(hashLength, 4), 32);
        if (normalizedHashLength <= rawHash.Length)
        {
            return "_" + rawHash.Substring(0, normalizedHashLength);
        }

        var extensionBuilder = new StringBuilder(normalizedHashLength);
        while (extensionBuilder.Length < normalizedHashLength)
        {
            extensionBuilder.Append(rawHash);
        }

        return "_" + extensionBuilder.ToString(0, normalizedHashLength);
    }
}
