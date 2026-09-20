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
}
