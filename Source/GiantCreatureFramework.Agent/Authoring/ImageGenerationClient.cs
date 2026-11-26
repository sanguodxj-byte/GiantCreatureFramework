using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GiantCreatureFramework.Agent.Imaging;

namespace GiantCreatureFramework.Agent.Authoring;

public sealed record ImageEndpointConfig(
    string BaseUrl,
    string ApiKey,
    string Model,
    int TimeoutSeconds = 300);

public sealed record ImageReferenceFile(string FileName, byte[] Bytes);

public sealed record ImageTokenUsage(
    int InputTokens,
    int OutputTokens,
    int ImageOutputTokens,
    int TotalTokens);

public sealed record GeneratedImagePayload(
    byte[] PngBytes,
    int Width,
    int Height,
    string UsedEndpoint,
    bool UsedEdits,
    ImageTokenUsage? Usage = null);

/// <summary>
/// Step 4: OpenAI-compatible image generation client supporting both
/// POST .../images/generations (text-to-image JSON) and POST .../images/edits
/// (multipart/form-data when reference images are supplied).
/// Invariants:
/// - With reference images -> ALWAYS calls /images/edits;
/// - Never sends any mask field;
/// - Never sends any output length limit / max_tokens parameter.
/// </summary>
public sealed class ImageGenerationClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpMessageHandler? _customHandler;
    private readonly Func<byte[], Task<RgbaImage>>? _nonPngDecoder;

    public ImageGenerationClient(HttpMessageHandler? customHandler = null, Func<byte[], Task<RgbaImage>>? nonPngDecoder = null)
    {
        _customHandler = customHandler;
        _nonPngDecoder = nonPngDecoder;
    }

    public static (string GenerationsUrl, string EditsUrl) ResolveImageUrls(string? rawEndpoint)
    {
        var url = (rawEndpoint ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
        {
            return (string.Empty, string.Empty);
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        foreach (var suffix in new[] { "/images/generations", "/images/edits", "/chat/completions" })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^suffix.Length];
                break;
            }
        }

        if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("/v1/", StringComparison.OrdinalIgnoreCase) &&
            !url.EndsWith("/v4", StringComparison.OrdinalIgnoreCase))
        {
            url += "/v1";
        }

        return (url + "/images/generations", url + "/images/edits");
    }

    public async Task<GeneratedImagePayload> GenerateAsync(
        ImageEndpointConfig config,
        string prompt,
        string size = "1024x1024",
        IReadOnlyList<ImageReferenceFile>? references = null,
        CancellationToken cancellationToken = default)
    {
        var (genUrl, editsUrl) = ResolveImageUrls(config.BaseUrl);
        if (string.IsNullOrWhiteSpace(genUrl) || string.IsNullOrWhiteSpace(config.Model))
        {
            throw new InvalidOperationException(AgentProtocol.Get("Authoring_ImageEndpointMissing"));
        }

        var useEdits = references != null && references.Count > 0;
        var targetUrl = useEdits ? editsUrl : genUrl;
        var effectiveSize = string.IsNullOrWhiteSpace(size) ? "1024x1024" : size.Trim();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 15, 900)));

        using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
        }

        if (useEdits)
        {
            var form = new MultipartFormDataContent("----gcfimg" + Guid.NewGuid().ToString("N"));
            form.Add(new StringContent(config.Model.Trim(), Encoding.UTF8), "model");
            form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
            form.Add(new StringContent("1", Encoding.UTF8), "n");
            form.Add(new StringContent(effectiveSize, Encoding.UTF8), "size");

            var fieldName = references!.Count == 1 ? "image" : "image[]";
            foreach (var refFile in references)
            {
                var part = new ByteArrayContent(refFile.Bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(IsPng(refFile.Bytes) ? "image/png" : "image/jpeg");
                form.Add(part, fieldName, Path.GetFileName(refFile.FileName));
            }
            request.Content = form;
        }
        else
        {
            var jsonPayload = new JsonObject
            {
                ["model"] = config.Model.Trim(),
                ["prompt"] = prompt,
                ["n"] = 1,
                ["size"] = effectiveSize
            };
            request.Content = new StringContent(jsonPayload.ToJsonString(Evidence.UnescapedJsonOptions), Encoding.UTF8, "application/json");
        }

        var client = _customHandler != null ? new HttpClient(_customHandler, disposeHandler: false) : SharedHttp;
        try
        {
            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = responseText.Length > 320 ? responseText[..320] + "..." : responseText;
                throw new HttpRequestException(AgentProtocol.Format("Authoring_ImageHttpError", (int)response.StatusCode, snippet));
            }

            var (rawImageBytes, usage) = await ExtractImageBytesAndUsageAsync(client, responseText, timeoutCts.Token).ConfigureAwait(false);
            RgbaImage decoded;
            if (IsPng(rawImageBytes))
            {
                decoded = PngCodec.Decode(rawImageBytes);
            }
            else if (_nonPngDecoder != null)
            {
                decoded = await _nonPngDecoder(rawImageBytes).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidDataException("Image endpoint returned non-PNG bytes and no fallback decoder is available.");
            }

            var normalizedPng = PngCodec.Encode(decoded);
            return new GeneratedImagePayload(normalizedPng, decoded.Width, decoded.Height, targetUrl, useEdits, usage);
        }
        finally
        {
            if (_customHandler != null) client.Dispose();
        }
    }

    private static async Task<(byte[] Bytes, ImageTokenUsage? Usage)> ExtractImageBytesAndUsageAsync(HttpClient client, string jsonBody, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(jsonBody);
        var root = doc.RootElement;
        ImageTokenUsage? usage = null;
        if (root.TryGetProperty("usage", out var uEl) && uEl.ValueKind == JsonValueKind.Object)
        {
            var inTok = uEl.TryGetProperty("input_tokens", out var p1) && p1.TryGetInt32(out var v1) ? v1
                : (uEl.TryGetProperty("prompt_tokens", out var p2) && p2.TryGetInt32(out var v2) ? v2 : 0);
            var outTok = uEl.TryGetProperty("output_tokens", out var c1) && c1.TryGetInt32(out var vc1) ? vc1
                : (uEl.TryGetProperty("completion_tokens", out var c2) && c2.TryGetInt32(out var vc2) ? vc2 : 0);
            var totTok = uEl.TryGetProperty("total_tokens", out var t1) && t1.TryGetInt32(out var vt1) ? vt1 : (inTok + outTok);
            var imgOutTok = 0;
            if (uEl.TryGetProperty("output_tokens_details", out var od) && od.ValueKind == JsonValueKind.Object &&
                od.TryGetProperty("image_tokens", out var imgEl) && imgEl.TryGetInt32(out var vi))
            {
                imgOutTok = vi;
            }
            if (totTok > 0 || inTok > 0 || outTok > 0 || imgOutTok > 0)
            {
                usage = new ImageTokenUsage(inTok, outTok, imgOutTok, totTok);
            }
        }
        if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array && dataArr.GetArrayLength() > 0)
        {
            var first = dataArr[0];
            if (first.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    return (Convert.FromBase64String(StripDataUrlPrefix(b64)), usage);
                }
            }
            if (first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var imgUrl = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(imgUrl))
                {
                    var bytes = await client.GetByteArrayAsync(imgUrl, token).ConfigureAwait(false);
                    return (bytes, usage);
                }
            }
        }
        throw new InvalidDataException(AgentProtocol.Get("Authoring_ImageEmptyData"));
    }

    private static string StripDataUrlPrefix(string raw)
    {
        var comma = raw.IndexOf(',');
        return raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 ? raw[(comma + 1)..] : raw;
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 &&
        bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
        bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;
}
