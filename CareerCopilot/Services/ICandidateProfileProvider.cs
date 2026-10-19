using CareerCopilot.Models;

namespace CareerCopilot.Services
{
    public interface ICandidateProfileProvider
    {
        string Text { get; }

        string Source { get; }

        HashSet<string> NormalizedTokens { get; }

        CvAnchors Anchors { get; }

        bool ProfileMentions(string token);
    }
}
