using System.Text.Json.Serialization;

namespace CareerCopilot.Models
{
    public class CvDocument
    {
        [JsonPropertyName("headline")]
        public string Headline { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("sections")]
        public List<CvSection> Sections { get; set; } = new();
    }
}
