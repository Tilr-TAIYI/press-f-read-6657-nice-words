using System.Net.Http.Json;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Diagnostics;
using System.Text;

namespace Sb6657Cs2Assistant;

public sealed class MemeApiClient
{
    private const string OfficialHost = "hguofichp.cn";
    private const string OfficialCertificateSha256 = "FED3C87C9C12351F325CC116E246A293ADC10ED54968C82CD90053D1511A26FB";
    private readonly HttpClient _http;
    private string _baseUrl;
    private int _timeoutSeconds;

    public MemeApiClient(string baseUrl, int timeoutSeconds)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 60);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sb6657Cs2Assistant/1.0");
    }

    public void Configure(string baseUrl, int timeoutSeconds)
    {
        _baseUrl = baseUrl.TrimEnd('/');
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        try
        {
            return await _http.GetFromJsonAsync<T>(url, timeout.Token);
        }
        catch (HttpRequestException windowsTlsError)
        {
            try
            {
                var json = await GetWithPythonAsync(url, timeout.Token);
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception fallbackError)
            {
                throw new HttpRequestException(
                    $"Windows TLS 失败，Python/OpenSSL 回退也失败。TLS: {Innermost(windowsTlsError)}；回退: {Innermost(fallbackError)}",
                    fallbackError);
            }
        }
    }

    private async Task<string> GetWithPythonAsync(string url, CancellationToken token)
    {
        const string script = "import sys,urllib.request;print(urllib.request.urlopen(sys.argv[1],timeout=float(sys.argv[2])).read().decode('utf-8'))";
        var start = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(url);
        start.ArgumentList.Add(_timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Python");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new HttpRequestException(string.IsNullOrWhiteSpace(error) ? $"Python 退出代码 {process.ExitCode}" : error.Trim());
            if (string.IsNullOrWhiteSpace(output)) throw new HttpRequestException("Python 未返回数据");
            return output;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }

    private static string Innermost(Exception exception)
    {
        while (exception.InnerException is not null) exception = exception.InnerException;
        return exception.Message;
    }

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
