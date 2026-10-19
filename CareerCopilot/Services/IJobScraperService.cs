using CareerCopilot.Models;

namespace CareerCopilot.Services
{
    public interface IJobScraperService
    {
        Task<List<JobOffer>> FetchTecnoEmpleoJobsAsync(string query, CancellationToken ct);

        Task<List<JobOffer>> FetchLinkedInJobsAsync(string query, CancellationToken ct);

        Task<List<JobOffer>> FetchRemotiveJobsAsync(CancellationToken ct);

        Task<List<JobOffer>> FetchAdzunaJobsAsync(string query, CancellationToken ct);

        Task<List<JobOffer>> FetchInfoJobsJobsAsync(string query, CancellationToken ct);

        Task<List<JobOffer>> FetchGenericFeedsAsync(CancellationToken ct);
    }
}
