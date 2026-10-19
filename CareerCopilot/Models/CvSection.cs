using System.Text.Json.Serialization;

namespace CareerCopilot.Models
{
    public class CvSection
    {
        [JsonPropertyName("heading")]
        public string Heading { get; set; } = string.Empty;

        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 2;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = CvSectionKinds.Texts;

        [JsonPropertyName("items")]
        public List<CvItem> Items { get; set; } = new();
    }
}
