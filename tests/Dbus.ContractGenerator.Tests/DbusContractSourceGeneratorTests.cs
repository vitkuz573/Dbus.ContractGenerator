
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Dbus.ContractGenerator;

namespace Dbus.ContractGenerator.Tests;

public sealed partial class DbusContractSourceGeneratorTests
{
    [Fact]
    public void Generate_WithKeywordSignalNames_ProducesCompilableOutput()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.kde.libkonq.FileUndoManager.xml"] =
                """
                <node>
                  <interface name="org.kde.libkonq.FileUndoManager">
                    <method name="get">
                      <arg direction="out" type="ay" name="commands"/>
                    </method>
                    <signal name="lock"/>
                    <signal name="pop"/>
                    <signal name="push">
                      <arg direction="out" type="ay" name="command"/>
                    </signal>
                    <signal name="unlock"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        AssertNoErrors(result.Diagnostics);
        Assert.Contains("WatchLockAsync", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("WatchPopAsync", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("WatchPushAsync", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("WatchUnlockAsync", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithTypeNameCollisions_ProducesUniqueInterfaceTypes()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.freedesktop.Akonadi.Server.xml"] =
                """
                <node>
                  <interface name="org.freedesktop.Akonadi.Server">
                    <method name="Ping"/>
                  </interface>
                </node>
                """,
            ["org.freedesktop.Avahi.Server.xml"] =
                """
                <node>
                  <interface name="org.freedesktop.Avahi.Server">
                    <method name="Ping"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);
        AssertNoErrors(result.Diagnostics);

        var matches = Regex.Matches(
            result.GeneratedSourceText,
            "public interface (?<name>[A-Za-z_][A-Za-z0-9_]*) : IDbusObject");
        var interfaceNames = matches
            .Select(static match => match.Groups["name"].Value)
            .ToArray();

        Assert.Equal(2, interfaceNames.Length);
        Assert.Equal(interfaceNames.Length, interfaceNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Generate_WithMergePolicyFail_ReportsConflictError()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Dynamic.v1.xml"] =
                """
                <node>
                  <interface name="org.example.Dynamic">
                    <method name="Ping"/>
                  </interface>
                </node>
                """,
            ["org.example.Dynamic.v2.xml"] =
                """
                <node>
                  <interface name="org.example.Dynamic">
                    <method name="Pong"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "mergePolicy": "fail"
            }
            """);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Id == "DBCG003" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generate_WithMergePolicyWarn_ReportsWarningAndMerges()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Dynamic.v1.xml"] =
                """
                <node>
                  <interface name="org.example.Dynamic">
                    <method name="Ping"/>
                  </interface>
                </node>
                """,
            ["org.example.Dynamic.v2.xml"] =
                """
                <node>
                  <interface name="org.example.Dynamic">
                    <method name="Pong"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "mergePolicy": "warn"
            }
            """);

        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Id == "DBCG009" && diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("PingAsync", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("PongAsync", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithStrictAbiCompatibility_ReportsConflictAndSkipsMerge()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Dynamic.v1.xml"] =
                """
                <node>
                  <interface name="org.example.Dynamic">
                    <method name="Ping"/>
                  </interface>
                </node>
                """,
            ["org.example.Dynamic.v2.xml"] =
                """
                <node>
                  <interface name="org.example.Dynamic">
                    <method name="Pong"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "strictAbiCompatibility": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "mergePolicy": "merge-union"
            }
            """);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Id == "DBCG003" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("PingAsync", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("PongAsync", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithRecursiveNodes_CollectsObjectPaths()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Deep.xml"] =
                """
                <node name="/org/example">
                  <interface name="org.example.Deep">
                    <method name="Ping"/>
                  </interface>
                  <node name="child">
                    <interface name="org.example.Deep">
                      <method name="Ping"/>
                    </interface>
                  </node>
                  <node name="/custom/path">
                    <interface name="org.example.Deep">
                      <method name="Ping"/>
                    </interface>
                  </node>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        AssertNoErrors(result.Diagnostics);
        Assert.Contains("DefaultObjectPath", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("\"/org/example\"", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("\"/org/example/child\"", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("\"/custom/path\"", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithStringLiteralCharactersInMetadata_EscapesGeneratedLiterals()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Quoted.xml"] =
                """
                <node name="/org/example/&quot;root\path">
                  <interface name="org.example.Quoted&quot;Interface">
                    <property name="Display&quot;\Name" type="s" access="readwrite"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        AssertNoErrors(result.Diagnostics);
        Assert.Contains("[DbusInterface(\"org.example.Quoted\\\"Interface\")]", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("DefaultObjectPath { get; } = \"/org/example/\\\"root\\\\path\";", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("GetAsync<string>(\"Display\\\"\\\\Name\")", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("SetAsync(\"Display\\\"\\\\Name\", val)", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithAnnotations_AppliesNoReplyQtHintsAndStabilityAttributes()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Annotated.xml"] =
                """
                <node>
                  <interface name="org.example.Annotated">
                    <method name="Execute">
                      <annotation name="org.freedesktop.DBus.Method.NoReply" value="true" />
                      <annotation name="org.freedesktop.DBus.Deprecated" value="true" />
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="quint64" />
                      <arg direction="in" name="target" type="u" />
                      <arg direction="out" name="result" type="i" />
                    </method>
                    <property name="Items" type="as" access="read">
                      <annotation name="org.qtproject.QtDBus.QtTypeName" value="QStringList" />
                      <annotation name="org.freedesktop.DBus.Experimental" value="true" />
                    </property>
                    <signal name="Updated">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.Out0" value="QByteArray" />
                      <arg name="payload" type="ay" />
                    </signal>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Id == "DBCG010");
        Assert.Contains("Task ExecuteAsync(ulong target);", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("[Obsolete(\"D-Bus method is marked as deprecated.\")]", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("public string[] Items { get; set; } = default!;", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("[Obsolete(\"D-Bus property is marked as experimental.\")]", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("Action<byte[]> handler", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithCustomQtTypeHintMappings_UsesConfiguredClrTypes()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.CustomQtTypes.xml"] =
                """
                <node>
                  <interface name="org.example.CustomQtTypes">
                    <method name="Execute">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="TenantToken" />
                      <arg direction="in" name="token" type="s" />
                    </method>
                    <property name="Metadata" type="a{sv}" access="read">
                      <annotation name="org.qtproject.QtDBus.QtTypeName" value="QVariantMap" />
                    </property>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "qtTypeHintMappings": {
                "TenantToken": "Guid",
                "QVariantMap": "IReadOnlyDictionary<string, object>"
              }
            }
            """);

        AssertNoErrors(result.Diagnostics.Where(static item => item.Id is not "DBCG010" and not "DBCG011"));
        Assert.Contains("Task ExecuteAsync(Guid token);", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyDictionary<string, object> Metadata { get; set; } = default!;", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithGenericQtTypeHints_ResolvesWithoutExplicitMappings()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.GenericQtHints.xml"] =
                """
                <node>
                  <interface name="org.example.GenericQtHints">
                    <method name="Execute">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="QList&lt;QString&gt;" />
                      <annotation name="org.qtproject.QtDBus.QtTypeName.Out0" value="QPair&lt;quint32,QString&gt;" />
                      <arg direction="in" name="items" type="as" />
                      <arg direction="out" name="result" type="(us)" />
                    </method>
                    <signal name="Updated">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.Out0" value="QMap&lt;QString,QByteArray&gt;" />
                      <arg name="payload" type="a{sv}" />
                    </signal>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "qtTypeHintPolicy": {
                "unknownHintBehavior": "error"
              }
            }
            """);

        AssertNoErrors(result.Diagnostics.Where(static item => item.Id is not "DBCG010" and not "DBCG011"));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id is "DBCG012" or "DBCG013");
        Assert.Contains("Task<(uint, string)> ExecuteAsync(string[] items);", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("Action<System.Collections.Generic.IDictionary<string, byte[]>> handler", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithCppDecoratedQtTypeHints_ResolvesAliasesAndNestedContainers()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.CppQtHints.xml"] =
                """
                <node>
                  <interface name="org.example.CppQtHints">
                    <method name="Execute">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="const QList&lt;const QString &amp;&gt; &amp;" />
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In1" value="unsigned int" />
                      <annotation name="org.qtproject.QtDBus.QtTypeName.Out0" value="std::map&lt;QString, const QByteArray &amp;&gt;" />
                      <arg direction="in" name="items" type="as" />
                      <arg direction="in" name="flags" type="u" />
                      <arg direction="out" name="payload" type="a{say}" />
                    </method>
                    <signal name="HandlePassed">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.Out0" value="QDBusUnixFileDescriptor" />
                      <arg name="handle" type="h" />
                    </signal>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "qtTypeHintPolicy": {
                "unknownHintBehavior": "error"
              }
            }
            """);

        AssertNoErrors(result.Diagnostics.Where(static item => item.Id is not "DBCG010" and not "DBCG011"));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id is "DBCG012" or "DBCG013");
        Assert.Contains(
            "Task<System.Collections.Generic.IDictionary<string, byte[]>> ExecuteAsync(string[] items, uint flags);",
            result.GeneratedSourceText,
            StringComparison.Ordinal);
        Assert.Contains("Action<CloseSafeHandle> handler", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithUnknownQtTypeHintAndWarnPolicy_ReportsWarning()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.UnknownQtHint.xml"] =
                """
                <node>
                  <interface name="org.example.UnknownQtHint">
                    <method name="Execute">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="TenantOpaqueToken" />
                      <arg direction="in" name="token" type="s" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "qtTypeHintPolicy": {
                "unknownHintBehavior": "warn"
              }
            }
            """);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Id == "DBCG012" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Generate_WithUnknownQtTypeHintAndErrorPolicy_ReportsError()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.UnknownQtHint.xml"] =
                """
                <node>
                  <interface name="org.example.UnknownQtHint">
                    <method name="Execute">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="TenantOpaqueToken" />
                      <arg direction="in" name="token" type="s" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "qtTypeHintPolicy": {
                "unknownHintBehavior": "error"
              }
            }
            """);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Id == "DBCG013" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generate_WithQtTypeHintNamespaceRestrictions_RejectsUnqualifiedClrMappings()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.RestrictedQtHint.xml"] =
                """
                <node>
                  <interface name="org.example.RestrictedQtHint">
                    <method name="Execute">
                      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="TenantToken" />
                      <arg direction="in" name="token" type="s" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "qtTypeHintPolicy": {
                "allowedClrNamespaces": ["System"]
              },
              "qtTypeHintMappings": {
                "TenantToken": "TenantGuid"
              }
            }
            """);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Id == "DBCG005" &&
                diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.GetMessage().Contains("unqualified type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WithNamingStrategyPrefixAndSuffix_AppliesConfiguredDecoration()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.alpha.Server.xml"] =
                """
                <node>
                  <interface name="org.alpha.Server">
                    <method name="Ping"/>
                  </interface>
                </node>
                """,
            ["org.beta.Server.xml"] =
                """
                <node>
                  <interface name="org.beta.Server">
                    <method name="Ping"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(
            xmlFiles,
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated",
              "naming": {
                "interfacePrefix": "Db",
                "interfaceSuffix": "Contract",
                "collisionPolicy": "suffix"
              }
            }
            """);

        AssertNoErrors(result.Diagnostics);
        Assert.Contains("public interface DbIServerContract", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.Contains("public interface DbIBetaServerContract", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithNoReplyAndOutArguments_ReportsSemanticDiagnosticWithXmlLocation()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Semantic.xml"] =
                """
                <node>
                  <interface name="org.example.Semantic">
                    <method name="Execute">
                      <annotation name="org.freedesktop.DBus.Method.NoReply" value="true" />
                      <arg direction="out" name="result" type="i" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);
        var diagnostics = result.Diagnostics.Where(static item => item.Id == "DBCG010").ToArray();
        Assert.NotEmpty(diagnostics);
        var diagnostic = diagnostics[0];
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("no-reply", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        var lineSpan = diagnostic.Location.GetLineSpan();
        Assert.EndsWith("org.example.Semantic.xml", lineSpan.Path ?? string.Empty, StringComparison.Ordinal);
        Assert.True(lineSpan.StartLinePosition.Line >= 0);
    }

    [Fact]
    public void Generate_WithInvalidAnnotationBooleanValue_ReportsAnnotationDiagnosticWithSuggestion()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.Annotation.xml"] =
                """
                <node>
                  <interface name="org.example.Annotation">
                    <method name="Execute">
                      <annotation name="org.freedesktop.DBus.Method.NoReply" value="maybe" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);
        var diagnostics = result.Diagnostics.Where(static item => item.Id == "DBCG011").ToArray();
        Assert.NotEmpty(diagnostics);
        var diagnostic = diagnostics[0];
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("true, false, 1, 0", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(diagnostic.Location.GetLineSpan().StartLinePosition.Line >= 0);
    }

    [Fact]
    public void Generate_WithWriteOnlyProperty_EmitsOnlySetterExtensionForProperty()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.WriteOnly.xml"] =
                """
                <node>
                  <interface name="org.example.WriteOnly">
                    <property name="Secret" type="s" access="write"/>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);
        AssertNoErrors(result.Diagnostics.Where(static item => item.Id is not "DBCG010" and not "DBCG011"));
        Assert.Contains("SetSecretAsync(this", result.GeneratedSourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSecretAsync(this", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithDuplicateMethodArgumentNames_ProducesUniqueParameterNames()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.DuplicateArgs.xml"] =
                """
                <node>
                  <interface name="org.example.DuplicateArgs">
                    <method name="Mix">
                      <arg direction="in" name="value" type="s" />
                      <arg direction="in" name="value" type="u" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        AssertNoErrors(result.Diagnostics.Where(static item => item.Id is not "DBCG010" and not "DBCG011"));
        Assert.Contains("Task MixAsync(string value, uint value2);", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithDuplicateTupleElementNames_ProducesUniqueTupleNames()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.TupleArgs.xml"] =
                """
                <node>
                  <interface name="org.example.TupleArgs">
                    <method name="GetPair">
                      <arg direction="out" name="item" type="s" />
                      <arg direction="out" name="item" type="u" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        AssertNoErrors(result.Diagnostics.Where(static item => item.Id is not "DBCG010" and not "DBCG011"));
        Assert.Contains("Task<(string item, uint item2)> GetPairAsync();", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithSingleElementStructType_UsesValueTupleType()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.StructOne.xml"] =
                """
                <node>
                  <interface name="org.example.StructOne">
                    <method name="Echo">
                      <arg direction="in" name="value" type="(s)" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        AssertNoErrors(result.Diagnostics.Where(static item => item.Id is not "DBCG010" and not "DBCG011"));
        Assert.Contains("Task EchoAsync(System.ValueTuple<string> value);", result.GeneratedSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithDictionaryContainerKey_ReportsSignatureDiagnostic()
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.InvalidDictKey.xml"] =
                """
                <node>
                  <interface name="org.example.InvalidDictKey">
                    <method name="Read">
                      <arg direction="out" name="value" type="a{asv}" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Id == "DBCG002" &&
                diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.GetMessage().Contains("Dictionary entry key type", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_WithExcessiveArrayNesting_ReportsSignatureDiagnostic()
    {
        var tooDeepSignature = new string('a', 33) + "s";
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.DeepArray.xml"] =
                $"""
                <node>
                  <interface name="org.example.DeepArray">
                    <method name="Read">
                      <arg direction="out" name="value" type="{tooDeepSignature}" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Id == "DBCG002" &&
                diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.GetMessage().Contains("Array nesting depth", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_WithOverlongSignature_ReportsSignatureDiagnostic()
    {
        var tooLongSignature = new string('s', 256);
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.example.LongSignature.xml"] =
                $"""
                <node>
                  <interface name="org.example.LongSignature">
                    <method name="Read">
                      <arg direction="out" name="value" type="{tooLongSignature}" />
                    </method>
                  </interface>
                </node>
                """
        };

        var result = RunGenerator(xmlFiles);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Id == "DBCG002" &&
                diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.GetMessage().Contains("exceeds the D-Bus maximum", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(GetCorpusCases))]
    public void Generate_WithCorpusCases_MatchesExpectedFragments(string caseDirectory)
    {
        var xmlFiles = LoadCorpusXmlFiles(caseDirectory);
        var configuration = File.ReadAllText(Path.Combine(caseDirectory, "config.json"));
        var result = RunGenerator(xmlFiles, configuration);
        AssertNoErrors(result.Diagnostics);

        var expectedFragments = File.ReadAllLines(Path.Combine(caseDirectory, "expected.contains.txt"))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        foreach (var fragment in expectedFragments)
        {
            Assert.Contains(fragment, result.GeneratedSourceText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(GetCorpusCases))]
    public void Generate_WithCorpusCases_MatchesGoldenSnapshots(string caseDirectory)
    {
        var xmlFiles = LoadCorpusXmlFiles(caseDirectory);
        var configuration = File.ReadAllText(Path.Combine(caseDirectory, "config.json"));
        var result = RunGenerator(xmlFiles, configuration);
        AssertNoErrors(result.Diagnostics);

        var snapshotPath = Path.Combine(caseDirectory, "expected.snapshot.cs");
        var normalizedCurrent = NormalizeSnapshot(result.GeneratedSourceText);
        if (ShouldUpdateSnapshots())
        {
            File.WriteAllText(snapshotPath, normalizedCurrent);
            return;
        }

        Assert.True(
            File.Exists(snapshotPath),
            $"Snapshot file '{snapshotPath}' is missing. Run tests with DBUS_GENERATOR_UPDATE_SNAPSHOTS=1 to generate it.");
        var normalizedExpected = NormalizeSnapshot(File.ReadAllText(snapshotPath));
        Assert.Equal(
            normalizedExpected,
            normalizedCurrent);
    }

    [Theory]
    [MemberData(nameof(GetCorpusCases))]
    public void Generate_WithCorpusCases_HasNoBreakingChangesComparedToSnapshots(string caseDirectory)
    {
        var snapshotPath = Path.Combine(caseDirectory, "expected.snapshot.cs");
        Assert.True(File.Exists(snapshotPath), $"Compatibility baseline snapshot '{snapshotPath}' is missing.");

        var xmlFiles = LoadCorpusXmlFiles(caseDirectory);
        var configuration = File.ReadAllText(Path.Combine(caseDirectory, "config.json"));
        var result = RunGenerator(xmlFiles, configuration);
        AssertNoErrors(result.Diagnostics);

        var baselineSnapshot = NormalizeSnapshot(File.ReadAllText(snapshotPath));
        var currentSnapshot = NormalizeSnapshot(result.GeneratedSourceText);
        var breakingChanges = DbusContractCompatibilityAnalyzer.FindBreakingChanges(baselineSnapshot, currentSnapshot);
        Assert.True(
            breakingChanges.Length == 0,
            "Breaking contract changes detected: " + string.Join("; ", breakingChanges));
    }

    [Fact]
    public void CompatibilityAnalyzer_WithRemovedMembers_DetectsBreakingChanges()
    {
        const string baseline =
            """
            public interface ITest : IDbusObject
            {
                Task PingAsync();
                Task<string> GetNameAsync();
            }
            """;

        const string candidate =
            """
            public interface ITest : IDbusObject
            {
                Task PingAsync();
            }
            """;

        var breakingChanges = DbusContractCompatibilityAnalyzer.FindBreakingChanges(baseline, candidate);
        Assert.Contains(breakingChanges, static item => item.Contains("GetNameAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_WithFuzzedInputs_RemainsDeterministicAndNoAnalyzerCrashes()
    {
        var seed = GetFuzzSeed();
        var iterationCount = GetFuzzIterationCount();
        var random = new Random(seed);
        for (var iteration = 0; iteration < iterationCount; iteration++)
        {
            var xml = BuildFuzzXml(random, iteration);
            var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"fuzz.case.{iteration}.xml"] = xml
            };

            GeneratorExecutionResult? firstRun = null;
            GeneratorExecutionResult? secondRun = null;

            try
            {
                VerifyFuzzCaseDeterminism(
                    xmlFiles,
                    out firstRun,
                    out secondRun);
            }
            catch (Exception ex)
            {
                PersistFuzzFailureCase(iteration, seed, xml, firstRun, secondRun, ex);
                throw;
            }
        }
    }

    public static IEnumerable<object[]> GetCorpusCases()
    {
        var corpusRoot = GetCorpusRoot();
        foreach (var directory in Directory.GetDirectories(corpusRoot).OrderBy(static path => path, StringComparer.Ordinal))
        {
            yield return [directory];
        }
    }

    private static IReadOnlyDictionary<string, string> LoadCorpusXmlFiles(string caseDirectory)
    {
        var xmlDirectory = Path.Combine(caseDirectory, "xml");
        return Directory
            .GetFiles(xmlDirectory, "*.xml", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToDictionary(
                static path => Path.GetFileName(path),
                File.ReadAllText,
                StringComparer.Ordinal);
    }

    private static string BuildFuzzXml(Random random, int iteration)
    {
        static string Pick(Random randomValue, params string[] values)
        {
            return values[randomValue.Next(values.Length)];
        }

        var builder = new StringBuilder();
        builder.AppendLine("<node>");
        builder.Append("  <interface name=\"org.example.Fuzz");
        builder.Append(iteration);
        builder.AppendLine("\">");

        var methodCount = random.Next(1, 4);
        for (var methodIndex = 0; methodIndex < methodCount; methodIndex++)
        {
            builder.Append("    <method name=\"Method");
            builder.Append(methodIndex);
            builder.AppendLine("\">");
            if (random.NextDouble() < 0.35)
            {
                builder.Append("      <annotation name=\"org.freedesktop.DBus.Method.NoReply\" value=\"");
                builder.Append(Pick(random, "true", "false", "1", "0", "maybe"));
                builder.AppendLine("\" />");
            }

            var argumentCount = random.Next(0, 4);
            for (var argumentIndex = 0; argumentIndex < argumentCount; argumentIndex++)
            {
                builder.Append("      <arg direction=\"");
                builder.Append(Pick(random, "in", "out", "side"));
                builder.Append("\" type=\"");
                builder.Append(Pick(random, "s", "u", "i", "ay", "a{sv}", "a{", "(su)", "zz"));
                builder.Append("\" name=\"arg");
                builder.Append(argumentIndex);
                builder.AppendLine("\" />");
            }

            builder.AppendLine("    </method>");
        }

        var propertyCount = random.Next(0, 3);
        for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
        {
            builder.Append("    <property name=\"Prop");
            builder.Append(propertyIndex);
            builder.Append("\" type=\"");
            builder.Append(Pick(random, "s", "u", "as", "a{sv}", "a{"));
            builder.Append("\" access=\"");
            builder.Append(Pick(random, "read", "write", "readwrite", "execute"));
            builder.AppendLine("\" />");
        }

        var signalCount = random.Next(0, 3);
        for (var signalIndex = 0; signalIndex < signalCount; signalIndex++)
        {
            builder.Append("    <signal name=\"Signal");
            builder.Append(signalIndex);
            builder.AppendLine("\">");
            var argumentCount = random.Next(0, 3);
            for (var argumentIndex = 0; argumentIndex < argumentCount; argumentIndex++)
            {
                builder.Append("      <arg direction=\"");
                builder.Append(Pick(random, "out", "in", "bad"));
                builder.Append("\" type=\"");
                builder.Append(Pick(random, "s", "u", "ay", "a{sv}", "("));
                builder.Append("\" name=\"sig");
                builder.Append(argumentIndex);
                builder.AppendLine("\" />");
            }

            builder.AppendLine("    </signal>");
        }

        builder.AppendLine("  </interface>");
        builder.AppendLine("</node>");
        return builder.ToString();
    }

    private static void VerifyFuzzCaseDeterminism(
        IReadOnlyDictionary<string, string> xmlFiles,
        out GeneratorExecutionResult firstRun,
        out GeneratorExecutionResult secondRun)
    {
        firstRun = RunGenerator(xmlFiles, GetNonStrictConfigurationJson());
        secondRun = RunGenerator(xmlFiles, GetNonStrictConfigurationJson());

        Assert.Equal(NormalizeSnapshot(firstRun.GeneratedSourceText), NormalizeSnapshot(secondRun.GeneratedSourceText));
        Assert.Equal(
            NormalizeDiagnostics(firstRun.Diagnostics),
            NormalizeDiagnostics(secondRun.Diagnostics));
        Assert.DoesNotContain(
            firstRun.Diagnostics,
            static diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal));
    }

    private static string NormalizeDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var normalizedLines = diagnostics
            .Select(static item =>
            {
                var lineSpan = item.Location.GetLineSpan();
                return $"{item.Id}|{item.Severity}|{lineSpan.Path}|{lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}|{lineSpan.EndLinePosition.Line}:{lineSpan.EndLinePosition.Character}|{item.GetMessage()}";
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static item => item, StringComparer.Ordinal);

        return string.Join("\n", normalizedLines);
    }

    private static int GetFuzzSeed()
    {
        var configuredSeed = Environment.GetEnvironmentVariable("DBUS_GENERATOR_FUZZ_SEED");
        if (int.TryParse(configuredSeed, out var seed))
        {
            return seed;
        }

        return 424242;
    }

    private static int GetFuzzIterationCount()
    {
        var configuredIterations = Environment.GetEnvironmentVariable("DBUS_GENERATOR_FUZZ_ITERATIONS");
        if (int.TryParse(configuredIterations, out var parsedIterations))
        {
            return Math.Clamp(parsedIterations, 1, 20000);
        }

        var profile = Environment.GetEnvironmentVariable("DBUS_GENERATOR_FUZZ_PROFILE");
        if (string.Equals(profile, "nightly", StringComparison.OrdinalIgnoreCase))
        {
            return 2500;
        }

        return 60;
    }

    private static void PersistFuzzFailureCase(
        int iteration,
        int seed,
        string xml,
        GeneratorExecutionResult? firstRun,
        GeneratorExecutionResult? secondRun,
        Exception exception)
    {
        try
        {
            var baseDirectory = Environment.GetEnvironmentVariable("DBUS_GENERATOR_FUZZ_FAILURE_DIR");
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Path.Combine(GetRepositoryRoot(), "artifacts", "dbus-generator-fuzz-failures");
            }

            Directory.CreateDirectory(baseDirectory!);
            var directoryName = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-iter{iteration:D5}-seed{seed}";
            var caseDirectory = Path.Combine(baseDirectory!, directoryName);
            Directory.CreateDirectory(caseDirectory);

            var failureSignature = CaptureFuzzFailureSignature(exception);
            var reducedXml = TryShrinkFailingFuzzXml(xml, failureSignature);

            File.WriteAllText(Path.Combine(caseDirectory, $"fuzz.case.{iteration}.xml"), xml);
            if (!string.Equals(reducedXml, xml, StringComparison.Ordinal))
            {
                File.WriteAllText(Path.Combine(caseDirectory, $"fuzz.case.{iteration}.reduced.xml"), reducedXml);
            }

            File.WriteAllText(Path.Combine(caseDirectory, "context.txt"), $"seed={seed}\niteration={iteration}\n");
            File.WriteAllText(
                Path.Combine(caseDirectory, "failure-signature.txt"),
                $"type={failureSignature.ExceptionType}\nmessagePrefix={failureSignature.MessagePrefix}\n");
            File.WriteAllText(Path.Combine(caseDirectory, "exception.txt"), exception.ToString());

            if (firstRun is not null)
            {
                File.WriteAllText(Path.Combine(caseDirectory, "first.generated.cs"), firstRun.GeneratedSourceText);
                File.WriteAllText(Path.Combine(caseDirectory, "first.diagnostics.txt"), NormalizeDiagnostics(firstRun.Diagnostics));
            }

            if (secondRun is not null)
            {
                File.WriteAllText(Path.Combine(caseDirectory, "second.generated.cs"), secondRun.GeneratedSourceText);
                File.WriteAllText(Path.Combine(caseDirectory, "second.diagnostics.txt"), NormalizeDiagnostics(secondRun.Diagnostics));
            }
        }
        catch
        {
            // Intentionally ignored: fuzz persistence should not hide the original test failure.
        }
    }

    private static FuzzFailureSignature CaptureFuzzFailureSignature(Exception exception)
    {
        return new FuzzFailureSignature(
            exception.GetType().FullName ?? exception.GetType().Name,
            BuildFailureMessagePrefix(exception.Message));
    }

    private static string TryShrinkFailingFuzzXml(string xml, FuzzFailureSignature signature)
    {
        var normalizedXml = xml.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalizedXml.Split('\n').ToList();
        if (lines.Count <= 6)
        {
            return normalizedXml;
        }

        var granularity = Math.Max(1, lines.Count / 2);
        while (granularity >= 1)
        {
            var reducedInCurrentPass = false;
            for (var start = 0; start + granularity <= lines.Count; start++)
            {
                var candidateLines = new List<string>(lines.Count - granularity);
                candidateLines.AddRange(lines.Take(start));
                candidateLines.AddRange(lines.Skip(start + granularity));
                if (candidateLines.Count == 0)
                {
                    continue;
                }

                var candidateXml = string.Join("\n", candidateLines);
                if (!DoesFuzzCaseFailWithSignature(candidateXml, signature))
                {
                    continue;
                }

                lines = candidateLines;
                reducedInCurrentPass = true;
                start = -1;
            }

            if (!reducedInCurrentPass)
            {
                granularity /= 2;
            }
        }

        return string.Join("\n", lines);
    }

    private static bool DoesFuzzCaseFailWithSignature(string xml, FuzzFailureSignature signature)
    {
        var xmlFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fuzz.shrink.xml"] = xml
        };

        try
        {
            VerifyFuzzCaseDeterminism(xmlFiles, out _, out _);
            return false;
        }
        catch (Exception ex)
        {
            return IsMatchingFuzzFailure(ex, signature);
        }
    }

    private static bool IsMatchingFuzzFailure(Exception exception, FuzzFailureSignature expected)
    {
        var currentType = exception.GetType().FullName ?? exception.GetType().Name;
        if (!string.Equals(currentType, expected.ExceptionType, StringComparison.Ordinal))
        {
            return false;
        }

        var currentPrefix = BuildFailureMessagePrefix(exception.Message);
        return string.Equals(currentPrefix, expected.MessagePrefix, StringComparison.Ordinal);
    }

    private static string BuildFailureMessagePrefix(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal);
        var firstLine = normalized.Split('\n')[0].Trim();
        return firstLine.Length <= 160
            ? firstLine
            : firstLine.Substring(0, 160);
    }

    private readonly record struct FuzzFailureSignature(string ExceptionType, string MessagePrefix);

    private static ImmutableArray<Diagnostic> DeduplicateDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var uniqueByKey = new Dictionary<string, Diagnostic>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics)
        {
            var key = BuildDiagnosticDeduplicationKey(diagnostic);
            uniqueByKey.TryAdd(key, diagnostic);
        }

        return uniqueByKey.Values
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .ThenBy(static item => item.GetMessage(), StringComparer.Ordinal)
            .ThenBy(static item => item.Location.GetLineSpan().Path, StringComparer.Ordinal)
            .ThenBy(static item => item.Location.GetLineSpan().StartLinePosition.Line)
            .ThenBy(static item => item.Location.GetLineSpan().StartLinePosition.Character)
            .ToImmutableArray();
    }

    private static string BuildDiagnosticDeduplicationKey(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        return $"{diagnostic.Id}|{diagnostic.Severity}|{lineSpan.Path}|{lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}|{lineSpan.EndLinePosition.Line}:{lineSpan.EndLinePosition.Character}|{diagnostic.GetMessage()}";
    }

    private static string NormalizeSnapshot(string source)
    {
        var normalizedLineEndings = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        return string.Join(
                "\n",
                normalizedLineEndings
                    .Split('\n')
                    .Select(static line => line.TrimEnd()))
            .Trim();
    }

    private static bool ShouldUpdateSnapshots()
    {
        var value = Environment.GetEnvironmentVariable("DBUS_GENERATOR_UPDATE_SNAPSHOTS");
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetNonStrictConfigurationJson()
    {
        return
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": false,
              "generatedNamespace": "GeneratorHarness.Generated"
            }
            """;
    }

    private static string GetCorpusRoot()
    {
        var repositoryRoot = GetRepositoryRoot();
        var corpusRoot = Path.Combine(
            repositoryRoot,
            "tests",
            "Dbus.ContractGenerator.Tests",
            "TestData",
            "DbusGeneratorCorpus");
        if (!Directory.Exists(corpusRoot))
        {
            throw new DirectoryNotFoundException($"DBus corpus test directory was not found: {corpusRoot}");
        }

        return corpusRoot;
    }

    private static string GetRepositoryRoot()
    {
        var candidateRoots = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var candidateRoot in candidateRoots)
        {
            var directory = new DirectoryInfo(candidateRoot);
            while (directory is not null)
            {
                var solutionPath = Path.Combine(directory.FullName, "Dbus.ContractGenerator.slnx");
                if (File.Exists(solutionPath))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate repository root (Dbus.ContractGenerator.slnx).");
    }

    private static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static item => item.ToString())));
    }

    private static GeneratorExecutionResult RunGenerator(
        IReadOnlyDictionary<string, string> xmlFiles,
        string? configurationJson = null)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText("namespace GeneratorHarness; public sealed class Marker { }", parseOptions),
            CSharpSyntaxTree.ParseText(GetDbusStubs(), parseOptions)
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorHarness",
            syntaxTrees: syntaxTrees,
            references: GetTrustedPlatformMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additionalTexts = new List<AdditionalText>();
        additionalTexts.Add(
            new InMemoryAdditionalText(
                "/tmp/Dbus/dbus-contract-generator.json",
                configurationJson ?? GetDefaultConfigurationJson()));

        foreach (var xmlFile in xmlFiles.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            additionalTexts.Add(new InMemoryAdditionalText($"/tmp/Dbus/{xmlFile.Key}", xmlFile.Value));
        }

        var generator = new DbusContractSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create([generator], additionalTexts, parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var runResult = driver.GetRunResult();
        var allDiagnostics = DeduplicateDiagnostics(
            outputCompilation.GetDiagnostics()
                .Concat(generatorDiagnostics)
                .Concat(runResult.Diagnostics));

        var sourceText = string.Join(
            Environment.NewLine,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static source => source.SourceText.ToString()));

        return new GeneratorExecutionResult(allDiagnostics, sourceText);
    }

    private static string GetDefaultConfigurationJson()
    {
        return
            """
            {
              "schemaVersion": 1,
              "strictConfiguration": true,
              "generatedNamespace": "GeneratorHarness.Generated"
            }
            """;
    }

    private static ImmutableArray<MetadataReference> GetTrustedPlatformMetadataReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is not available.");
        }

        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private static string GetDbusStubs()
    {
        return
            """
            using System;
            namespace Dbus.Contracts
            {
                [AttributeUsage(AttributeTargets.Interface)]
                public sealed class DbusInterfaceAttribute : Attribute
                {
                    public DbusInterfaceAttribute(string interfaceName) { }
                }

                [AttributeUsage(AttributeTargets.Class)]
                public sealed class DbusDictionaryAttribute : Attribute
                {
                }

                public interface IDbusObject
                {
                }

                public readonly struct DbusObjectPath
                {
                }

                public sealed class CloseSafeHandle
                {
                }

                public sealed class DbusPropertyChanges
                {
                }
            }
            """;
    }

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return SourceText.From(content, Encoding.UTF8);
        }
    }

    private sealed class DbusContractCompatibilityAnalyzer
    {
        public static ImmutableArray<string> FindBreakingChanges(string baselineSource, string candidateSource)
        {
            var baselineMembers = ExtractContractMembersCore(baselineSource);
            var candidateMembers = ExtractContractMembersCore(candidateSource);
            var breakingChanges = baselineMembers
                .Where(member => !candidateMembers.Contains(member))
                .OrderBy(static member => member, StringComparer.Ordinal)
                .ToImmutableArray();
            return breakingChanges;
        }

        public static ImmutableHashSet<string> ExtractContractMembers(string source)
        {
            return ExtractContractMembersCore(source);
        }

        private static ImmutableHashSet<string> ExtractContractMembersCore(string source)
        {
            var members = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            var interfaceMatches = Regex.Matches(
                source,
                @"public interface (?<name>[A-Za-z_][A-Za-z0-9_]*) : IDbusObject\s*\{(?<body>.*?)\n\}",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            foreach (Match interfaceMatch in interfaceMatches)
            {
                var interfaceName = interfaceMatch.Groups["name"].Value;
                var body = interfaceMatch.Groups["body"].Value;
                foreach (var line in body.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.EndsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    members.Add("I|" + interfaceName + "|" + trimmed);
                }
            }

            var classMatches = Regex.Matches(
                source,
                @"public class (?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{(?<body>.*?)\n\}",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            foreach (Match classMatch in classMatches)
            {
                var className = classMatch.Groups["name"].Value;
                var body = classMatch.Groups["body"].Value;
                foreach (var line in body.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                        !trimmed.EndsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    members.Add("C|" + className + "|" + trimmed);
                }
            }

            var extensionMatches = Regex.Matches(
                source,
                @"public static class (?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{(?<body>.*?)\n\}",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            foreach (Match extensionMatch in extensionMatches)
            {
                var extensionName = extensionMatch.Groups["name"].Value;
                var body = extensionMatch.Groups["body"].Value;
                foreach (var line in body.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("public static ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    members.Add("E|" + extensionName + "|" + trimmed);
                }
            }

            return members.ToImmutable();
        }
    }

    private sealed record GeneratorExecutionResult(
        ImmutableArray<Diagnostic> Diagnostics,
        string GeneratedSourceText);
}
