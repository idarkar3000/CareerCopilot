using CareerCopilot.Models;

namespace CareerCopilot.Services
{
    public interface ITelegramNotifierService
    {
        Task StartReceivingAsync(CancellationToken ct);

        Task SendNotificationAsync(JobOffer job, EvaluationResult eval, string? pdfPath, CancellationToken ct);

        Task SendSystemAlertAsync(string html, CancellationToken ct = default);
    }
}
