using System.Globalization;

namespace Monkeysphere.Core;

public sealed record RecordPatchChange(string Operation, Guid? FieldDefinitionId = null, string? Value = null,
    IReadOnlyList<string>? Values = null, string? ScalarValue = null, IReadOnlyList<string>? Tags = null,
    TemporalValueInput? Temporal = null, LocationValueInput? Location = null);

public sealed class RecordCommandService(IMonkeysphereService records, IRecordCommandStore commands, TimeProvider timeProvider)
{
    public async Task<RecordCommandReceipt> CreateAsync(RecordCommandIdentity identity, Guid recordTypeId, string displayName,
        IReadOnlyList<FieldValueInput> values, IReadOnlyList<string>? aliases = null, CancellationToken cancellationToken = default)
    {
        RequireAction(identity, "records.create");
        RecordCommandReceipt? replay = await commands.GetReceiptAsync(identity, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        PreparedRecord prepared = await PrepareCreateAsync(recordTypeId, displayName, values, aliases, cancellationToken).ConfigureAwait(false);
        return await commands.ExecuteAsync(identity, [new(RecordMutationKind.Create, Guid.CreateVersion7(), prepared)],
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreparedRecord> PrepareCreateAsync(Guid recordTypeId, string displayName, IReadOnlyList<FieldValueInput> values,
        IReadOnlyList<string>? aliases = null, CancellationToken cancellationToken = default)
    {
        if (values.Count > RecordCommandLimits.MaximumFields) throw new DomainValidationException("A record command can supply at most 1000 fields.");
        RecordTypeDetails type = await records.GetRecordTypeAsync(recordTypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Record type was not found.");
        foreach (FieldValueInput input in values)
        {
            RecordTypeField field = type.Fields.FirstOrDefault(field => field.Definition.Id == input.FieldDefinitionId)
                ?? throw new DomainValidationException("The field is not attached to this record type.");
            int shapes = (input.ScalarValue is null ? 0 : 1) + (input.Tags is null ? 0 : 1) +
                (input.Temporal is null ? 0 : 1) + (input.Location is null ? 0 : 1);
            bool matches = field.Definition.TypeId switch
            {
                FieldTypes.Tags => input.Tags is not null,
                FieldTypes.Temporal => input.Temporal is not null,
                FieldTypes.Location => input.Location is not null,
                _ => input.ScalarValue is not null,
            };
            if (shapes != 1 || !matches) throw new DomainValidationException("The supplied value shape does not match the field type.");
        }
        PreparedRecord prepared = await records.PrepareRecordAsync(recordTypeId, displayName, values, aliases, cancellationToken).ConfigureAwait(false);
        if (prepared.SchemaRevision != type.RecordType.Revision) throw new ConcurrencyConflictException("The record type changed during validation. Read it again.");
        return prepared;
    }

    public async Task<RecordCommandReceipt> PatchAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        IReadOnlyList<RecordPatchChange> changes, CancellationToken cancellationToken = default)
    {
        RequireAction(identity, "records.patch");
        RecordCommandReceipt? replay = await commands.GetReceiptAsync(identity, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        PreparedRecordMutation prepared = await PreparePatchAsync(id, expectedRevision, changes, cancellationToken).ConfigureAwait(false);
        return await commands.ExecuteAsync(identity, [prepared], timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreparedRecordMutation> PreparePatchAsync(Guid id, string expectedRevision,
        IReadOnlyList<RecordPatchChange> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count is < 1 or > RecordCommandLimits.MaximumPatchChanges || string.IsNullOrWhiteSpace(expectedRevision))
            throw new DomainValidationException("Patching requires a revision and 1-1000 explicit changes.");
        RecordDetails current = await records.GetRecordAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Record was not found.");
        if (current.Revision != expectedRevision) throw new ConcurrencyConflictException("The record or its fields changed. Read it again before patching.");
        if (current.Values.Select(value => value.FieldDefinitionId).Distinct().Count() != current.Values.Count)
            throw new DomainValidationException("This record contains multiple values per field and requires a dedicated editing workflow.");
        Dictionary<Guid, FieldValueInput> inputs = current.Values.ToDictionary(value => value.FieldDefinitionId, ToInput);
        string name = current.Record.DisplayName;
        IReadOnlyList<string> aliases = current.Aliases;
        HashSet<string> targets = new(StringComparer.Ordinal);
        HashSet<Guid> setFields = [];
        foreach (RecordPatchChange change in changes)
        {
            int shapes = (change.ScalarValue is null ? 0 : 1) + (change.Tags is null ? 0 : 1) +
                (change.Temporal is null ? 0 : 1) + (change.Location is null ? 0 : 1);
            string target;
            switch (change.Operation)
            {
                case "set_name" when change.Value is not null && change.Values is null && change.FieldDefinitionId is null && shapes == 0:
                    target = "name";
                    name = change.Value;
                    break;
                case "replace_aliases" when change.Values is not null && change.Value is null && change.FieldDefinitionId is null && shapes == 0:
                    target = "aliases";
                    aliases = change.Values;
                    break;
                case "set_field" when change.FieldDefinitionId is Guid fieldId && change.Value is null && change.Values is null && shapes == 1:
                    target = fieldId.ToString("D");
                    RecordTypeField field = current.AvailableFields.FirstOrDefault(field => field.Definition.Id == fieldId)
                        ?? throw new DomainValidationException("The field is not attached to this record type.");
                    bool shapeMatches = field.Definition.TypeId switch
                    {
                        FieldTypes.Tags => change.Tags is not null,
                        FieldTypes.Temporal => change.Temporal is not null,
                        FieldTypes.Location => change.Location is not null,
                        _ => change.ScalarValue is not null,
                    };
                    if (!shapeMatches) throw new DomainValidationException("The supplied value shape does not match the field type.");
                    inputs[fieldId] = new(fieldId, change.ScalarValue, change.Tags, change.Temporal, change.Location);
                    setFields.Add(fieldId);
                    break;
                case "clear_field" when change.FieldDefinitionId is Guid clearId && change.Value is null && change.Values is null && shapes == 0:
                    target = clearId.ToString("D");
                    if (!current.AvailableFields.Any(field => field.Definition.Id == clearId))
                        throw new DomainValidationException("The field is not attached to this record type.");
                    inputs.Remove(clearId);
                    break;
                default:
                    throw new DomainValidationException("The patch operation or its value shape is invalid.");
            }
            if (!targets.Add(target)) throw new DomainValidationException("A patch cannot change the same target more than once.");
        }
        PreparedRecordMutation prepared = await records.PrepareRecordUpdateAsync(id, name, inputs.Values.ToArray(), aliases,
            expectedRevision, cancellationToken).ConfigureAwait(false);
        if (setFields.Any(id => !prepared.Record.Values.Any(value => value.FieldDefinitionId == id)))
            throw new DomainValidationException("Use clear_field to remove a value; set_field requires a nonempty value.");
        return prepared;
    }

    private static void RequireAction(RecordCommandIdentity identity, string action)
    {
        identity.Validate();
        if (identity.Action != action) throw new DomainValidationException("The command action is invalid.");
    }

    private static FieldValueInput ToInput(RecordValue value) => new(value.FieldDefinitionId,
        value.TextValue ?? value.NumberValue ?? value.DateValue, value.Tags,
        value.TemporalValue is not null && value.TemporalPrecision is TemporalPrecision precision
            ? new(value.TemporalValue, precision, value.IsApproximate, value.ApproximationNote) : null,
        value.Location is LocationValue location ? new(location.DisplayContext,
            location.Latitude?.ToString("R", CultureInfo.InvariantCulture), location.Longitude?.ToString("R", CultureInfo.InvariantCulture),
            location.AccuracyMetres?.ToString("R", CultureInfo.InvariantCulture), location.ApproximationRadiusKilometres?.ToString("R", CultureInfo.InvariantCulture)) : null);
}
