using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerCopilot.Models;

namespace CareerCopilot.Services;

public class CandidateProfileProvider
{
    private readonly BotConfig _config;
    private readonly ILogger<CandidateProfileProvider> _logger;
    private readonly Lock _loadLock = new();

    private string? _text;
    private HashSet<string>? _normalizedTokens;
    private string _source = string.Empty;
    private CvAnchors? _anchors;

    public CandidateProfileProvider(BotConfig config, ILogger<CandidateProfileProvider> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string Text
    {
        get
        {
            EnsureLoaded();
            return _text!;
        }
    }

    public string Source
    {
        get
        {
            EnsureLoaded();
            return _source;
        }
    }

    public HashSet<string> NormalizedTokens
    {
        get
        {
            EnsureLoaded();
            return _normalizedTokens!;
        }
    }

    public CvAnchors Anchors
    {
        get
        {
            EnsureLoaded();
            return _anchors!;
        }
    }

    public bool ProfileMentions(string token)
    {
        var normalized = Normalize(token);
        return normalized.Length > 0 && NormalizedTokens.Contains(normalized);
    }

    public static string Normalize(string input) =>
        new string(input
            .ToLowerInvariant()
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => !CharUnicodeInfo.GetUnicodeCategory(c).Equals(UnicodeCategory.NonSpacingMark))
            .ToArray())
            .Replace(".", string.Empty)
            .Replace("#", string.Empty)
            .Replace("+", string.Empty)
            .Trim();

    private void EnsureLoaded()
    {
        if (_text is not null) return;

        lock (_loadLock)
        {
            if (_text is not null) return;

            var text = string.Empty;
            var source = "ninguna";

            var configuredPath = _config.CandidateProfileFile;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                var fullPath = Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(AppContext.BaseDirectory, configuredPath);

                if (File.Exists(fullPath))
                {
                    try
                    {
                        text = File.ReadAllText(fullPath);
                        source = configuredPath;
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "No se pudo leer el perfil '{Path}'.", fullPath);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(_config.CandidateProfile))
            {
                text = _config.CandidateProfile;
                source = "appsettings:BotConfig:CandidateProfile";
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogError(
                    "No hay perfil del candidato: ni '{Path}' ni BotConfig:CandidateProfile. Gemini no podrá evaluar nada sin alucinaciones.",
                    configuredPath);
            }

            _text = text;
            _source = source;
            _normalizedTokens = BuildTokens(text);
            _anchors = ParseAnchors(text);
            _logger.LogInformation(
                "Perfil del candidato cargado desde '{Source}' ({Chars} caracteres, {Exp} anclas de experiencia, {Proj} de proyecto).",
                source, text.Length, _anchors.Experience.Count, _anchors.Projects.Count);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const string AnchorMarker = "<!-- cv-anchors -->";

    private CvAnchors ParseAnchors(string text)
    {
        var empty = new CvAnchors();
        if (string.IsNullOrWhiteSpace(text)) return empty;

        var markerIndex = text.IndexOf(AnchorMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            _logger.LogWarning(
                "El perfil no contiene el bloque '{Marker}'. Los CV dependerán de que Gemini rellene empresa, stack y enlaces.",
                AnchorMarker);
            return empty;
        }

        var start = text.IndexOf('{', markerIndex);
        if (start < 0)
        {
            _logger.LogWarning("El bloque de anclas del perfil no contiene un objeto JSON.");
            return empty;
        }

        var depth = 0;
        var end = -1;
        var inString = false;
        var escaped = false;

        // Recorre el texto contando llaves (ignorando las que van dentro de comillas) para sacar el JSON completo.
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) { end = i; break; }
                    break;
            }

            if (end >= 0) break;
        }

        if (end < 0)
        {
            _logger.LogWarning("El bloque de anclas del perfil tiene las llaves desbalanceadas; se ignora.");
            return empty;
        }

        try
        {
            var anchors = JsonSerializer.Deserialize<CvAnchors>(text[start..(end + 1)], JsonOptions)
                ?? new CvAnchors();
            Sanitize(anchors);
            return anchors;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "El bloque de anclas del perfil no es JSON válido; se ignora. Revisa el bloque '{Marker}'.", AnchorMarker);
            return empty;
        }
    }

    private static void Sanitize(CvAnchors anchors)
    {
        anchors.Experience.RemoveAll(e => string.IsNullOrWhiteSpace(e.Title));
        anchors.Projects.RemoveAll(e => string.IsNullOrWhiteSpace(e.Title));
    }

    private static HashSet<string> BuildTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        foreach (Match match in Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}#+.\-]*"))
        {
            var normalized = Normalize(match.Value.Trim('.', '-'));
            if (normalized.Length > 0) tokens.Add(normalized);
        }

        return tokens;
    }
}
