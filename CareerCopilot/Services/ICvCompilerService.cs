using CareerCopilot.Models;

namespace CareerCopilot.Services
{
    public interface ICvCompilerService
    {
        Task<string?> GeneratePdfAsync(JobOffer job, EvaluationResult eval, CancellationToken ct);
    }
}
