using System.Text.Json.Serialization;
using System.Text.Json;

namespace Sb6657Cs2Assistant;

public sealed class AppSettings
{
    public string ApiBaseUrl { get; set; } = "https://hguofichp.cn:10086";
    public int IntervalSeconds { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 3;
    public string ChatKey { get; set; } = "Y";
    public string SendKey { get; set; } = "F8";
    public string ChatChannel { get; set; } = "All";
    public string SteamPath { get; set; } = "";
    public string Cs2Path { get; set; } = "";
    public string SteamUserId { get; set; } = "";
    public string BoundKey { get; set; } = "";
    public string? OriginalBindingCommand { get; set; }
    public bool OriginalBindingExisted { get; set; }
    public Dictionary<string, BindingSnapshot> OriginalBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AutoexecCreatedByTool { get; set; }
    public List<SendHistory> SendHistory { get; set; } = [];
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+F10";
    public string ChatPrefix { get; set; } = "";
    public int MaxMessageLength { get; set; } = 220;
    public bool StartEnabled { get; set; }
    public List<string> SelectedTagValues { get; set; } = [];
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
}

public sealed record MemeTag(string DictValue, string DictLabel, string? IconUrl)
{
    public bool IsSelected { get; set; }
}

public sealed record Meme(string Id, string Barrage, string Tags);

internal sealed class ApiEnvelope<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Message { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
}

internal sealed class MemeDto
{
    [JsonPropertyName("id")] public JsonElement Id { get; set; }
    [JsonPropertyName("barrageId")] public JsonElement BarrageId { get; set; }
    [JsonPropertyName("barrage")] public string? Barrage { get; set; }
    [JsonPropertyName("tags")] public string? Tags { get; set; }
}

internal sealed class TagDto
{
    [JsonPropertyName("dictValue")] public string? Value { get; set; }
    [JsonPropertyName("dictLabel")] public string? Label { get; set; }
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
}

internal sealed class PageDto
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("list")] public List<MemeDto> List { get; set; } = [];
}

public sealed class Counters
{
    public int Success { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Redrawn { get; set; }
}

public sealed record SendHistory(DateTime Time, string Text, string Channel, bool Triggered);
public sealed record BindingSnapshot(bool Existed, string? Command);
