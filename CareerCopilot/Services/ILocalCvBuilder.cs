using CareerCopilot.Models;

namespace CareerCopilot.Services
{
    public interface ILocalCvBuilder
    {
        EvaluationResult? Build(JobOffer job);
    }
}
