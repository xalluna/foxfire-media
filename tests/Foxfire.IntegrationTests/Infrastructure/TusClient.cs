using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Foxfire.Server.Uploads;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>Just enough of the tus 1.0 protocol to drive the upload endpoint from tests.</summary>
public sealed class TusClient(HttpClient http, string uploadToken)
{
    public static async Task<UploadTicket> MintAsync(HttpClient appClient, string title = "Clip", long sizeBytes = 3000, string? externalId = null, string filename = "clip.mp4")
    {
        var response = await appClient.PostAsJsonAsync("/api/v1/uploads", new { title, filename, sizeBytes, externalId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadTicket>())!;
    }

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
        return request;
    }

    public async Task<HttpResponseMessage> CreateAsync(long length)
    {
        using var request = Request(HttpMethod.Post, UploadGrants.TusPath);
        request.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        return await http.SendAsync(request);
    }

    public async Task<string> CreateOrThrowAsync(long length)
    {
        using var response = await CreateAsync(length);
        if (response.StatusCode != System.Net.HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"tus create failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        return response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location.AbsolutePath : response.Headers.Location.OriginalString;
    }

    public async Task<HttpResponseMessage> PatchAsync(string location, long offset, byte[] chunk)
    {
        using var request = Request(HttpMethod.Patch, location);
        request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(chunk);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        return await http.SendAsync(request);
    }

    public async Task<HttpResponseMessage> HeadAsync(string location)
    {
        using var request = Request(HttpMethod.Head, location);
        return await http.SendAsync(request);
    }

    public async Task<HttpResponseMessage> DeleteAsync(string location)
    {
        using var request = Request(HttpMethod.Delete, location);
        return await http.SendAsync(request);
    }

    public static long Offset(HttpResponseMessage response) =>
        long.Parse(response.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
}
