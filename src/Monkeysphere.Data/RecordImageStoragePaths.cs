using DnaX.Hosting;

namespace Monkeysphere.Data;

internal static class RecordImageStoragePaths
{
    internal static string RecordDirectory(IDnaXPaths paths, Guid recordId) =>
        paths.ResolveWritable(Path.Combine("media", "records", recordId.ToString("N")));

    internal static string OriginalPath(IDnaXPaths paths, Guid recordId, Guid imageId, string extension) =>
        Path.Combine(RecordDirectory(paths, recordId), imageId.ToString("N") + ".original" + extension);

    internal static string PreviewPath(IDnaXPaths paths, Guid recordId, Guid imageId) =>
        Path.Combine(RecordDirectory(paths, recordId), imageId.ToString("N") + ".preview.webp");

    internal static string ThumbnailPath(IDnaXPaths paths, Guid recordId, Guid imageId) =>
        Path.Combine(RecordDirectory(paths, recordId), imageId.ToString("N") + ".thumbnail.webp");

    internal static void DeleteRecordDirectory(IDnaXPaths paths, Guid recordId)
    {
        string directory = RecordDirectory(paths, recordId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
