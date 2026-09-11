using System.Net;
using Monkeysphere.Core;

namespace Monkeysphere.Web;

/// <summary>
/// The only outbound request Monkeysphere ever makes, and only when an operator ticks the box on a
/// contact import. Everything about it is bounded: the address must be absolute http or https, the
/// response must declare an image type, the body is read to a hard ceiling, redirects are followed
/// only to http or https, and the whole attempt is abandoned after a short timeout.
///
/// It never throws for an ordinary failure. A photo that cannot be had is one contact without a
/// picture, not a lost import, and the caller reports it per contact.
/// </summary>
public sealed partial class ContactPhotoFetcher(HttpClient client, ILogger<ContactPhotoFetcher> logger) : IContactPhotoFetcher
{
    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = TimeSpan.FromSeconds(ContactPhotoLimits.TimeoutSeconds);
        client.MaxResponseContentBufferSize = ContactPhotoLimits.MaximumBytes;
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        // Deliberately not identifying the deployment beyond the product name.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Monkeysphere");
    }

    public async Task<ContactPhotoBytes?> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                PhotoUnavailable(logger, address.Host, (int)response.StatusCode);
                return null;
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                PhotoNotAnImage(logger, address.Host, mediaType ?? "none");
                return null;
            }

            if (response.Content.Headers.ContentLength is long declared && declared > ContactPhotoLimits.MaximumBytes)
            {
                PhotoTooLarge(logger, address.Host);
                return null;
            }

            byte[]? content = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                PhotoTooLarge(logger, address.Host);
                return null;
            }

            // The name is display metadata only and never becomes a path; the image pipeline
            // generates its own opaque storage name and decodes the bytes before accepting them.
            return new(content, "contact-photo" + Extension(mediaType));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            PhotoFetchFailed(logger, address.Host);
            return null;
        }
    }

    /// <summary>Reads at most the ceiling, so a server that lies about or omits its length cannot exhaust memory.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > ContactPhotoLimits.MaximumBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private static string Extension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => string.Empty,
    };

    // Only the host is logged, never the full address: a contact photo URL identifies the contact.
    [LoggerMessage(1, LogLevel.Information, "A contact photo at {Host} was unavailable ({StatusCode}).")]
    private static partial void PhotoUnavailable(ILogger logger, string host, int statusCode);

    [LoggerMessage(2, LogLevel.Information, "A contact photo at {Host} was not an image ({MediaType}).")]
    private static partial void PhotoNotAnImage(ILogger logger, string host, string mediaType);

    [LoggerMessage(3, LogLevel.Information, "A contact photo at {Host} exceeded the size limit and was not read.")]
    private static partial void PhotoTooLarge(ILogger logger, string host);

    [LoggerMessage(4, LogLevel.Information, "A contact photo at {Host} could not be fetched.")]
    private static partial void PhotoFetchFailed(ILogger logger, string host);
}
