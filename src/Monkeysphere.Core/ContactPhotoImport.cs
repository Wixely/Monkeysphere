namespace Monkeysphere.Core;

/// <summary>What happened to one card's photo, so an import can report per contact rather than in bulk.</summary>
public sealed record ContactPhotoImportOutcome(int ContactIndex, Guid RecordId, bool Attached, bool WasRemote, string? Failure);

public sealed record ContactPhotoImportResult(
    int Attached, int Skipped, int Failed, IReadOnlyList<ContactPhotoImportOutcome> Outcomes);

public static class ContactPhotoLimits
{
    /// <summary>Matches the image pipeline's own ceiling; a larger download is abandoned unread.</summary>
    public const int MaximumBytes = 10 * 1024 * 1024;

    public const int TimeoutSeconds = 15;
}

/// <summary>
/// Fetches a photo a card only referenced. Deliberately an abstraction with no default: the
/// application makes no outbound request of any kind unless a host supplies one of these, and the
/// operator asked for it on this import.
/// </summary>
public interface IContactPhotoFetcher
{
    /// <summary>
    /// Reads the photo at an absolute http or https address, or returns null when it cannot be had.
    /// Implementations must bound size and time and must never throw for an ordinary failure: one
    /// unreachable photo is not a reason to lose an import.
    /// </summary>
    Task<ContactPhotoBytes?> FetchAsync(string url, CancellationToken cancellationToken = default);
}

public sealed record ContactPhotoBytes(byte[] Content, string FileName);

/// <summary>
/// Attaches the photos an import found. Inline photos need nothing but a decoder; a referenced one
/// is only fetched when the caller passes a fetcher, which is how the deployment's no-outbound-
/// request default is kept.
/// </summary>
public sealed class ContactPhotoImporter(IRecordImageService images, IMonkeysphereService records, IVCardStore store)
{
    /// <summary>
    /// Attaches photos to the records an import just produced. The records are found through the
    /// provenance the import recorded, so this needs nothing threaded through the import result.
    /// </summary>
    /// <param name="approvedUrls">
    /// The exact addresses a person reviewed and approved. An address absent from this set is never
    /// contacted, even when a fetcher is supplied: approval is of addresses, not of the operation.
    /// </param>
    public async Task<ContactPhotoImportResult> AttachAsync(
        VCardImportPreview preview,
        IContactPhotoFetcher? fetcher = null,
        IReadOnlySet<string>? approvedUrls = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        VCardContactPreview[] withPhotos = preview.Contacts.Where(contact => contact.Photos.Count > 0).ToArray();
        if (withPhotos.Length == 0) return new(0, 0, 0, []);
        IReadOnlyDictionary<string, Guid> imported = await store
            .MapImportedRecordsAsync([.. withPhotos.Select(contact => contact.Card.Fingerprint)], cancellationToken)
            .ConfigureAwait(false);

        List<ContactPhotoImportOutcome> outcomes = [];
        int attached = 0, skipped = 0, failed = 0;

        foreach (VCardContactPreview contact in withPhotos)
        {
            // A contact the operator chose to skip has no record, so it simply has no photo.
            if (!imported.TryGetValue(contact.Card.Fingerprint, out Guid recordId)) continue;
            ContactPhotoCandidate photo = contact.Photos[0];

            // A contact that already has an image keeps it. Re-importing the same export must not
            // add the same face again, and a merge must not overwrite a photo chosen deliberately.
            RecordDetails? existing = await records.GetRecordAsync(recordId, cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.Images.Count > 0)
            {
                skipped++;
                outcomes.Add(new(contact.Index, recordId, false, photo.IsRemote, "the record already has an image"));
                continue;
            }

            ContactPhotoBytes? bytes;
            if (photo.InlineBase64 is string inline)
            {
                bytes = Decode(inline);
                if (bytes is null)
                {
                    failed++;
                    outcomes.Add(new(contact.Index, recordId, false, false, "the photo in the card was not valid data"));
                    continue;
                }
            }
            else if (photo.RemoteUrl is string url)
            {
                if (fetcher is null)
                {
                    skipped++;
                    outcomes.Add(new(contact.Index, recordId, false, true, "the photo is held elsewhere and fetching was not enabled"));
                    continue;
                }

                if (approvedUrls is null || !approvedUrls.Contains(url))
                {
                    skipped++;
                    outcomes.Add(new(contact.Index, recordId, false, true, "that address was not among the ones approved for fetching"));
                    continue;
                }

                bytes = await fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    failed++;
                    outcomes.Add(new(contact.Index, recordId, false, true, "the photo could not be fetched"));
                    continue;
                }
            }
            else
            {
                continue;
            }

            try
            {
                using MemoryStream content = new(bytes.Content, writable: false);
                _ = await images.AddAsync(recordId, content, bytes.FileName, cancellationToken).ConfigureAwait(false);
                attached++;
                outcomes.Add(new(contact.Index, recordId, true, photo.IsRemote, null));
            }
            catch (DomainValidationException exception)
            {
                // The shared image pipeline still decides what is an acceptable image. A photo it
                // refuses is reported against its contact rather than failing the whole import.
                failed++;
                outcomes.Add(new(contact.Index, recordId, false, photo.IsRemote, exception.Message));
            }
        }

        return new(attached, skipped, failed, outcomes);
    }

    private static ContactPhotoBytes? Decode(string base64)
    {
        try
        {
            byte[] content = Convert.FromBase64String(base64.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal));
            return content.Length is 0 or > ContactPhotoLimits.MaximumBytes ? null : new(content, "contact-photo");
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
