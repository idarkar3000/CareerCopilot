using CareerCopilot.Models;

namespace CareerCopilot.Services
{
    public interface IGeminiScorerService
    {
        Task<EvaluationResult?> EvaluateAsync(JobOffer job, CancellationToken ct);
    }
}
