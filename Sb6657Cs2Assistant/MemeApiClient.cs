using System.Net.Http.Json;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace Sb6657Cs2Assistant;

public sealed class MemeApiClient : IDisposable
{
    private const long MaxResponseBytes = 2 * 1024 * 1024;
    private const string OfficialHost = "hguofichp.cn";
    private const string OfficialCertificateSha256 = "FED3C87C9C12351F325CC116E246A293ADC10ED54968C82CD90053D1511A26FB";
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

        using var fallbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        fallbackTimeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        try
        {
            var json = await GetWithPythonAsync(url, fallbackTimeout.Token);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception fallbackError) when (!token.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"接口重试 3 次仍失败。Windows: {Innermost(lastError!)}；备用请求: {Innermost(fallbackError)}",
                fallbackError);
        }
    }

    private async Task<string> GetWithPythonAsync(string url, CancellationToken token)
    {
        const string script = "import sys,urllib.request;sys.stdout.reconfigure(encoding='utf-8');print(urllib.request.urlopen(sys.argv[1],timeout=float(sys.argv[2])).read().decode('utf-8'))";
        Process? process = null;
        Exception? startError = null;
        foreach (var candidate in PythonCandidates())
        {
            var probe = new ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            probe.Environment["PYTHONIOENCODING"] = "utf-8";
            probe.Environment["PYTHONUTF8"] = "1";
            if (Path.GetFileNameWithoutExtension(candidate).Equals("py", StringComparison.OrdinalIgnoreCase)) probe.ArgumentList.Add("-3");
            probe.ArgumentList.Add("-c");
            probe.ArgumentList.Add(script);
            probe.ArgumentList.Add(url);
            probe.ArgumentList.Add(_timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                process = Process.Start(probe);
                if (process is not null) break;
            }
            catch (Win32Exception ex) { startError = ex; }
        }
        if (process is null)
            throw new InvalidOperationException("未找到可用 Python；请确认 Python 已加入启动本程序用户的 PATH", startError);
        using (process)
        try
        {
            var outputTask = ReadLimitedTextAsync(process.StandardOutput, MaxResponseBytes, token);
            var errorTask = ReadLimitedTextAsync(process.StandardError, 128 * 1024, token);
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

    private static IEnumerable<string> PythonCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "python.exe", "py.exe", "python3.exe" })
            {
                var path = Path.Combine(directory.Trim(), name);
                if (File.Exists(path) && seen.Add(path)) yield return path;
            }
        }
        foreach (var name in new[] { "python", "py", "python3" })
            if (seen.Add(name)) yield return name;
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

    private static async Task<string> ReadLimitedTextAsync(StreamReader reader, long maxBytes, CancellationToken token)
    {
        var builder = new StringBuilder();
        var chunk = new char[8192];
        long bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(chunk.AsMemory(), token);
            if (read == 0) break;
            bytes += Encoding.UTF8.GetByteCount(chunk, 0, read);
            if (bytes > maxBytes) throw new HttpRequestException("备用接口响应过大，已拒绝读取");
            builder.Append(chunk, 0, read);
        }
        return builder.ToString();
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
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("接口地址格式无效");
        var localHttp = uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase));
        if (uri.UserInfo.Length > 0 || (uri.Scheme != Uri.UriSchemeHttps && !localHttp))
            throw new ArgumentException("接口地址必须是 HTTPS 地址（本机 localhost 可使用 HTTP）");
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
