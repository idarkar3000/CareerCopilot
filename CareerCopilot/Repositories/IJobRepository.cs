using CareerCopilot.Models;

namespace CareerCopilot.Repositories
{
    public interface IJobRepository
    {
        bool HasBeenProcessed(string jobId);
        void MarkAsProcessed(JobOffer offer, int score);
        JobOffer? FindProcessedOffer(string term, out int score);
        bool RemoveProcessed(string id);
        (int Total, int Matches, double AverageScore, int Last24h) GetStats(int threshold);
        List<string> GetSearchQueries();
        bool AddSearchQuery(string query);
        bool RemoveSearchQuery(string query);
        void SeedDefaultQueries(List<string> defaults);
    }
}
