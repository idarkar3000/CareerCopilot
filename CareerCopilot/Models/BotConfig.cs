namespace CareerCopilot.Models;

public class BotConfig
{
    public string GeminiApiKey { get; set; } = string.Empty;

    public int GeminiTimeoutSeconds { get; set; } = 20;

    public string TelegramBotToken { get; set; } = string.Empty;
    public long TelegramChatId { get; set; }
    public int MinScoreThreshold { get; set; } = 75;
    public int CheckIntervalMinutes { get; set; } = 60;

    public int MaxOffersPerCycle { get; set; } = 12;

    public int ExperienceBullets { get; set; } = 5;

    public int ProjectBullets { get; set; } = 3;

    public string CandidateProfile { get; set; } = string.Empty;

    public string CandidateProfileFile { get; set; } = "prompts/profile.md";

    public string InfoJobsClientId { get; set; } = string.Empty;
    public string InfoJobsClientSecret { get; set; } = string.Empty;
    public List<string> RequiredKeywords { get; set; } = new();
    public List<string> ExcludedKeywords { get; set; } = new();
    public List<string> SearchQueries { get; set; } = new();
    public List<string> Feeds { get; set; } = new();

    public CandidateConfig Candidate { get; set; } = new();

    public LocationFilterConfig LocationFilter { get; set; } = new();
}

public class CandidateConfig
{
    public string FullName { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;

    public string FooterNote { get; set; } = string.Empty;

    public string PdfFileNameTemplate { get; set; } = "{job}_{name}";

    public string OutputDir { get; set; } = "GeneratedCVs";

    public List<ContactLink> Links { get; set; } = new();
}

public class ContactLink
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class LocationFilterConfig
{
    public List<string> AcceptedLocations { get; set; } = new();

    public List<string> RemoteKeywords { get; set; } = new();
}
