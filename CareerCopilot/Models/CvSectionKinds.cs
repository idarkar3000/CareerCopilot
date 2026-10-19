namespace CareerCopilot.Models
{
    public static class CvSectionKinds
    {
        public const string Texts = "texts";
        public const string Entries = "entries";

        public static string Normalize(string? kind) =>
            kind?.Trim().ToLowerInvariant() switch
            {
                "entries" or "entry" or "list_entries" or "experience" or "proyectos" => Entries,
                _ => Texts
            };
    }
}
