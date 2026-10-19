using CareerCopilot.Models;

namespace CareerCopilot.Services
{
    public interface ICvSanitizer
    {
        List<string> Sanitize(CvDocument document);
    }
}
