using System.Text.Json;

namespace Gm8Builder.Lint;

/// <summary>One request to `gm8-builder lint --serve`: a JSON object on one line.</summary>
public sealed class LintRequest
{
    /// <summary>Echoed back unchanged, so a client can match replies to requests.</summary>
    public JsonElement? Id { get; set; }
    public string? Code { get; set; }
    public bool Xml { get; set; }
    public string? Name { get; set; }
    public List<string>? Trees { get; set; }
    public List<string>? Extensions { get; set; }
    public string? Gm8 { get; set; }
    public bool? Arity { get; set; }
    public bool? Style { get; set; }
}

/// <summary>
/// The reply, one line per request. `errors` repeats the error-severity entries
/// of `findings` for the common "did it pass" question; `note` says linting could
/// not run at all (which is `ok`, since a missing install must never block work);
/// `error` says the request itself was malformed.
/// </summary>
public sealed class LintResponse
{
    public JsonElement? Id { get; set; }
    public bool Ok { get; set; }
    public List<Finding> Findings { get; set; } = [];
    public List<Finding> Errors { get; set; } = [];
    public string? Note { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A long-running linter for editors and tools that lint on every keystroke or
/// call. The context (fnames and every tree's symbols) is built once and reused,
/// so a request costs the check itself and a stat per resource directory - no
/// process launch, no tree walk.
///
/// Requests are answered strictly in order. End of input ends the server.
/// </summary>
public static class LintServer
{
    public static void Run(TextReader input, TextWriter output, LintEnvironment env, LintOptions defaults)
    {
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = Answer(line, env, defaults);
            output.WriteLine(JsonSerializer.Serialize(response, LintJson.Relaxed.LintResponse));
            output.Flush();
        }
    }

    internal static LintResponse Answer(string line, LintEnvironment env, LintOptions defaults)
    {
        LintRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(line, LintJson.Default.LintRequest);
        }
        catch (JsonException e)
        {
            return new LintResponse { Error = "not a JSON request: " + e.Message };
        }
        if (req?.Code == null) return new LintResponse { Id = req?.Id, Error = "\"code\" is required" };

        var options = defaults with
        {
            Trees = req.Trees ?? defaults.Trees,
            Extensions = req.Extensions ?? defaults.Extensions,
            Gm8Dir = req.Gm8 ?? defaults.Gm8Dir,
            Arity = req.Arity ?? defaults.Arity,
            Style = req.Style ?? defaults.Style,
        };
        var result = env.Check(req.Code, options, req.Xml, req.Name ?? "<gml>");
        return new LintResponse
        {
            Id = req.Id,
            Ok = result.Ok,
            Findings = [.. result.Findings],
            Errors = [.. result.Errors],
            Note = result.Note,
        };
    }
}

/// <summary>
/// The server's JSON, source-generated so it works under Native AOT. camelCase;
/// relaxed escaping keeps code readable instead of escaping every byte above 127.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LintRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LintResponse))]
public partial class LintJson : System.Text.Json.Serialization.JsonSerializerContext
{
    private static LintJson? _relaxed;

    public static LintJson Relaxed => _relaxed ??= new LintJson(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
