namespace Waypoint.Api.Coach;

public sealed class ModelChoice
{
    public string Provider { get; set; } = "anthropic";
    public string Model { get; set; } = "";
}

/// <summary>Section <c>Coach</c>: <c>Models:{CoachCallKind}</c> picks provider and model per kind of call.</summary>
public sealed class CoachOptions
{
    public Dictionary<string, ModelChoice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ModelChoice For(CoachCallKind kind) =>
        Models.TryGetValue(kind.ToString(), out var choice) && choice.Model.Length > 0
            ? choice
            : throw new InvalidOperationException($"Coach:Models:{kind} is not configured.");
}

public sealed class AnthropicOptions
{
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    // Sonnet 5.5 thinks before it answers: a first Plan took ~9k thinking + ~3k text tokens.
    // A ceiling, not a target: only the tokens used are billed.
    public int MaxTokens { get; set; } = 32000;
}
