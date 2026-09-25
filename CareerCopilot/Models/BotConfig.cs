namespace CareerCopilot.Models;

public class BotConfig
{
    public string GeminiApiKey { get; set; } = string.Empty;
    public string TelegramBotToken { get; set; } = string.Empty;
    public long TelegramChatId { get; set; }
    public int MinScoreThreshold { get; set; } = 75;
    public int CheckIntervalMinutes { get; set; } = 60;
    public string CandidateProfile { get; set; } = string.Empty;
    public string InfoJobsClientId { get; set; } = string.Empty;
    public string InfoJobsClientSecret { get; set; } = string.Empty;
    public List<string> RequiredKeywords { get; set; } = new();
    public List<string> ExcludedKeywords { get; set; } = new();
    public List<string> SearchQueries { get; set; } = new();
    public List<string> Feeds { get; set; } = new();
}