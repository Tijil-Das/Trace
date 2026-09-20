using System.Text.Json;

namespace ScreenRecall.Storage;

/// <summary>Shared JSON settings so config/meta files stay stable and hand-editable.</summary>
public static class RecallJson
{
    /// <summary>Serializer options used for every file Screen Recall writes.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Options for the named-pipe protocol. One JSON document per line is the framing, so this variant
    /// must stay un-indented: an indented payload would span lines and break any reader using ReadLine.
    /// </summary>
    public static JsonSerializerOptions WireOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
