using System.Text.Json.Serialization;

namespace CareerCopilot.Models;

public class EvaluationResult
{
    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("match")]
    public bool Match { get; set; }

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; set; } = new();

    [JsonPropertyName("concerns")]
    public List<string> Concerns { get; set; } = new();

    [JsonPropertyName("cv")]
    public CvDocument? Cv { get; set; }

    [JsonPropertyName("isLocal")]
    public bool IsLocal { get; set; }

    [JsonPropertyName("sanitizerViolations")]
    public List<string> SanitizerViolations { get; set; } = new();
}
