using CareerCopilot.Models;

namespace CareerCopilot.Services;

/// <summary>
/// Lo que el bot puede pedirle al pipeline. Se resuelve con un Func porque Worker y
/// TelegramNotifierService se necesitan mutuamente y si no habría ciclo en el DI.
/// </summary>
public interface IManualActions
{
    /// <summary>True si hay un escaneo en marcha.</summary>
    bool IsBusy { get; }

    /// <summary>Motivo del escaneo en curso, para poder avisar.</summary>
    string BusyOrigin { get; }

    /// <summary>Ciclo completo. False si ya había otro escaneo en marcha.</summary>
    Task<bool> RunPipelineAsync(CancellationToken ct);

    /// <summary>Escaneo de un término. False si ya había otro escaneo en marcha.</summary>
    Task<bool> RunAdHocScanAsync(string query, CancellationToken ct);

    /// <summary>Regenera el CV de una oferta ya registrada. Con localOnly no se llama a Gemini.</summary>
    Task<CvRequestResult> RegenerateCvAsync(JobOffer offer, CancellationToken ct, bool localOnly = false);

    /// <summary>Cobertura de las palabras clave filtradas en una oferta. Solo diagnóstico.</summary>
    KeywordCoverage CheckKeywords(JobOffer offer);
}

public record CvRequestResult(
    bool Ok,
    string Message,
    string? PdfPath = null,
    int Score = 0,
    string? JobTitle = null,
    string? JobLink = null,
    bool IsLocal = false,
    KeywordCoverage? Coverage = null);

/// <summary>Qué palabras clave de los filtros aparecen en la oferta y cuáles no.</summary>
public record KeywordCoverage(IReadOnlyList<string> Matched, IReadOnlyList<string> Missing);