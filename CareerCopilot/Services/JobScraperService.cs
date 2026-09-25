using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareerCopilot.Models;
using Microsoft.Extensions.Configuration;

namespace CareerCopilot.Services;

public class JobScraperService
{
    private readonly HttpClient _http;
    private readonly ILogger<JobScraperService> _logger;
    private readonly string? _adzunaAppId;
    private readonly string? _adzunaAppKey;

    public JobScraperService(HttpClient http, ILogger<JobScraperService> logger, IConfiguration configuration)
    {
        _http = http;
        _logger = logger;

        // appsettings.json:
        // "JobSources": { "Adzuna": { "AppId": "...", "AppKey": "..." } }
        _adzunaAppId = configuration["JobSources:Adzuna:AppId"];
        _adzunaAppKey = configuration["JobSources:Adzuna:AppKey"];
    }

    // --- 1. TECNOEMPLEO (Extractor HTML limpio sobre el listado real) ---
    public async Task<List<JobOffer>> FetchTecnoEmpleoJobsAsync(string query, CancellationToken ct)
    {
        var list = new List<JobOffer>();
        var url = $"https://www.tecnoempleo.com/ofertas-trabajo/?te={Uri.EscapeDataString(query)}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            var response = await _http.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode) return list;

            var html = await response.Content.ReadAsStringAsync(ct);

            // Coincide con los enlaces directos a las fichas de trabajo de Tecnoempleo
            var matches = Regex.Matches(html, @"href=""(https://www\.tecnoempleo\.com/[^""]*?rf-[a-z0-9\-]+)""[^>]*?>([^<]+)</a>", RegexOptions.IgnoreCase);

            foreach (Match m in matches)
            {
                var offerUrl = m.Groups[1].Value.Trim();
                var title = CleanHtml(m.Groups[2].Value);

                if (string.IsNullOrWhiteSpace(title) || title.Length < 4 || title.Contains("ofertas de empleo", StringComparison.OrdinalIgnoreCase))
                    continue;

                var idMatch = Regex.Match(offerUrl, @"rf-([a-z0-9\-]+)", RegexOptions.IgnoreCase);
                var id = idMatch.Success ? "te_" + idMatch.Groups[1].Value : "te_" + Math.Abs(offerUrl.GetHashCode()).ToString();

                if (list.All(x => x.Id != id))
                {
                    list.Add(new JobOffer(id, title, "Empresa Tecnoempleo", offerUrl, $"{title} en Tecnoempleo", DateTime.UtcNow));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aviso consultando Tecnoempleo.");
        }

        return list;
    }

    // --- 2. LINKEDIN GUEST API (Principal motor estable) ---
    public async Task<List<JobOffer>> FetchLinkedInJobsAsync(string query, CancellationToken ct)
    {
        var list = new List<JobOffer>();
        var url = $"https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?keywords={Uri.EscapeDataString(query)}&location=Spain&f_TPR=r604800";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

            var response = await _http.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode) return list;

            var html = await response.Content.ReadAsStringAsync(ct);
            var cardMatches = Regex.Matches(html, @"<li[\s\S]*?</li>");

            foreach (Match card in cardMatches)
            {
                var snippet = card.Value;

                var titleMatch = Regex.Match(snippet, @"(?:base-search-card__title|job-search-card__title)[^>]*>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
                if (!titleMatch.Success)
                    titleMatch = Regex.Match(snippet, @"<h3[^>]*>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);

                var companyMatch = Regex.Match(snippet, @"(?:base-search-card__subtitle|job-search-card__subtitle)[\s\S]*?<a[^>]*>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
                if (!companyMatch.Success)
                    companyMatch = Regex.Match(snippet, @"(?:base-search-card__subtitle|job-search-card__subtitle)[^>]*>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);

                var linkMatch = Regex.Match(snippet, @"href=""(https://[a-z]{2,3}\.linkedin\.com/jobs/view/[^""\?]+)", RegexOptions.IgnoreCase);
                if (!linkMatch.Success)
                    linkMatch = Regex.Match(snippet, @"href=""([^""]*linkedin\.com/jobs/view/[^""]*)""", RegexOptions.IgnoreCase);

                if (titleMatch.Success && linkMatch.Success)
                {
                    var title = titleMatch.Groups[1].Value.Trim();
                    var company = companyMatch.Success ? companyMatch.Groups[1].Value.Trim() : "Empresa en LinkedIn";
                    var rawLink = linkMatch.Groups[1].Value.Trim();

                    var idMatch = Regex.Match(rawLink, @"(\d{8,})");
                    var id = idMatch.Success ? idMatch.Value : Math.Abs(rawLink.GetHashCode()).ToString();
                    var finalUrl = $"https://es.linkedin.com/jobs/view/{id}";

                    list.Add(new JobOffer("li_" + id, title, company, finalUrl, $"{title} en {company}", DateTime.UtcNow));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al consultar LinkedIn para '{Query}'.", query);
        }

        return list;
    }

    // --- 3. REMOTIVE API (API Pública JSON sin Cloudflare - Ofertas de Software remoto compatibles) ---
    public async Task<List<JobOffer>> FetchRemotiveJobsAsync(CancellationToken ct)
    {
        var list = new List<JobOffer>();
        var url = "https://remotive.com/api/remote-jobs?category=software-dev&limit=25";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("CareerCopilot-Agent/1.0");

            var response = await _http.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode) return list;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("jobs", out var jobsElement))
            {
                foreach (var item in jobsElement.EnumerateArray())
                {
                    var title = item.GetProperty("title").GetString() ?? "";
                    var company = item.GetProperty("company_name").GetString() ?? "Empresa Remota";
                    var jobUrl = item.GetProperty("url").GetString() ?? "";
                    var candidateLocations = item.TryGetProperty("candidate_required_location", out var loc) ? loc.GetString() ?? "" : "";

                    // Filtrar por ofertas aplicables a España / Worldwide y tecnologías .NET / C#
                    var text = $"{title} {candidateLocations}".ToLowerInvariant();
                    if ((text.Contains("c#") || text.Contains(".net") || text.Contains("backend")) &&
                        (candidateLocations.Contains("Spain", StringComparison.OrdinalIgnoreCase) ||
                         candidateLocations.Contains("Worldwide", StringComparison.OrdinalIgnoreCase) ||
                         candidateLocations.Contains("Europe", StringComparison.OrdinalIgnoreCase)))
                    {
                        var id = "rem_" + item.GetProperty("id").GetInt64();
                        list.Add(new JobOffer(id, title, company, jobUrl, $"{title} en {company} ({candidateLocations})", DateTime.UtcNow));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aviso consultando Remotive API.");
        }

        return list;
    }

    // --- 4. ADZUNA API (Sustituto legal y estable de Indeed España) ---
    // Requiere registro gratuito en https://developer.adzuna.com/ (App ID + App Key).
    // Cubre España vía el segmento /es/ y en la práctica agrega muchas ofertas
    // que también aparecen en Indeed, sin WAF ni fingerprinting TLS que esquivar.
    public async Task<List<JobOffer>> FetchAdzunaJobsAsync(string query, CancellationToken ct)
    {
        var list = new List<JobOffer>();

        if (string.IsNullOrWhiteSpace(_adzunaAppId) || string.IsNullOrWhiteSpace(_adzunaAppKey))
        {
            _logger.LogWarning("Adzuna no está configurado (falta JobSources:Adzuna:AppId/AppKey en appsettings.json); se omite esta fuente.");
            return list;
        }

        var url = "https://api.adzuna.com/v1/api/jobs/es/search/1" +
                  $"?app_id={Uri.EscapeDataString(_adzunaAppId)}" +
                  $"&app_key={Uri.EscapeDataString(_adzunaAppKey)}" +
                  "&results_per_page=25" +
                  $"&what={Uri.EscapeDataString(query)}" +
                  "&content-type=application/json";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("CareerCopilot-Agent/1.0");

            var response = await _http.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Adzuna respondió {Status} para la búsqueda '{Query}'.", response.StatusCode, query);
                return list;
            }

            // Leemos como array de bytes para evitar que el charset no estándar de Adzuna
            // ('charset=utf8' sin guion) dispare una InvalidOperationException en ReadAsStringAsync.
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var json = Encoding.UTF8.GetString(bytes);

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results))
                return list;

            foreach (var item in results.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? CleanHtml(t.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(title)) continue;

                var company = item.TryGetProperty("company", out var comp) && comp.TryGetProperty("display_name", out var compName)
                    ? compName.GetString() ?? "Empresa en Adzuna"
                    : "Empresa en Adzuna";

                var jobUrl = item.TryGetProperty("redirect_url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(jobUrl)) continue;

                var location = item.TryGetProperty("location", out var loc) && loc.TryGetProperty("display_name", out var locName)
                    ? locName.GetString() ?? ""
                    : "";

                var id = item.TryGetProperty("id", out var idProp)
                    ? "adz_" + idProp.GetString()
                    : "adz_" + Math.Abs(jobUrl.GetHashCode());

                if (list.All(x => x.Id != id))
                {
                    var summary = string.IsNullOrWhiteSpace(location)
                        ? $"{title} en {company}"
                        : $"{title} en {company} ({location})";

                    list.Add(new JobOffer(id, title, company, jobUrl, summary, DateTime.UtcNow));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aviso consultando Adzuna API para '{Query}'.", query);
        }

        return list;
    }

    private static string CleanHtml(string input)
    {
        return Regex.Replace(input, @"<[^>]+>|&nbsp;", " ").Trim();
    }
}