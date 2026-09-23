using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Relating many records to one other record in a single stroke: a set of people to the place they
/// work. The relationship store already creates them one at a time, so what these pin is what the
/// bulk form adds — that each record gets its own outcome, that a directional type can be applied
/// from either end, and that a symmetric one cannot be stored twice meaning the same thing.
/// </summary>
public sealed class RecordRelationshipCommandTests
{
    [Fact]
    public async Task ManyRecordsCanBeRelatedToOneOtherInASingleStroke()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRecordRelationshipCommandService relating = application.Services.GetRequiredService<IRecordRelationshipCommandService>();

        RecordType person = await records.CreateRecordTypeAsync("Employed person");
        RecordType place = await records.CreateRecordTypeAsync("Workplace");
        RelationshipType worksAt = await relationships.CreateTypeAsync(new(
            "works at", RelationshipDirectionality.Directional, "employs"));
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", []);
        RecordDetails grace = await records.CreateRecordAsync(person.Id, "Grace", []);
        RecordDetails acme = await records.CreateRecordAsync(place.Id, "Acme", []);

        IReadOnlyList<RecordRelationshipChange> changes = await relating.ApplyAsync(new(
            [ada.Record.Id, grace.Record.Id], worksAt.Id, acme.Record.Id));

        Assert.All(changes, change => Assert.Equal(RecordRelationshipOutcome.Created, change.Outcome));

        // Each person reads it forwards, and the workplace reads all of them back through the
        // inverse label, which is what makes the direction the right way round.
        RelationshipView fromAda = Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
        Assert.Equal("works at", fromAda.Label);
        Assert.Equal(acme.Record.Id, fromAda.RelatedRecordId);

        IReadOnlyList<RelationshipView> fromAcme = await relationships.ListForRecordAsync(acme.Record.Id);
        Assert.Equal(2, fromAcme.Count);
        Assert.All(fromAcme, view => Assert.Equal("employs", view.Label));
    }

    [Fact]
    public async Task TheSelectionCanBeEitherEndOfADirectionalRelationship()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRecordRelationshipCommandService relating = application.Services.GetRequiredService<IRecordRelationshipCommandService>();

        RecordType person = await records.CreateRecordTypeAsync("Directed person");
        RelationshipType mentors = await relationships.CreateTypeAsync(new(
            "mentors", RelationshipDirectionality.Directional, "is mentored by"));
        RecordDetails grace = await records.CreateRecordAsync(person.Id, "Grace", []);
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", []);

        // The selection as the far end: Grace mentors Ada, expressed by selecting Ada.
        _ = await relating.ApplyAsync(new([ada.Record.Id], mentors.Id, grace.Record.Id, SelectedAreSource: false));

        Assert.Equal("is mentored by", Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id)).Label);
        Assert.Equal("mentors", Assert.Single(await relationships.ListForRecordAsync(grace.Record.Id)).Label);
    }

    [Fact]
    public async Task RecordsAlreadyRelatedAreReportedRatherThanDuplicated()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRecordRelationshipCommandService relating = application.Services.GetRequiredService<IRecordRelationshipCommandService>();

        RecordType person = await records.CreateRecordTypeAsync("Repeat person");
        RelationshipType worksAt = await relationships.CreateTypeAsync(new(
            "works at", RelationshipDirectionality.Directional, "employs"));
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", []);
        RecordDetails grace = await records.CreateRecordAsync(person.Id, "Grace", []);
        RecordDetails acme = await records.CreateRecordAsync(person.Id, "Acme", []);

        _ = await relating.ApplyAsync(new([ada.Record.Id], worksAt.Id, acme.Record.Id));

        // Ada again alongside somebody new: applying the same assignment twice must be safe, and
        // must not stop the record that had not been done yet.
        IReadOnlyList<RecordRelationshipChange> second = await relating.ApplyAsync(new(
            [ada.Record.Id, grace.Record.Id, acme.Record.Id], worksAt.Id, acme.Record.Id));

        Assert.Equal(RecordRelationshipOutcome.AlreadyRelated, second.Single(c => c.RecordId == ada.Record.Id).Outcome);
        Assert.Equal(RecordRelationshipOutcome.Created, second.Single(c => c.RecordId == grace.Record.Id).Outcome);

        // The record being related to cannot be related to itself, and says so rather than failing
        // the whole assignment on a constraint.
        Assert.Equal(RecordRelationshipOutcome.SameRecord, second.Single(c => c.RecordId == acme.Record.Id).Outcome);

        Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
    }

    [Fact]
    public async Task ASymmetricRelationshipIsNotStoredTwiceMeaningTheSameThing()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRecordRelationshipCommandService relating = application.Services.GetRequiredService<IRecordRelationshipCommandService>();

        RecordType person = await records.CreateRecordTypeAsync("Symmetric person");
        RelationshipType knows = await relationships.CreateTypeAsync(new(
            "knows", RelationshipDirectionality.Symmetric));
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", []);
        RecordDetails grace = await records.CreateRecordAsync(person.Id, "Grace", []);

        _ = await relating.ApplyAsync(new([ada.Record.Id], knows.Id, grace.Record.Id));

        // The other way round is the same relationship for a symmetric type, whichever end was
        // selected, so the second assignment must find the first rather than add a mirror of it.
        IReadOnlyList<RecordRelationshipChange> mirrored =
            await relating.ApplyAsync(new([grace.Record.Id], knows.Id, ada.Record.Id));

        Assert.Equal(RecordRelationshipOutcome.AlreadyRelated, Assert.Single(mirrored).Outcome);
        Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
    }

    [Fact]
    public async Task AnAssignmentThatCannotMeanOneThingIsRefusedBeforeAnythingIsWritten()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRecordRelationshipCommandService relating = application.Services.GetRequiredService<IRecordRelationshipCommandService>();

        RecordType person = await records.CreateRecordTypeAsync("Refused person");
        RelationshipType knows = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Symmetric));
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", []);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            relating.ApplyAsync(new([], knows.Id, ada.Record.Id)));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            relating.ApplyAsync(new([ada.Record.Id], Guid.Empty, ada.Record.Id)));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            relating.ApplyAsync(new([ada.Record.Id], knows.Id, Guid.Empty)));
        await Assert.ThrowsAsync<DomainValidationException>(() => relating.ApplyAsync(new(
            [.. Enumerable.Range(0, RecordRelationshipCommandService.MaximumRecords + 1).Select(_ => Guid.CreateVersion7())],
            knows.Id,
            ada.Record.Id)));

        // A type that is gone is a failure of the request rather than an outcome per record.
        await relationships.RetireTypeAsync(knows.Id);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            relating.ApplyAsync(new([ada.Record.Id], knows.Id, ada.Record.Id)));
    }
}
