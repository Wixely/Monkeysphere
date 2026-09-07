using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpCanCreateCustomSchemaReuseFieldsAndCreateTypedRecords()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["structure.write", "records.read", "records.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        var createType = new { domainId, name = "  Custom people  ", symbol = "P", idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument typeResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record_type", createType);
        RecordMutationOutcome type = Structured(typeResult).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0];
        using JsonDocument typeReplay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record_type", createType);
        Assert.Equal(Structured(typeResult).GetRawText(), Structured(typeReplay).GetRawText());
        using JsonDocument typeConflict = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record_type",
            createType with { name = "Different" });
        AssertWriteError(typeConflict, "retry_conflict");
        var fieldInput = new
        {
            domainId,
            recordTypeId = type.Id,
            expectedRevision = type.Revision,
            name = "Category",
            typeId = "CHOICE",
            isRequired = true,
            choiceOptions = new[] { " Friend ", "Colleague" },
            idempotencyKey = Guid.CreateVersion7(),
        };
        using JsonDocument fieldResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_and_attach_field", fieldInput);
        RecordCommandReceipt fieldReceipt = Structured(fieldResult).Deserialize<RecordCommandReceipt>(JsonOptions)!;
        Assert.Equal(2, fieldReceipt.Items.Count);
        RecordMutationOutcome field = fieldReceipt.Items[0];
        Assert.Equal(type.Id, fieldReceipt.Items[1].Id);
        Assert.NotEqual(type.Revision, fieldReceipt.Items[1].Revision);
        using JsonDocument temporalResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_and_attach_field",
            new { domainId, recordTypeId = type.Id, expectedRevision = fieldReceipt.Items[1].Revision, name = "Period", typeId = "temporal", idempotencyKey = Guid.CreateVersion7() });
        RecordMutationOutcome temporal = Structured(temporalResult).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0];
        using JsonDocument fieldReplay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_and_attach_field", fieldInput);
        Assert.Equal(Structured(fieldResult).GetRawText(), Structured(fieldReplay).GetRawText());
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_and_attach_field",
            fieldInput with { idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_revision");
        using JsonDocument discovery = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_field_definitions", new { domainId });
        RemoteReusableField[] fields = Structured(discovery).GetProperty("items").Deserialize<RemoteReusableField[]>(JsonOptions)!;
        Assert.Equal(field.Revision, fields.Single(item => item.Id == field.Id).Revision);
        Assert.Equal(["Friend", "Colleague"], fields.Single(item => item.Id == field.Id).ChoiceOptions);
        using JsonDocument secondType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record_type",
            new { domainId, name = "Other people", idempotencyKey = Guid.CreateVersion7() });
        RecordMutationOutcome other = Structured(secondType).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0];
        var attach = new
        {
            domainId,
            recordTypeId = other.Id,
            fieldDefinitionId = field.Id,
            expectedRevision = other.Revision,
            expectedFieldRevision = field.Revision,
            isRequired = true,
            idempotencyKey = Guid.CreateVersion7(),
        };
        using JsonDocument attached = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "attach_field", attach);
        Assert.Equal(other.Id, Structured(attached).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0].Id);
        using JsonDocument attachReplay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "attach_field", attach);
        Assert.Equal(Structured(attached).GetRawText(), Structured(attachReplay).GetRawText());
        var createRecord = new
        {
            domainId,
            recordTypeId = type.Id,
            displayName = "Fictional person",
            idempotencyKey = Guid.CreateVersion7(),
            aliases = new[] { "Alias" },
            values = new RemoteFieldInput[] { new(field.Id, "Friend"), new(temporal.Id, Temporal: new("2010s", "decade", true)) },
        };
        using JsonDocument recordResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record", createRecord);
        Guid recordId = Structured(recordResult).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0].Id;
        using JsonDocument missingRequired = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record",
            createRecord with { values = [], idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(missingRequired, "validation_failed");
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordDetails record = (await records.GetRecordAsync(recordId))!;
        Assert.Equal("Custom people", record.Record.RecordTypeName);
        Assert.Equal("P", (await records.GetRecordTypeAsync(type.Id))!.RecordType.Symbol);
        Assert.Equal(["Alias"], record.Aliases);
        Assert.Equal(TemporalPrecision.Decade, record.Values.Single(value => value.FieldDefinitionId == temporal.Id).TemporalPrecision);
        Assert.Single((await records.GetRecordTypeAsync(other.Id))!.Fields);
        Assert.Equal(2, (await records.ListFieldDefinitionsAsync()).Count);
    }

    [Theory]
    [InlineData("records.read")]
    [InlineData("records.write")]
    [InlineData("relationships.write")]
    public async Task SchemaCommandsRequireExplicitStructureGrant(string grant)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        object[] inputs = [
            new { domainId, name = "Denied", idempotencyKey = Guid.CreateVersion7() },
            new { domainId, recordTypeId = Guid.NewGuid(), expectedRevision = "revision", name = "Denied", typeId = "text", idempotencyKey = Guid.CreateVersion7() },
            new { domainId, recordTypeId = Guid.NewGuid(), fieldDefinitionId = Guid.NewGuid(), expectedRevision = "revision", expectedFieldRevision = "revision", idempotencyKey = Guid.CreateVersion7() },
        ];
        string[] names = ["create_record_type", "create_and_attach_field", "attach_field"];
        for (int index = 0; index < names.Length; index++)
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", names[index], inputs[index]);
            AssertWriteError(denied, "permission_denied");
        }
        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities discovery = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.All(discovery.Tools.Where(tool => names.Contains(tool.Name)), tool => Assert.False(tool.Allowed));
    }

    [Fact]
    public async Task SchemaCommandsRejectStaleFieldsRequiredGapsAndForeignReferences()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType source = await records.CreateRecordTypeAsync("Source");
        FieldDefinition field = await records.CreateAndAttachFieldAsync(source.Id, new("Shared", "text", false));
        RecordType target = await records.CreateRecordTypeAsync("Target");
        _ = await records.CreateRecordAsync(target.Id, "Existing record", []);
        Guid otherDomain = (await scope.ServiceProvider.GetRequiredService<IDomainCatalog>().CreateAsync("Foreign schema")).Id;
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["structure.write"]);
        var attach = new
        {
            domainId = MonkeysphereDomains.DefaultId,
            recordTypeId = target.Id,
            fieldDefinitionId = field.Id,
            expectedRevision = target.Revision,
            expectedFieldRevision = field.Revision,
            isRequired = true,
            idempotencyKey = Guid.CreateVersion7(),
        };
        using JsonDocument required = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "attach_field", attach);
        AssertWriteError(required, "validation_failed");
        using JsonDocument foreign = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "attach_field", attach with { domainId = otherDomain });
        AssertWriteError(foreign, "not_found");
        using JsonDocument missingField = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "attach_field",
            attach with { fieldDefinitionId = Guid.NewGuid(), isRequired = false });
        AssertWriteError(missingField, "not_found");
        await records.RenameFieldAsync(field.Id, "Changed definition");
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "attach_field", attach with { isRequired = false });
        AssertWriteError(stale, "stale_revision");
        var create = new
        {
            attach.domainId,
            recordTypeId = target.Id,
            expectedRevision = target.Revision,
            name = "New required",
            typeId = "text",
            isRequired = true,
            idempotencyKey = Guid.CreateVersion7(),
        };
        using JsonDocument failedCreate = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_and_attach_field", create);
        AssertWriteError(failedCreate, "validation_failed");
        using JsonDocument invalidType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_and_attach_field",
            create with { typeId = "invalid type!", isRequired = false });
        AssertWriteError(invalidType, "validation_failed");
        using JsonDocument missingChoice = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_and_attach_field",
            create with { typeId = "choice", isRequired = false });
        AssertWriteError(missingChoice, "validation_failed");
        Assert.Single(await records.ListFieldDefinitionsAsync());
        Assert.Empty((await records.GetRecordTypeAsync(target.Id))!.Fields);
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
    }
}
