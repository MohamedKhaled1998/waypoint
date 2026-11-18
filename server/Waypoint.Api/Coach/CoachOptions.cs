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
    public int MaxTokens { get; set; } = 2048;
}
