using System.Text.Json.Serialization;

namespace CareerCopilot.Models
{
    public class CvItem
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("org")]
        public string? Org { get; set; }

        [JsonPropertyName("dates")]
        public string? Dates { get; set; }

        [JsonPropertyName("stack")]
        public string? Stack { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("urlLabel")]
        public string? UrlLabel { get; set; }

        [JsonIgnore]
        public bool FromProfile { get; set; }

        [JsonPropertyName("bullets")]
        public List<string>? Bullets { get; set; }
    }
}
