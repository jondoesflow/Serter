using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Serter.Core.Sources;

/// <summary>Small JSON GET helper with an in-memory + on-disk cache, so repeated loads are instant and API limits are respected.</summary>
public sealed class HttpJson
{
    private readonly HttpClient _http;
    private readonly string? _cacheDirectory;
    private readonly TimeSpan _cacheLifetime;
    private readonly ConcurrentDictionary<string, JsonNode?> _memory = new();
    private readonly SemaphoreSlim _throttle;

    public HttpJson(HttpClient http, string? cacheDirectory, TimeSpan cacheLifetime, int maxConcurrentRequests = 4)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _cacheLifetime = cacheLifetime;
        _throttle = new SemaphoreSlim(maxConcurrentRequests);
        if (_cacheDirectory is not null)
            Directory.CreateDirectory(_cacheDirectory);
    }

    /// <param name="cacheKey">Stable key without secrets (API keys are left out of cache file names).</param>
    public async Task<JsonNode?> GetAsync(string url, string cacheKey, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(cacheKey, out var cached))
            return cached;

        var file = CacheFile(cacheKey);
        if (file is not null && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < _cacheLifetime)
        {
            try
            {
                var node = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken));
                _memory[cacheKey] = node;
                return node;
            }
            catch (JsonException)
            {
                // Corrupt cache entry – fetch again.
            }
        }

        await _throttle.WaitAsync(cancellationToken);
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Remember(cacheKey, file, null);
            if (!response.IsSuccessStatusCode)
                return null; // Don't cache transient failures (rate limits, outages).

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var node = JsonNode.Parse(text);
            return Remember(cacheKey, file, node);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private JsonNode? Remember(string cacheKey, string? file, JsonNode? node)
    {
        _memory[cacheKey] = node;
        if (file is not null)
        {
            try
            {
                File.WriteAllText(file, node?.ToJsonString() ?? "null");
            }
            catch (IOException)
            {
                // Cache is best-effort.
            }
        }
        return node;
    }

    private string? CacheFile(string cacheKey)
    {
        if (_cacheDirectory is null)
            return null;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(cacheKey)));
        return Path.Combine(_cacheDirectory, hash + ".json");
    }
}

internal static class JsonNodeExtensions
{
    public static string? Str(this JsonNode? node, string property)
    {
        var value = node?[property];
        if (value is null)
            return null;
        if (value is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            return v.ToJsonString();
        }
        return null;
    }

    public static double? Num(this JsonNode? node, string property)
    {
        var value = node?[property];
        if (value is not JsonValue v)
            return null;
        if (v.TryGetValue<double>(out var d))
            return d;
        if (v.TryGetValue<long>(out var l))
            return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    public static IEnumerable<JsonNode> Items(this JsonNode? node)
    {
        return node switch
        {
            JsonArray array => array.Where(n => n is not null).Select(n => n!),
            JsonObject obj => [obj], // Some APIs return a lone object instead of a one-item array.
            _ => [],
        };
    }
}
