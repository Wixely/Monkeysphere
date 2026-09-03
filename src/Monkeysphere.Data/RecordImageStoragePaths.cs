using DnaX.Hosting;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

internal static class RecordImageStoragePaths
{
    internal static string RecordDirectory(IDnaXPaths paths, ICurrentDomain domain, Guid recordId) =>
        RecordDirectory(paths, domain.Id, recordId);

    internal static string RecordDirectory(IDnaXPaths paths, Guid domainId, Guid recordId) =>
        paths.ResolveWritable(Path.Combine(DomainStoragePaths.MediaRelativeRoot(domainId), recordId.ToString("N")));

    internal static string OriginalPath(IDnaXPaths paths, ICurrentDomain domain, Guid recordId, Guid imageId, string extension) =>
        OriginalPath(paths, domain.Id, recordId, imageId, extension);

    internal static string OriginalPath(IDnaXPaths paths, Guid domainId, Guid recordId, Guid imageId, string extension) =>
        Path.Combine(RecordDirectory(paths, domainId, recordId), imageId.ToString("N") + ".original" + extension);

    internal static string PreviewPath(IDnaXPaths paths, ICurrentDomain domain, Guid recordId, Guid imageId) =>
        Path.Combine(RecordDirectory(paths, domain, recordId), imageId.ToString("N") + ".preview.webp");

    internal static string ThumbnailPath(IDnaXPaths paths, ICurrentDomain domain, Guid recordId, Guid imageId) =>
        Path.Combine(RecordDirectory(paths, domain, recordId), imageId.ToString("N") + ".thumbnail.webp");

    internal static void DeleteRecordDirectory(IDnaXPaths paths, ICurrentDomain domain, Guid recordId)
    {
        string directory = RecordDirectory(paths, domain, recordId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
