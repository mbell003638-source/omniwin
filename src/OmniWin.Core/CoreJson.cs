using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniWin.Core;

/// <summary>JSON settings shared by every tool: snake_case names, strict about missing required values.</summary>
public static class CoreJson
{
    /// <summary>For tool arguments and results.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>For files people may read or edit (config, workflows).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Files are edited by hand ("ctrl+alt+shift+k", not "ctrl+alt..."). Output is never embedded in HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            // Models don't always put "kind" first.
            AllowOutOfOrderMetadataProperties = true,
            // A missing required value is an error, never a silent null (e.g. element_gone without "ref").
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}
