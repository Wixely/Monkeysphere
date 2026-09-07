using System.Security.Cryptography;
using System.Text.Json;

namespace Monkeysphere.Core;

public static class ContactImportCommandLimits
{
    public const int MaximumRetainedCommandsPerDomain = 1000;
    public const int MaximumRetainedOutcomesPerDomain = 100000;
    public const int RetryWindowHours = 24;
    public const int TombstoneDays = 7;
}

public sealed record ContactImportCommand(UploadOwner Owner, Guid IdempotencyKey, Guid PreviewId, string RequestHash)
{
    public void Validate()
    {
        Owner.Validate();
        if (IdempotencyKey == Guid.Empty || PreviewId == Guid.Empty || RequestHash is not { Length: 64 } || !RequestHash.All(char.IsAsciiHexDigit))
            throw new DomainValidationException("The import command identity is invalid.");
    }
}
public sealed record ContactImportOutcome(int ContactIndex, VCardImportAction Action, Guid? RecordId, string? Revision);
public sealed record ContactImportReceipt(Guid IdempotencyKey, Guid PreviewId, VCardImportResult Result, int ContactCount,
    DateTimeOffset CompletedAtUtc, DateTimeOffset RetryUntilUtc);

public interface IContactImportCommandStore
{
    Task<ContactImportReceipt?> FindAsync(ContactImportCommand command, CancellationToken cancellationToken = default);
    Task<bool> WasPreviewAppliedAsync(UploadOwner owner, Guid previewId, CancellationToken cancellationToken = default);
    Task<ContactImportReceipt> ExecuteAsync(ContactImportCommand command, IReadOnlyList<VCardPreparedImport> prepared, string expectedRevision,
        DateTimeOffset previewExpiresAtUtc, CancellationToken cancellationToken = default);
    Task<ContactImportReceipt> GetAsync(UploadOwner owner, Guid idempotencyKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContactImportOutcome>> ReadOutcomesAsync(UploadOwner owner, Guid idempotencyKey, int page, int pageSize,
        CancellationToken cancellationToken = default);
}

public sealed class ContactImportCommandService(IContactImportCommandStore commands, IContactImportPreviewStore previews,
    IVCardService vcards, ICurrentDomain currentDomain)
{
    public async Task<ContactImportReceipt> ApplyAsync(UploadOwner owner, Guid previewId, string expectedRevision, Guid idempotencyKey,
        IReadOnlyList<VCardImportSelection> selections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        owner.Validate();
        if (owner.DomainId != currentDomain.Id) throw new DomainValidationException("The import domain does not match the selected domain.");
        if (expectedRevision is not { Length: 32 } || !expectedRevision.All(char.IsAsciiHexDigit) || selections.Count is < 1 or > VCardParser.MaximumCards)
            throw new DomainValidationException("Supply the preview revision and 1-1000 explicit contact selections.");
        VCardImportSelection[] choices = selections.OrderBy(choice => choice.ContactIndex).ToArray();
        if (choices.Where((choice, index) => choice.ContactIndex != index || !Enum.IsDefined(choice.Action) ||
            ((choice.Action is VCardImportAction.MergeNonConflicting or VCardImportAction.ReplaceMappedValues)
                ? choice.ExistingRecordId is null || choice.ExistingRecordId == Guid.Empty : choice.ExistingRecordId is not null)).Any())
            throw new DomainValidationException("Choose one valid action for every consecutive contact index, with targets only for merge or replace.");
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { previewId, expectedRevision, selections = choices })));
        ContactImportCommand command = new(owner, idempotencyKey, previewId, hash);
        command.Validate();
        ContactImportReceipt? replay = await commands.FindAsync(command, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        if (await commands.WasPreviewAppliedAsync(owner, previewId, cancellationToken).ConfigureAwait(false))
            throw new CommandReplayException("preview_consumed", "This contact preview was already applied with another retry key.");
        try
        {
            return await previews.WithPreviewAsync(owner, previewId, async (preview, expiry, token) =>
            {
                ContactImportReceipt? committed = await commands.FindAsync(command, token).ConfigureAwait(false);
                if (committed is not null) return committed;
                if (preview.Revision != expectedRevision) throw new ConcurrencyConflictException("The supplied revision does not match the reviewed preview.");
                IReadOnlyList<VCardPreparedImport> prepared = await vcards.PrepareImportAsync(preview, choices, token).ConfigureAwait(false);
                return await commands.ExecuteAsync(command, prepared, expectedRevision, expiry, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is UploadException or ConcurrencyConflictException)
        {
            // Another identical apply can commit while this request waits for the staging lease.
            replay = await commands.FindAsync(command, cancellationToken).ConfigureAwait(false);
            if (replay is not null) return replay;
            throw;
        }
    }
}
