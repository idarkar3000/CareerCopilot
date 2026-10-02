namespace CareerCopilot.Models;

public class BotConfig
{
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Segundos que se espera a Gemini antes de dar el modelo por saturado. Las llamadas buenas
    /// tardan unos 4s, así que con 20s se distinguen de las que se quedan colgadas sin responder.
    /// </summary>
    public int GeminiTimeoutSeconds { get; set; } = 20;

    public string TelegramBotToken { get; set; } = string.Empty;
    public long TelegramChatId { get; set; }
    public int MinScoreThreshold { get; set; } = 75;
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Ofertas que se mandan a Gemini en un ciclo. El plan gratis da 20 al día por modelo, así que
    /// sin tope un ciclo se gasta la cuota entera. Las que no se alcancen quedan para el siguiente.
    /// </summary>
    public int MaxOffersPerCycle { get; set; } = 12;

    /// <summary>Viñetas de la experiencia. Es la sección más importante, así que va la última en recortarse.</summary>
    public int ExperienceBullets { get; set; } = 5;

    /// <summary>Viñetas por proyecto. Menos que en la experiencia para que la sección no se alargue.</summary>
    public int ProjectBullets { get; set; } = 3;

    /// <summary>Perfil en texto. Si existe CandidateProfileFile, manda el archivo sobre este texto.</summary>
    public string CandidateProfile { get; set; } = string.Empty;

    /// <summary>Ruta, relativa a la base de la aplicación, del archivo de perfil markdown.</summary>
    public string CandidateProfileFile { get; set; } = "prompts/profile.md";

    public string InfoJobsClientId { get; set; } = string.Empty;
    public string InfoJobsClientSecret { get; set; } = string.Empty;
    public List<string> RequiredKeywords { get; set; } = new();
    public List<string> ExcludedKeywords { get; set; } = new();
    public List<string> SearchQueries { get; set; } = new();
    public List<string> Feeds { get; set; } = new();

    /// <summary>Datos personales y de contacto que se imprimen en la cabecera del CV.</summary>
    public CandidateConfig Candidate { get; set; } = new();

    /// <summary>Criterio de ubicación al que se aceptan las ofertas.</summary>
    public LocationFilterConfig LocationFilter { get; set; } = new();
}

public class CandidateConfig
{
    public string FullName { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;

    /// <summary>Texto del pie del CV (ej. "Madrid · Disponible inmediatamente").</summary>
    public string FooterNote { get; set; } = string.Empty;

    /// <summary>Plantilla del nombre de fichero: {job} = puesto, {name} = nombre normalizado.</summary>
    public string PdfFileNameTemplate { get; set; } = "CV_{job}_{name}";

    /// <summary>Directorio de salida; relativo si no es una ruta absoluta. Por defecto "GeneratedCVs".</summary>
    public string OutputDir { get; set; } = "GeneratedCVs";

    public List<ContactLink> Links { get; set; } = new();
}

public class ContactLink
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class LocationFilterConfig
{
    /// <summary>Si está vacía no se aplica filtro geográfico (solokeywords de remoto).</summary>
    public List<string> AcceptedLocations { get; set; } = new();

    /// <summary>Palabras que, en título o descripción, convierten la oferta en remota.</summary>
    public List<string> RemoteKeywords { get; set; } = new();
}
