using CareerCopilot.Models;

namespace CareerCopilot.Services;

/// <summary>
/// Monta el CV sin llamar a Gemini, para cuando no queda cuota. Reutiliza el texto fijo del bloque
/// de anclas y deja experiencia y proyectos vacíos a propósito, para que los rellene el
/// compilador igual que cuando el documento viene del modelo.
/// </summary>
public sealed class LocalCvBuilder
{
    private readonly ILogger<LocalCvBuilder> _logger;
    private readonly BotConfig _config;
    private readonly CandidateProfileProvider _profile;

    public LocalCvBuilder(
        ILogger<LocalCvBuilder> logger,
        BotConfig config,
        CandidateProfileProvider profile)
    {
        _logger = logger;
        _config = config;
        _profile = profile;
    }

    /// <summary>
    /// Construye el documento. Devuelve null si el perfil no trae el bloque "profile", porque sin
    /// él solo se podría rellenar la experiencia y el CV saldría incompleto.
    /// </summary>
    public EvaluationResult? Build(JobOffer job)
    {
        var anchors = _profile.Anchors;
        if (anchors.Profile.IsEmpty)
        {
            _logger.LogError(
                "El perfil no trae el bloque 'profile' (summary, skills, education, languages); no se puede generar el CV local de '{Title}'.",
                job.Title);
            return null;
        }

        var document = new CvDocument
        {
            Headline = _config.Candidate.Headline,
            Summary = anchors.Profile.Summary,
            Sections =
            [
                // Vacías a propósito: ApplyAnchors las rellena con empresa, fechas, stack y viñetas
                new CvSection { Heading = "Experiencia Laboral", Priority = 1, Kind = CvSectionKinds.Entries },
                new CvSection { Heading = "Proyectos", Priority = 2, Kind = CvSectionKinds.Entries },

                new CvSection
                {
                    Heading = "Habilidades Técnicas",
                    Priority = 1,
                    Kind = CvSectionKinds.Texts,
                    Items = ToItems(anchors.Profile.Skills)
                },
                new CvSection
                {
                    Heading = "Formación Académica",
                    Priority = 2,
                    Kind = CvSectionKinds.Texts,
                    Items = ToItems(anchors.Profile.Education)
                },
                new CvSection
                {
                    Heading = "Idiomas",
                    Priority = 3,
                    Kind = CvSectionKinds.Texts,
                    Items = ToItems(anchors.Profile.Languages)
                }
            ]
        };

        _logger.LogInformation(
            "CV local montado para '{Title}': {Skills} habilidades, {Education} titulaciones, {Languages} idiomas.",
            job.Title, anchors.Profile.Skills.Count, anchors.Profile.Education.Count, anchors.Profile.Languages.Count);

        return new EvaluationResult { Cv = document, IsLocal = true };
    }

    private static List<CvItem> ToItems(List<CvAnchorLine> lines) =>
        lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Label) && !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => new CvItem { Label = l.Label, Text = l.Text })
            .ToList();
}
