using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using SmartCon.Agent;
using SmartCon.Agent.Services;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Unit twin of <c>AgentToolRegistry.ValidateDefinition</c> for the WHOLE
/// production catalog. The registry validates a schema only where the catalog
/// is built - inside the Revit host - so one malformed schema (a trailing comma,
/// night run 2 R0) passed the green unit gates and then broke every integration
/// test that creates the production host. The unit host has no RevitAPI
/// runtime: it cannot instantiate the Revit-side tools, and even
/// <see cref="MethodBase.GetMethodBody"/> fails on them. So the schema is read
/// where it is stored, straight from the assembly metadata: the constant
/// string literal the tool assigns to <c>InputSchemaJson</c> in its own IL. A
/// tool whose schema is assembled at run time fails here loudly and has to
/// become a constant again.
/// </summary>
public sealed class AgentToolSchemaJsonTests
{
    private const string SchemaSetter = "set_InputSchemaJson";

    private static readonly OpCode[] SingleByteCodes = BuildOpcodeTable(doubleByte: false);

    private static readonly OpCode[] DoubleByteCodes = BuildOpcodeTable(doubleByte: true);

    [Fact]
    public void EveryProductionToolSchema_IsAValidJsonObject()
    {
        var types = AgentToolRegistration.ToolTypes
            .Concat(AgentCoreRegistration.ToolTypes)
            .ToList();
        var offenders = new List<string>();
        var checkedTools = 0;

        using var metadata = new CatalogMetadata();
        foreach (var type in types)
        {
            var schema = metadata.ReadSchemaLiteral(type, out var status);
            if (schema is null && status == SchemaReadStatus.AssembledAtRunTime
                                && type.Assembly == typeof(AgentOrchestrator).Assembly)
            {
                // An agent-side tool may build its schema at run time: the unit
                // host can instantiate it (no Revit types involved), as
                // AgentToolSchemaDocumentIdTests already does.
                schema = ((IAgentTool)AgentToolSchemaDocumentIdTests.Instantiate(type))
                    .Definition.InputSchemaJson;
            }

            if (schema is null)
            {
                offenders.Add(type.Name + ": " + Describe(status));
                continue;
            }

            checkedTools++;
            try
            {
                using var document = JsonDocument.Parse(schema);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    offenders.Add(type.Name + ": schema must be a JSON object");
            }
            catch (JsonException ex)
            {
                offenders.Add(type.Name + ": invalid InputSchemaJson: " + ex.Message);
            }
        }

        // The reasons first: a silent skip would switch the guard off, so the
        // count is part of the contract too.
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
        Assert.Equal(types.Count, checkedTools);
    }

    [Fact]
    public void SchemaLiteral_IsReadFromTheRealTool_WithRoutingArgumentInsideProperties()
    {
        // Positive control of the reader: the literal is the complete schema,
        // not a fragment of it.
        using var metadata = new CatalogMetadata();
        var schema = metadata.ReadSchemaLiteral(typeof(ListLevelsTool), out var status);

        Assert.Equal(SchemaReadStatus.Literal, status);
        Assert.NotNull(schema);
        Assert.StartsWith("{\"type\":\"object\",\"properties\":{\"documentId\":{\"type\":\"string\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingComma_TheShapeOfTheRegression_IsRejectedByTheSameCheck()
    {
        // get_project_overview as committed in 721dbd8c: the shared documentId
        // property kept its separator comma while being the LAST member of
        // properties, i.e. "...},}".
        const string broken =
            """{"type":"object","properties":{"documentId":{"type":"string"},},"additionalProperties":false}""";

        Assert.ThrowsAny<JsonException>(() =>
        {
            using var document = JsonDocument.Parse(broken);
        });

        // The separator comma between two members is not the defect.
        using var valid = JsonDocument.Parse(
            """{"type":"object","properties":{"documentId":{"type":"string"}},"additionalProperties":false}""");
        Assert.Equal(JsonValueKind.Object, valid.RootElement.ValueKind);
    }

    [Fact]
    public void SharedDocumentIdProperty_IsValidJsonAsFirstAndAsLastMember()
    {
        // 721dbd8c used the first-member constant (separator comma) as the only
        // member of get_project_overview.properties: "...},}". Both constants
        // are compile-time strings, so both shapes are checked here as they
        // are written into the tool IL.
        const string asFirst = "{\"type\":\"object\",\"properties\":{" +
                               AgentReadToolBase.DocumentIdPropertyJson +
                               "\"viewId\":{\"type\":\"integer\"}},\"additionalProperties\":false}";
        const string asLast = "{\"type\":\"object\",\"properties\":{" +
                              AgentReadToolBase.DocumentIdPropertyJsonLast +
                              "},\"additionalProperties\":false}";

        JsonElement firstProperty;
        using (var document = JsonDocument.Parse(asFirst))
            firstProperty = document.RootElement.GetProperty("properties").GetProperty("documentId").Clone();
        using var last = JsonDocument.Parse(asLast);
        var lastProperty = last.RootElement.GetProperty("properties").GetProperty("documentId");

        Assert.Single(last.RootElement.GetProperty("properties").EnumerateObject());
        Assert.Equal(firstProperty.GetRawText(), lastProperty.GetRawText());
        Assert.Equal("string", lastProperty.GetProperty("type").GetString());
    }

    private static string Describe(SchemaReadStatus status) => status switch
    {
        SchemaReadStatus.MissingAssignment => "no assignment to " + SchemaSetter + " was found in the tool's IL",
        SchemaReadStatus.AmbiguousAssignment => "more than one assignment to " + SchemaSetter + ": the schema literal is ambiguous",
        SchemaReadStatus.AssembledAtRunTime => "InputSchemaJson is not a constant string literal (assembled at run time) and the tool cannot be instantiated in the unit host",
        SchemaReadStatus.NotAnObjectLiteral => "the literal assigned to InputSchemaJson does not start with '{'",
        _ => "schema was not read: " + status
    };

    private enum SchemaReadStatus
    {
        Literal,
        MissingAssignment,
        AmbiguousAssignment,
        AssembledAtRunTime,
        NotAnObjectLiteral
    }

    /// <summary>
    /// Reads method bodies of the tool assemblies from their PE metadata; no
    /// type of the tool is loaded or instantiated by this.
    /// </summary>
    private sealed class CatalogMetadata : IDisposable
    {
        private readonly Dictionary<string, PEReader> _readers = new(StringComparer.Ordinal);

        private readonly Dictionary<string, MetadataReader> _metadata = new(StringComparer.Ordinal);

        /// <summary>
        /// The string literal assigned to <c>InputSchemaJson</c> by the tool's own
        /// methods (the constructor and factory-built definitions alike). Null
        /// with a status when the assignment is missing, ambiguous or fed by
        /// anything but a constant literal.
        /// </summary>
        public string? ReadSchemaLiteral(Type type, out SchemaReadStatus status)
        {
            status = SchemaReadStatus.Literal;
            var path = type.Assembly.Location;
            if (!_readers.TryGetValue(path, out var peReader))
            {
                peReader = new PEReader(File.OpenRead(path));
                _readers[path] = peReader;
                _metadata[path] = peReader.GetMetadataReader();
            }

            var reader = _metadata[path];
            var typeDefinition = reader.GetTypeDefinition(
                MetadataTokens.TypeDefinitionHandle(type.MetadataToken));

            var assignments = 0;
            string? literal = null;
            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var rva = method.RelativeVirtualAddress;
                if (rva == 0)
                    continue;

                var il = peReader.GetMethodBody(rva).GetILBytes()
                         ?? throw new InvalidOperationException("Method body is not available: " + type.Name);
                string? pending = null;
                foreach (var instruction in ReadInstructions(il))
                {
                    if (instruction.Code == OpCodes.Ldstr)
                    {
                        pending = reader.GetUserString(MetadataTokens.UserStringHandle(instruction.Token));
                        continue;
                    }

                    if (instruction.Code != OpCodes.Call && instruction.Code != OpCodes.Callvirt)
                        continue;

                    var target = MetadataTokens.EntityHandle(instruction.Token);
                    var targetName = target.Kind switch
                    {
                        HandleKind.MemberReference => reader.GetString(reader.GetMemberReference((MemberReferenceHandle)target).Name),
                        HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)target).Name),
                        _ => string.Empty
                    };
                    if (!string.Equals(targetName, SchemaSetter, StringComparison.Ordinal))
                    {
                        // Another call consumes whatever was loaded last.
                        pending = null;
                        continue;
                    }

                    assignments++;
                    literal = pending;
                    pending = null;
                }
            }

            if (assignments == 0)
                status = SchemaReadStatus.MissingAssignment;
            else if (assignments > 1)
                status = SchemaReadStatus.AmbiguousAssignment;
            else if (literal is null)
                status = SchemaReadStatus.AssembledAtRunTime;
            else if (!literal.TrimStart().StartsWith("{", StringComparison.Ordinal))
                status = SchemaReadStatus.NotAnObjectLiteral;

            return status == SchemaReadStatus.Literal ? literal : null;
        }

        public void Dispose()
        {
            foreach (var reader in _readers.Values)
                reader.Dispose();
        }
    }

    private static IEnumerable<(OpCode Code, int Token)> ReadInstructions(byte[] il)
    {
        var position = 0;
        while (position < il.Length)
        {
            var first = il[position++];
            var code = first == 0xFE ? DoubleByteCodes[il[position++]] : SingleByteCodes[first];
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
            if (position + size > il.Length)
                throw new InvalidOperationException(
                    "IL desynchronized at offset " + position + ", opcode " + code.Name);

            yield return (code, size == 4 ? BitConverter.ToInt32(il, position) : 0);
            position += size;
        }
    }

    private static OpCode[] BuildOpcodeTable(bool doubleByte)
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode code)
                continue;

            // OpCode.Value is a signed short: the two-byte codes (0xFExx) are
            // negative until cast to ushort.
            var value = (ushort)code.Value;
            if (doubleByte == (value > 0xFF))
                table[value & 0xFF] = code;
        }

        return table;
    }
}
