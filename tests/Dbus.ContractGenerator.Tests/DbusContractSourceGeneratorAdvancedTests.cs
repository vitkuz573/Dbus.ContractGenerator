
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dbus.ContractGenerator.Tests;

public sealed partial class DbusContractSourceGeneratorTests
{
    [Fact]
    public void Generate_WithCorpusCases_MatchesAbiBaselineManifest()
    {
        var manifestPath = Path.Combine(GetCorpusRoot(), "abi-baseline.manifest.json");
        var currentManifest = BuildAbiManifestJson();

        if (ShouldUpdateAbiManifest())
        {
            File.WriteAllText(manifestPath, currentManifest);
            return;
        }

        Assert.True(
            File.Exists(manifestPath),
            $"ABI manifest file '{manifestPath}' is missing. Run tests with DBUS_GENERATOR_UPDATE_ABI_MANIFEST=1 to generate it.");
        var expectedManifest = NormalizeSnapshot(File.ReadAllText(manifestPath));
        Assert.Equal(expectedManifest, NormalizeSnapshot(currentManifest));
    }

    [Fact]
    public void Differential_WithQDbusXml2Cpp_ParsesCorpusXml_WhenToolAvailable()
    {
        var toolPath = ResolveCommandPath("qdbusxml2cpp");
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            return;
        }

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "dbus-contract-generator-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            foreach (var caseDirectory in Directory.GetDirectories(GetCorpusRoot()).OrderBy(static path => path, StringComparer.Ordinal))
            {
                var xmlDirectory = Path.Combine(caseDirectory, "xml");
                foreach (var xmlPath in Directory.GetFiles(xmlDirectory, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(static path => path, StringComparer.Ordinal))
                {
                    var outputBase = Path.Combine(
                        workspaceRoot,
                        Path.GetFileNameWithoutExtension(caseDirectory) + "." + Path.GetFileNameWithoutExtension(xmlPath));
                    var arguments = $"-p \"{outputBase}\" \"{xmlPath}\"";
                    var result = ExecuteProcess(toolPath!, arguments, workspaceRoot, timeoutMilliseconds: 20000);

                    Assert.True(
                        result.ExitCode == 0,
                        $"qdbusxml2cpp failed for '{xmlPath}' with code {result.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StdOut}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StdErr}");
                    Assert.True(
                        File.Exists(outputBase + ".h"),
                        $"qdbusxml2cpp did not produce header file for '{xmlPath}'.");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
            catch
            {
                // Intentionally ignored: temporary workspace cleanup must not affect test outcome.
            }
        }
    }

    [Fact]
    public void Generate_WithCorpusCases_StaysWithinPerformanceBudget()
    {
        var iterationCount = ReadIntFromEnvironment("DBUS_GENERATOR_PERF_ITERATIONS", fallback: 12, minimum: 1, maximum: 200);
        var maxDurationMs = ReadLongFromEnvironment("DBUS_GENERATOR_PERF_MAX_MS", fallback: 12000, minimum: 1, maximum: long.MaxValue);
        var maxAllocatedBytes = ReadLongFromEnvironment("DBUS_GENERATOR_PERF_MAX_ALLOC_BYTES", fallback: 1024L * 1024L * 1024L, minimum: 1, maximum: long.MaxValue);

        var corpusCases = Directory.GetDirectories(GetCorpusRoot()).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(corpusCases);

        var inputs = corpusCases
            .Select(caseDirectory =>
            {
                var xmlFiles = LoadCorpusXmlFiles(caseDirectory);
                var configuration = File.ReadAllText(Path.Combine(caseDirectory, "config.json"));
                return (CaseDirectory: caseDirectory, XmlFiles: xmlFiles, Configuration: configuration);
            })
            .ToArray();

        foreach (var input in inputs)
        {
            var warmupResult = RunGenerator(input.XmlFiles, input.Configuration);
            AssertNoErrors(warmupResult.Diagnostics);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBytesBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        for (var iteration = 0; iteration < iterationCount; iteration++)
        {
            foreach (var input in inputs)
            {
                var result = RunGenerator(input.XmlFiles, input.Configuration);
                AssertNoErrors(result.Diagnostics);
            }
        }

        stopwatch.Stop();
        var allocatedBytesAfter = GC.GetTotalAllocatedBytes(precise: true);
        var allocatedBytes = Math.Max(0, allocatedBytesAfter - allocatedBytesBefore);
        var durationMs = stopwatch.ElapsedMilliseconds;

        TryWritePerformanceReport(
            iterationCount,
            inputs.Length,
            durationMs,
            maxDurationMs,
            allocatedBytes,
            maxAllocatedBytes);

        Assert.True(
            durationMs <= maxDurationMs,
            $"Performance budget exceeded: duration {durationMs} ms > limit {maxDurationMs} ms.");
        Assert.True(
            allocatedBytes <= maxAllocatedBytes,
            $"Allocation budget exceeded: allocated {allocatedBytes} bytes > limit {maxAllocatedBytes} bytes.");
    }

    private static string BuildAbiManifestJson()
    {
        var manifestEntries = ImmutableArray.CreateBuilder<string>();
        foreach (var caseDirectory in Directory.GetDirectories(GetCorpusRoot()).OrderBy(static path => path, StringComparer.Ordinal))
        {
            var caseName = Path.GetFileName(caseDirectory);
            var xmlFiles = LoadCorpusXmlFiles(caseDirectory);
            var configuration = File.ReadAllText(Path.Combine(caseDirectory, "config.json"));
            var result = RunGenerator(xmlFiles, configuration);
            AssertNoErrors(result.Diagnostics);

            var normalizedSource = NormalizeSnapshot(result.GeneratedSourceText);
            var contractMembers = DbusContractCompatibilityAnalyzer
                .ExtractContractMembers(normalizedSource)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToImmutableArray();

            var sourceHash = ComputeSha256(normalizedSource);
            var membersPayload = string.Join("\n", contractMembers);
            var membersHash = ComputeSha256(membersPayload);

            manifestEntries.Add(
                $"  {{\"case\": \"{EscapeJsonString(caseName)}\", \"sourceSha256\": \"{sourceHash}\", \"membersSha256\": \"{membersHash}\", \"memberCount\": {contractMembers.Length}}}");
        }

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"schemaVersion\": 1,");
        builder.AppendLine("  \"entries\": [");
        builder.AppendLine(string.Join(",\n", manifestEntries));
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string ComputeSha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private static bool ShouldUpdateAbiManifest()
    {
        var value = Environment.GetEnvironmentVariable("DBUS_GENERATOR_UPDATE_ABI_MANIFEST");
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeJsonString(string value)
    {
        return JsonEncodedText.Encode(value).ToString();
    }

    private static string? ResolveCommandPath(string commandName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in paths)
        {
            try
            {
                var candidate = Path.Combine(path, commandName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static ProcessExecutionResult ExecuteProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMilliseconds)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.Start();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            throw new TimeoutException($"Process '{fileName} {arguments}' exceeded timeout {timeoutMilliseconds} ms.");
        }

        return new ProcessExecutionResult(process.ExitCode, stdOut, stdErr);
    }

    private static int ReadIntFromEnvironment(string key, int fallback, int minimum, int maximum)
    {
        var configured = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(configured, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, minimum, maximum);
    }

    private static long ReadLongFromEnvironment(string key, long fallback, long minimum, long maximum)
    {
        var configured = Environment.GetEnvironmentVariable(key);
        if (!long.TryParse(configured, out var parsed))
        {
            return fallback;
        }

        if (parsed < minimum)
        {
            return minimum;
        }

        if (parsed > maximum)
        {
            return maximum;
        }

        return parsed;
    }

    private static void TryWritePerformanceReport(
        int iterations,
        int corpusCaseCount,
        long durationMs,
        long maxDurationMs,
        long allocatedBytes,
        long maxAllocatedBytes)
    {
        var reportPath = Environment.GetEnvironmentVariable("DBUS_GENERATOR_PERF_REPORT_PATH");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        var reportDirectory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
        }

        var reportJson =
            "{\n" +
            $"  \"iterations\": {iterations},\n" +
            $"  \"corpusCaseCount\": {corpusCaseCount},\n" +
            $"  \"durationMs\": {durationMs},\n" +
            $"  \"maxDurationMs\": {maxDurationMs},\n" +
            $"  \"allocatedBytes\": {allocatedBytes},\n" +
            $"  \"maxAllocatedBytes\": {maxAllocatedBytes}\n" +
            "}\n";
        File.WriteAllText(reportPath!, reportJson);
    }

    private readonly record struct ProcessExecutionResult(int ExitCode, string StdOut, string StdErr);
}
