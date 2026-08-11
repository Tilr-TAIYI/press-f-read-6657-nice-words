using System.Net.Http.Json;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.IO;

namespace Sb6657Cs2Assistant;

public sealed class MemeApiClient : IDisposable
{
    private const long MaxResponseBytes = 2 * 1024 * 1024;
    private const string OfficialHost = "hguofichp.cn";
    private const string OfficialCertificateSha256 = "FED3C87C9C12351F325CC116E246A293ADC10ED54968C82CD90053D1511A26FB";
    public const string DefaultApiBaseUrl = "https://hguofichp.cn:10086";
    private readonly HttpClient _http;
    private string _baseUrl;
    private int _timeoutSeconds;

    public MemeApiClient(string baseUrl, int timeoutSeconds)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 60);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sb6657Cs2Assistant/1.0");
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public void Configure(string baseUrl, int timeoutSeconds)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 60);
    }

    public async Task<IReadOnlyList<MemeTag>> GetTagsAsync(CancellationToken token)
    {
        var envelope = await GetAsync<ApiEnvelope<List<TagDto>>>($"{_baseUrl}/machine/dictList", token);
        EnsureSuccess(envelope);
        return envelope!.Data!
            .Where(x => !string.IsNullOrWhiteSpace(x.Value) && !string.IsNullOrWhiteSpace(x.Label))
            .Select(x => new MemeTag(x.Value!, x.Label!, x.IconUrl))
            .ToList();
    }

    public async Task<Meme?> GetRandomAsync(CancellationToken token)
    {
        var envelope = await GetAsync<ApiEnvelope<MemeDto>>($"{_baseUrl}/machine/getRandOne", token);
        EnsureSuccess(envelope);
        return Map(envelope!.Data);
    }

    public async Task<(int Total, Meme? Meme)> GetFilteredPageAsync(string tags, int page, CancellationToken token)
    {
        var url = $"{_baseUrl}/machine/Page?tags={Uri.EscapeDataString(tags)}&pageNum={page}&pageSize=1";
        var envelope = await GetAsync<ApiEnvelope<PageDto>>(url, token);
        EnsureSuccess(envelope);
        return (envelope!.Data!.Total, Map(envelope.Data.List.FirstOrDefault()));
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken token)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    throw new HttpRequestException("接口响应过大，已拒绝读取");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = await ReadLimitedAsync(stream, timeout.Token);
                return JsonSerializer.Deserialize<T>(bytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
            {
                lastError = ex;
                if (ex is HttpRequestException && IsTlsFailure(ex)) break;
                if (attempt < 3) await Task.Delay(300 * attempt, token);
            }
        }

        throw new HttpRequestException(
            $"接口重试 3 次仍失败：{Innermost(lastError ?? new HttpRequestException("未知网络错误"))}",
            lastError);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new HttpRequestException("接口响应过大，已拒绝读取");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Innermost(Exception exception)
    {
        while (exception.InnerException is not null) exception = exception.InnerException;
        return exception.Message;
    }

    private static bool IsTlsFailure(Exception exception)
    {
        var text = exception.ToString();
        return text.Contains("SEC_E_", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Schannel", StringComparison.OrdinalIgnoreCase)
            || text.Contains("安全包中没有可用的凭证", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TLS", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 10086 ||
            uri.AbsolutePath != "/" ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 ||
            uri.UserInfo.Length > 0)
            return DefaultApiBaseUrl;
        return uri.ToString().TrimEnd('/');
    }

    public void Dispose() => _http.Dispose();

    private static bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || request.RequestUri?.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase) != true)
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
            return false;
        return certificate.GetCertHashString(HashAlgorithmName.SHA256)
            .Equals(OfficialCertificateSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static Meme? Map(MemeDto? dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Barrage)) return null;
        var id = ElementText(dto.Id) ?? ElementText(dto.BarrageId) ?? dto.Barrage.GetHashCode().ToString();
        return new Meme(id, dto.Barrage, dto.Tags ?? "");
    }

    private static string? ElementText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    private static void EnsureSuccess<T>(ApiEnvelope<T>? response)
    {
        if (response is null || response.Code != 200 || response.Data is null)
            throw new HttpRequestException(response?.Message ?? "接口返回无效数据");
    }
}
