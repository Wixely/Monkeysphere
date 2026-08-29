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
                timeProvider.GetUtcNow(),
                IsCover: record.Images.Count == 0,
                Correction: new ImageCorrection());
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

    public async Task<RecordImage> UpdateMetadataAsync(
        Guid recordId,
        Guid imageId,
        string? caption,
        bool isCover,
        CancellationToken cancellationToken = default)
    {
        RecordImage image = await RequireImageAsync(recordId, imageId, cancellationToken).ConfigureAwait(false);
        string? normalizedCaption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (normalizedCaption?.Length > 500)
        {
            throw new DomainValidationException("Image captions cannot exceed 500 characters.");
        }

        return await store.UpdateRecordImageAsync(
            recordId,
            imageId,
            normalizedCaption,
            isCover,
            image.Correction ?? new ImageCorrection(),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderAsync(
        Guid recordId,
        IReadOnlyList<Guid> imageIds,
        CancellationToken cancellationToken = default)
    {
        if (imageIds.Count > IRecordImageService.MaximumImagesPerRecord || imageIds.Distinct().Count() != imageIds.Count)
        {
            throw new DomainValidationException("Image order contains an invalid or duplicate image.");
        }

        await store.ReorderRecordImagesAsync(
            recordId,
            imageIds,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordImage> CorrectAsync(
        Guid recordId,
        Guid imageId,
        ImageCorrection correction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correction);
        RecordImage image = await RequireImageAsync(recordId, imageId, cancellationToken).ConfigureAwait(false);
        if (correction.RotationQuarterTurns is < 0 or > 3)
        {
            throw new DomainValidationException("Image rotation must be 0, 90, 180, or 270 degrees.");
        }

        bool hasAnyCrop = correction.CropX.HasValue || correction.CropY.HasValue ||
            correction.CropWidth.HasValue || correction.CropHeight.HasValue;
        bool hasEveryCrop = correction.CropX.HasValue && correction.CropY.HasValue &&
            correction.CropWidth.HasValue && correction.CropHeight.HasValue;
        if (hasAnyCrop != hasEveryCrop)
        {
            throw new DomainValidationException("A crop requires X, Y, width, and height together.");
        }

        string originalPath = RecordImageStoragePaths.OriginalPath(
            paths,
            recordId,
            imageId,
            ExtensionForContentType(image.OriginalContentType));
        byte[] encoded = await File.ReadAllBytesAsync(originalPath, cancellationToken).ConfigureAwait(false);
        using SKBitmap original = SKBitmap.Decode(encoded)
            ?? throw new DomainValidationException("The retained original image could not be decoded.");
        ValidateCrop(correction, original.Width, original.Height);
        using SKBitmap corrected = ApplyCorrection(original, correction);
        string previewPath = RecordImageStoragePaths.PreviewPath(paths, recordId, imageId);
        string thumbnailPath = RecordImageStoragePaths.ThumbnailPath(paths, recordId, imageId);
        string previewTemporary = previewPath + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        string thumbnailTemporary = thumbnailPath + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        try
        {
            await WriteWebpAsync(corrected, previewTemporary, PreviewDimension, 88, cancellationToken).ConfigureAwait(false);
            await WriteWebpAsync(corrected, thumbnailTemporary, ThumbnailDimension, 82, cancellationToken).ConfigureAwait(false);
            RecordImage updated = await store.UpdateRecordImageAsync(
                recordId,
                imageId,
                image.Caption,
                false,
                correction,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(previewTemporary, previewPath, true);
                File.Move(thumbnailTemporary, thumbnailPath, true);
            }
            catch
            {
                await store.UpdateRecordImageAsync(
                    recordId,
                    imageId,
                    image.Caption,
                    false,
                    image.Correction ?? new ImageCorrection(),
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                throw;
            }

            return updated;
        }
        finally
        {
            DeleteIfExists(previewTemporary);
            DeleteIfExists(thumbnailTemporary);
        }
    }

    public async Task<RecordImageFile?> OpenAsync(
        Guid recordId,
        Guid imageId,
        RecordImageVariant variant,
        CancellationToken cancellationToken = default)
    {
        RecordImage? image = (await store.ListRecordImagesAsync(recordId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(image => image.Id == imageId);
        if (image is null)
        {
            return null;
        }

        string path = variant switch
        {
            RecordImageVariant.Preview => RecordImageStoragePaths.PreviewPath(paths, recordId, imageId),
            RecordImageVariant.Thumbnail => RecordImageStoragePaths.ThumbnailPath(paths, recordId, imageId),
            RecordImageVariant.Original => RecordImageStoragePaths.OriginalPath(
                paths,
                recordId,
                imageId,
                ExtensionForContentType(image.OriginalContentType)),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        if (variant is RecordImageVariant.Preview or RecordImageVariant.Thumbnail && !File.Exists(path))
        {
            await RegenerateDerivativesAsync(image, cancellationToken).ConfigureAwait(false);
        }

        return File.Exists(path)
            ? new RecordImageFile(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan),
                variant == RecordImageVariant.Original ? image.OriginalContentType : "image/webp",
                variant == RecordImageVariant.Original ? image.OriginalFileName : null)
            : null;
    }

    private async Task RegenerateDerivativesAsync(RecordImage image, CancellationToken cancellationToken)
    {
        string originalPath = RecordImageStoragePaths.OriginalPath(
            paths,
            image.RecordId,
            image.Id,
            ExtensionForContentType(image.OriginalContentType));
        byte[] encoded = await File.ReadAllBytesAsync(originalPath, cancellationToken).ConfigureAwait(false);
        using SKBitmap original = SKBitmap.Decode(encoded)
            ?? throw new InvalidDataException("The retained original image could not be decoded.");
        ImageCorrection correction = image.Correction ?? new ImageCorrection();
        ValidateCrop(correction, original.Width, original.Height);
        using SKBitmap corrected = ApplyCorrection(original, correction);
        string previewPath = RecordImageStoragePaths.PreviewPath(paths, image.RecordId, image.Id);
        string thumbnailPath = RecordImageStoragePaths.ThumbnailPath(paths, image.RecordId, image.Id);
        string suffix = "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        string previewTemporary = previewPath + suffix;
        string thumbnailTemporary = thumbnailPath + suffix;
        try
        {
            await WriteWebpAsync(corrected, previewTemporary, PreviewDimension, 88, cancellationToken).ConfigureAwait(false);
            await WriteWebpAsync(corrected, thumbnailTemporary, ThumbnailDimension, 82, cancellationToken).ConfigureAwait(false);
            File.Move(previewTemporary, previewPath, true);
            File.Move(thumbnailTemporary, thumbnailPath, true);
        }
        finally
        {
            DeleteIfExists(previewTemporary);
            DeleteIfExists(thumbnailTemporary);
        }
    }

    private async Task<RecordImage> RequireImageAsync(
        Guid recordId,
        Guid imageId,
        CancellationToken cancellationToken) =>
        (await store.ListRecordImagesAsync(recordId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(image => image.Id == imageId)
        ?? throw new DomainValidationException("Image was not found.");

    private static void ValidateCrop(ImageCorrection correction, int width, int height)
    {
        if (correction.CropX is not int x)
        {
            return;
        }

        int y = correction.CropY!.Value;
        int cropWidth = correction.CropWidth!.Value;
        int cropHeight = correction.CropHeight!.Value;
        if (x < 0 || y < 0 || cropWidth <= 0 || cropHeight <= 0 ||
            x + cropWidth > width || y + cropHeight > height)
        {
            throw new DomainValidationException($"Crop bounds must fit within the {width} × {height} original image.");
        }
    }

    private static SKBitmap ApplyCorrection(SKBitmap source, ImageCorrection correction)
    {
        SKRect sourceRect = correction.CropX is int x
            ? new SKRect(x, correction.CropY!.Value, x + correction.CropWidth!.Value, correction.CropY.Value + correction.CropHeight!.Value)
            : new SKRect(0, 0, source.Width, source.Height);
        int croppedWidth = (int)sourceRect.Width;
        int croppedHeight = (int)sourceRect.Height;
        using SKBitmap cropped = new(croppedWidth, croppedHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (SKCanvas cropCanvas = new(cropped))
        {
            cropCanvas.Clear(SKColors.Transparent);
            using SKPaint paint = new() { IsAntialias = true };
            cropCanvas.DrawBitmap(
                source,
                sourceRect,
                new SKRect(0, 0, croppedWidth, croppedHeight),
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                paint);
        }

        bool swapsDimensions = correction.RotationQuarterTurns is 1 or 3;
        SKBitmap result = new(
            swapsDimensions ? croppedHeight : croppedWidth,
            swapsDimensions ? croppedWidth : croppedHeight,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using SKCanvas canvas = new(result);
        canvas.Clear(SKColors.Transparent);
        switch (correction.RotationQuarterTurns)
        {
            case 0:
                break;
            case 1:
                canvas.Translate(croppedHeight, 0);
                canvas.RotateDegrees(90);
                break;
            case 2:
                canvas.Translate(croppedWidth, croppedHeight);
                canvas.RotateDegrees(180);
                break;
            case 3:
                canvas.Translate(0, croppedWidth);
                canvas.RotateDegrees(270);
                break;
        }

        using SKPaint rotationPaint = new() { IsAntialias = true };
        canvas.DrawBitmap(
            cropped,
            new SKRect(0, 0, croppedWidth, croppedHeight),
            new SKRect(0, 0, croppedWidth, croppedHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            rotationPaint);
        return result;
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
