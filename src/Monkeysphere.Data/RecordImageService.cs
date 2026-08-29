using DnaX.Hosting;
using Monkeysphere.Core;
using SkiaSharp;

namespace Monkeysphere.Data;

public sealed class RecordImageService(
    IMonkeysphereStore store,
    IDnaXPaths paths,
    TimeProvider timeProvider) : IRecordImageService
{
    private const int MaximumPixels = 24_000_000;
    private const int MaximumDimension = 12_000;
    private const int PreviewDimension = 4_096;
    private const int ThumbnailDimension = 640;

    public async Task<RecordImage> AddAsync(
        Guid recordId,
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        RecordDetails record = await store.GetRecordAsync(recordId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Record was not found.");
        if (record.Images.Count >= IRecordImageService.MaximumImagesPerRecord)
        {
            throw new DomainValidationException($"A record cannot have more than {IRecordImageService.MaximumImagesPerRecord} images.");
        }

        byte[] encoded = await ReadBoundedAsync(content, cancellationToken).ConfigureAwait(false);
        using SKMemoryStream inspectionStream = new(encoded);
        using SKCodec codec = SKCodec.Create(inspectionStream)
            ?? throw new DomainValidationException("The selected file is not a supported image.");
        (string contentType, string extension) = DescribeFormat(codec.EncodedFormat);
        SKImageInfo info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 ||
            info.Width > MaximumDimension || info.Height > MaximumDimension ||
            (long)info.Width * info.Height > MaximumPixels)
        {
            throw new DomainValidationException("The image dimensions are too large. Use an image no larger than 24 megapixels or 12,000 pixels on either side.");
        }

        using SKBitmap bitmap = SKBitmap.Decode(encoded)
            ?? throw new DomainValidationException("The selected image could not be decoded safely.");
        Guid imageId = Guid.CreateVersion7();
        string directory = Directory.CreateDirectory(RecordImageStoragePaths.RecordDirectory(paths, recordId)).FullName;
        string originalPath = RecordImageStoragePaths.OriginalPath(paths, recordId, imageId, extension);
        string previewPath = RecordImageStoragePaths.PreviewPath(paths, recordId, imageId);
        string thumbnailPath = RecordImageStoragePaths.ThumbnailPath(paths, recordId, imageId);
        try
        {
            await File.WriteAllBytesAsync(originalPath, encoded, cancellationToken).ConfigureAwait(false);
            await WriteWebpAsync(bitmap, previewPath, PreviewDimension, 88, cancellationToken).ConfigureAwait(false);
            await WriteWebpAsync(bitmap, thumbnailPath, ThumbnailDimension, 82, cancellationToken).ConfigureAwait(false);

            int ordinal = record.Images.Count == 0 ? 0 : record.Images.Max(image => image.Ordinal) + 1;
            RecordImage image = new(
                imageId,
                recordId,
                ordinal,
                NormalizeFileName(originalFileName),
                contentType,
                encoded.LongLength,
                info.Width,
                info.Height,
                timeProvider.GetUtcNow());
            return await store.AddRecordImageAsync(image, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteIfExists(originalPath);
            DeleteIfExists(previewPath);
            DeleteIfExists(thumbnailPath);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            throw;
        }
    }

    public async Task<bool> DeleteAsync(
        Guid recordId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        RecordImage? image = (await store.ListRecordImagesAsync(recordId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.Id == imageId);
        if (image is null || !await store.DeleteRecordImageAsync(
            recordId,
            imageId,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        DeleteIfExists(RecordImageStoragePaths.OriginalPath(paths, recordId, imageId, ExtensionForContentType(image.OriginalContentType)));
        DeleteIfExists(RecordImageStoragePaths.PreviewPath(paths, recordId, imageId));
        DeleteIfExists(RecordImageStoragePaths.ThumbnailPath(paths, recordId, imageId));
        return true;
    }

    public async Task<RecordImageFile?> OpenAsync(
        Guid recordId,
        Guid imageId,
        RecordImageVariant variant,
        CancellationToken cancellationToken = default)
    {
        bool exists = (await store.ListRecordImagesAsync(recordId, cancellationToken).ConfigureAwait(false))
            .Any(image => image.Id == imageId);
        if (!exists)
        {
            return null;
        }

        string path = variant switch
        {
            RecordImageVariant.Preview => RecordImageStoragePaths.PreviewPath(paths, recordId, imageId),
            RecordImageVariant.Thumbnail => RecordImageStoragePaths.ThumbnailPath(paths, recordId, imageId),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        return File.Exists(path)
            ? new RecordImageFile(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan),
                "image/webp")
            : null;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] block = new byte[64 * 1024];
        while (true)
        {
            int read = await content.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > IRecordImageService.MaximumUploadBytes)
            {
                throw new DomainValidationException("Images must be 10 MB or smaller.");
            }

            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            throw new DomainValidationException("The selected image is empty.");
        }

        return buffer.ToArray();
    }

    private static async Task WriteWebpAsync(
        SKBitmap source,
        string path,
        int maximumDimension,
        int quality,
        CancellationToken cancellationToken)
    {
        double scale = Math.Min(1d, maximumDimension / (double)Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new DomainValidationException("The image could not be prepared for display.");
        surface.Canvas.Clear(SKColors.Transparent);
        using SKPaint paint = new() { IsAntialias = true };
        surface.Canvas.DrawBitmap(
            source,
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKCubicResampler.Mitchell),
            paint);
        using SKImage normalized = surface.Snapshot();
        using SKData data = normalized.Encode(SKEncodedImageFormat.Webp, quality)
            ?? throw new DomainValidationException("The image could not be encoded safely.");
        await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        data.SaveTo(output);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string ContentType, string Extension) DescribeFormat(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => ("image/jpeg", ".jpg"),
        SKEncodedImageFormat.Png => ("image/png", ".png"),
        SKEncodedImageFormat.Webp => ("image/webp", ".webp"),
        _ => throw new DomainValidationException("Only JPEG, PNG, and WebP images are supported."),
    };

    private static string ExtensionForContentType(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new InvalidOperationException("Stored image content type is invalid."),
    };

    private static string NormalizeFileName(string originalFileName)
    {
        string name = Path.GetFileName(originalFileName.Replace('\\', '/')).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "image";
        }

        string safe = new(name.Where(character => !char.IsControl(character)).Take(255).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "image" : safe;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
