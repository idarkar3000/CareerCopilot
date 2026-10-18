using CareerCopilot.Models;

namespace CareerCopilot.Services;

public interface IManualActions
{
    bool IsBusy { get; }

    string BusyOrigin { get; }

    Task<bool> RunPipelineAsync(CancellationToken ct);

    Task<bool> RunAdHocScanAsync(string query, CancellationToken ct);

    Task<CvRequestResult> RegenerateCvAsync(JobOffer offer, CancellationToken ct, bool localOnly = false);

    KeywordCoverage CheckKeywords(JobOffer offer);
}

public record CvRequestResult(
    bool Ok,
    string Message,
    string? PdfPath = null,
    int Score = 0,
    string? JobTitle = null,
    string? JobLink = null,
    bool IsLocal = false,
    KeywordCoverage? Coverage = null);

public record KeywordCoverage(IReadOnlyList<string> Matched, IReadOnlyList<string> Missing);