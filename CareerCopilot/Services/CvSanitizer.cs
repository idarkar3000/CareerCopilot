using System.Text.RegularExpressions;
using CareerCopilot.Models;

namespace CareerCopilot.Services;

public class CvSanitizer
{
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "api", "apis", "app", "apps", "backend", "frontend", "fullstack", "web", "software", "datos",
        "servicio", "servicios", "sistema", "sistemas", "aplicacion", "aplicaciones", "cliente",
        "clientes", "proyecto", "proyectos", "equipo", "codigo", "desarrollo", "desarrollador",
        "programacion", "programador", "arquitectura", "patrones", "patron", "patrones", "base",
        "bases", "lenguaje", "lenguajes", "framework", "libreria", "librerias", "herramienta",
        "herramientas", "metodologia", "metodologias", "agil", "scrum", "kanban", "gitflow", "git",
        "github", "gitlab", "azure", "devops", "docker", "cloud", "nube", "seguridad", "calidad",
        "pruebas", "testing", "test", "tests", "control", "versiones", "codigo", "modernizacion",
        "migracion", "plataforma", "corporativa", "legacy", "microservicios", "microservicio",
        "arquitecto", "ingeniero", "ingenieria", "tecnico", "superior", "universidad", "grado",
        "ingenieria", "empresa", "oferta", "vacante", "puesto", "candidato", "perfil", "experiencia",
        "profesional", "junior", "trainee", "practicas", "formacion", "academica", "idiomas",
        "espanol", "ingles", "nativo", "certificado", "certificacion", "b2", "c2", "a2", "master",
        "repositorio", "deploy", "despliegue", "entorno", "entornos", "version", "versiones",
        "documentacion", "swagger", "openapi", "postman", "seguridad", "autenticacion", "autorizacion",
        "permisos", "roles", "rol", "usuario", "usuarios", "operacion", "operaciones", "endpoint",
        "endpoints", "peticion", "peticiones", "respuesta", "respuestas", "consulta", "consultas",
        "consultas", "indices", "indice", "optimizacion", "refactorizacion", "refactorizando",
        "estructuras", "diccionarios", "memoria", "tiempos", "tiempo", "procesamiento", "bloqueos",
        "timeout", "segundos", "sub", "forma", "convencion", "europeo", "coma", "punto", "importes",
        "dato", "formato", "formatos", "interfaz", "componentes", "servicio", "conexion", "apoyo",
        "codigo", "equipos", "contratos", "contrato", "frontal", "caso", "reglas", "regla",
        "entradas", "entrada", "rechazo", "inválidas", "validas", "slices", "slice", "handlers",
        "handler", "request", "requests", "response", "viewmodel", "viewmodels", "lectura",
        "escritura", "queries", "commands", "query", "command", "solucion", "soluciones",
        "arquitecturas", "microservicios", "web", "correo", "telefono", "enlace", "enlaces",
        "contacto", "ciudad", "disponibilidad", "remoto", "hibrido", "presencial", "madrid",
        "abril", "mayo", "marzo", "junio", "julio", "septiembre", "enero", "febrero", "octubre",
        "noviembre", "diciembre", "agosto", "años", "año", "meses", "semanas", "mes",
        "proyecto", "reto", "reto", "reto", "reto"
    };

    private static readonly string[] BrandNoise = { "core", "framework", "sdk", "latest" };

    private static readonly Regex MeasurementPattern =
        new(@"^\d+(ms|s|m|h|d|w)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownTechnologies = new(StringComparer.Ordinal)
    {
        "csharp", "java", "javascript", "typescript", "python", "php", "go", "golang", "kotlin",
        "swift", "ruby", "rust", "scala", "sql", "html", "html5", "css", "css3", "sass", "scss",
        "bash", "powershell", "plsql", "tsql", "vb", "visualbasic", "fsharp", "elixir", "erlang",
        "dotnet", "net", "netcore", "aspnet", "aspnetcore", "minimalapis", "minimalapi",
        "entityframework", "efcore", "dapper", "mapster", "automapper", "fluentvalidation",
        "mediatr", "masstransit", "nsubstitute", "moq", "castle", "hangfire", "quartz", "polly",
        "serilog", "nlog", "log4net", "newtonsoft", "systemtextjson", "httpclient",
        "angular", "angularjs", "react", "reactjs", "vue", "vuejs", "svelte", "nextjs", "nuxt",
        "redux", "mobx", "rxjs", "rxjava", "jquery", "bootstrap", "tailwind", "sass", "vite",
        "webpack", "storybook", "blazor", "razor", "tailwindcss", "material",
        "sqlserver", "mssql", "postgresql", "postgres", "mysql", "mariadb", "mongodb", "redis",
        "sqlite", "oracle", "informix", "elasticsearch", "opensearch", "cassandra", "neo4j",
        "dynamodb", "snowflake", "bigquery", "databricks", "kafka", "rabbitmq", "servicebus",
        "nservicebus", "activemq", "sqs", "cosmosdb", "memcached", "clickhouse", "duckdb",
        "spark", "hadoop", "airflow", "dbt", "linq", "ado", "ado", "odbc", "oledb", "entityframeworkcore",
        "azure", "aws", "amazonwebservices", "gcp", "googlecloud", "docker", "dockerfile",
        "kubernetes", "helm", "terraform", "ansible", "jenkins", "githubactions", "argocd",
        "openshift", "linux", "ubuntu", "debian", "nginx", "apache", "iis", "kafka", "grafana",
        "prometheus", "datadog", "newrelic", "sentry", "sonarqube", "sonar", "zabbix", "netdata",
        "loadbalancer", "proxy", "reverse", "cdn", "vpc", "iam", "lambda", "cloudfunctions",
        "wpf", "winforms", "xaml", "avalonia", "maui", "electron", "qt", "powerbuilder", "delphi",
        "flutter", "reactnative", "xamarin", "android", "ios", "swiftui", "jetpackcompose",
        "cqrs", "ddd", "solid", "cleanarchitecture", "onionarchitecture", "hexagonalarchitecture",
        "verticalslice", "microservices", "microservice", "rest", "graphql", "grpc", "soap",
        "mvvm", "mvp", "saga", "repository", "unitofwork", "factory", "singleton", "observer",
        "eventdriven", "mapstruct", "tdd", "bdd", "ddd", "agile", "scrum", "kanban", "gitflow",
        "devops", "cicd", "unit", "integration", "e2e", "smoke", "benchmarking", "profiling",
        "jwt", "oauth", "oauth2", "openidconnect", "identityserver", "keycloak", "auth0", "saml",
        "ldap", "bcrypt", "hash", "encriptacion", "tls", "ssl", "owasp",
        "git", "github", "gitlab", "bitbucket", "jira", "confluence", "azuredevops", "swagger",
        "openapi", "postman", "insomnia", "excel", "vscode", "visualstudio", "rider", "intellij",
        "xunit", "nunit", "mstest", "testcontainers", "resharper", "postsharp", "fiddler",
        "charles", "wireshark", "putty", "navicat", "dbeaver", "kibana", "logstash"
    };

    private readonly CandidateProfileProvider _profile;
    private readonly ILogger<CvSanitizer> _logger;

    public CvSanitizer(CandidateProfileProvider profile, ILogger<CvSanitizer> logger)
    {
        _profile = profile;
        _logger = logger;
    }

    public List<string> Sanitize(CvDocument document)
    {
        var violations = new List<string>();
        if (document == null) return violations;

        document.Headline = Check("headline", document.Headline, violations) ?? string.Empty;
        document.Summary = Check("summary", document.Summary, violations) ?? string.Empty;

        var keptSections = new List<CvSection>();
        foreach (var section in document.Sections ?? new List<CvSection>())
        {
            if (section == null) continue;
            section.Heading = Check($"{section.Heading}/encabezado", section.Heading, violations) ?? string.Empty;
            section.Kind = CvSectionKinds.Normalize(section.Kind);
            section.Priority = Math.Clamp(section.Priority, 1, 3);

            var keptItems = new List<CvItem>();
            foreach (var item in section.Items ?? new List<CvItem>())
            {
                if (item == null) continue;

                item.Label = Check("label", item.Label, violations);
                item.Text = Check("text", item.Text, violations);
                item.Title = Check("title", item.Title, violations);
                item.Org = Check("org", item.Org, violations);
                item.Dates = Check("dates", item.Dates, violations);
                item.Stack = Check("stack", item.Stack, violations);
                item.Url = CheckUrl(item.Url, violations);

                item.UrlLabel = item.UrlLabel?.Trim();

                if (item.Bullets is { Count: > 0 })
                {
                    var keptBullets = new List<string>();
                    foreach (var bullet in item.Bullets)
                    {
                        var clean = Check("viñeta", bullet, violations);
                        if (!string.IsNullOrWhiteSpace(clean)) keptBullets.Add(clean);
                    }
                    item.Bullets = keptBullets;
                }

                bool isEmpty =
                    string.IsNullOrWhiteSpace(item.Title) &&
                    string.IsNullOrWhiteSpace(item.Text) &&
                    string.IsNullOrWhiteSpace(item.Label) &&
                    (item.Bullets is null || item.Bullets.Count == 0);

                if (isEmpty) continue;
                keptItems.Add(item);
            }

            section.Items = keptItems;
            if (keptItems.Count == 0) continue;
            keptSections.Add(section);
        }

        document.Sections = keptSections;

        if (violations.Count > 0)
        {
            _logger.LogWarning(
                "Guardarraíl del CV: {Count} token(s) no respaldados por el perfil se han eliminado: {Tokens}",
                violations.Count, string.Join(", ", violations.Distinct()));
        }

        return violations;
    }

    private string? Check(string where, string? text, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var invented = FindUnsourcedTokens(text);
        if (invented.Count == 0) return text;

        violations.AddRange(invented);
        _logger.LogWarning("CV: '{Where}' descartado por tecnología no presente en el perfil: {Tokens}",
            where, string.Join(", ", invented));

        if (text.Length <= 260) return null;

        var cleaned = RemoveUnsourcedTokens(text, violations, invented);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private string? CheckUrl(string? url, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var clean = url.Trim();

        if (!clean.Contains("://", StringComparison.Ordinal))
        {
            if (!IsPlausibleHost(clean)) return RejectUrl(clean, violations);
            clean = $"https://{clean}";
        }

        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return RejectUrl(clean, violations);
        }

        return clean;
    }

    private static bool IsPlausibleHost(string value)
    {
        var slash = value.IndexOf('/');
        var host = slash < 0 ? value : value[..slash];

        return !host.Contains(' ')
            && host.Contains('.')
            && !host.StartsWith('.')
            && !host.EndsWith('.')
            && host.Split('.').All(label => label.Length > 0 && char.IsLetter(label[0]) && label.All(char.IsLetterOrDigit));
    }

    private string? RejectUrl(string url, List<string> violations)
    {
        violations.Add($"url:{url}");
        _logger.LogWarning("Enlace descartado por no ser una URL http/https válida: '{Url}'.", url);
        return null;
    }

    private static readonly (string Alias, string Canonical)[] TokenAliases =
    {
        ("csharp", "c"),
        ("fsharp", "f"),
        ("js", "javascript"),
        ("ts", "typescript"),
        ("py", "python"),
        ("golang", "go"),
        ("netcore", "net"),
        ("dotnetcore", "net"),
        ("entityframework", "entityframeworkcore"),
        ("ef", "entityframeworkcore"),
        ("postgres", "postgresql"),
        ("mongo", "mongodb"),
        ("k8s", "kubernetes"),
        ("gcp", "googlecloud"),
        ("oap", "openapi")
    };

    private string? UnsourcedTechnology(string word)
    {
        var core = word.Trim('.', '-', ',', ';', ':', ')', ']', '"', '\'');
        if (core.Length < 2) return null;

        var normalized = CandidateProfileProvider.Normalize(core);
        if (normalized.Length < 2) return null;
        if (GenericWords.Contains(normalized)) return null;
        if (!IsTechnologyToken(core, normalized)) return null;
        if (_profile.ProfileMentions(normalized)) return null;

        foreach (var (alias, canonical) in TokenAliases)
        {
            if (normalized == alias && _profile.ProfileMentions(canonical)) return null;
        }

        if (BrandNoise.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length + 2))
        {
            var trimmed = normalized[..^BrandNoise.Max(s => s.Length)];
            if (_profile.ProfileMentions(trimmed)) return null;
        }

        if (normalized.EndsWith('s') && normalized.Length > 4 && _profile.ProfileMentions(normalized[..^1]))
            return null;

        return normalized;
    }

    private List<string> FindUnsourcedTokens(string text)
    {
        var found = new List<string>();

        foreach (var word in SplitWords(text))
        {
            var token = UnsourcedTechnology(word);
            if (token is not null && !found.Contains(token)) found.Add(token);
        }

        return found;
    }
    private string RemoveUnsourcedTokens(string text, List<string> violations, List<string> alreadyReported)
    {
        var cleaned = Regex.Replace(text, @"[\p{L}\p{N}][\p{L}\p{N}#+.\-]*", match =>
        {
            var token = UnsourcedTechnology(match.Value);
            if (token is null) return match.Value;

            if (!alreadyReported.Contains(token)) violations.Add(token);
            return " ";
        });

        return Tidy(cleaned);
    }

    private static string Tidy(string text) =>
        Regex
            .Replace(
                Regex.Replace(
                    Regex.Replace(text, @"\s{2,}", " "),
                    @"\s+([,.;:!?)\]])", "$1"),
                @"\(\s+", "(")
            .Trim();

    private static bool IsTechnologyToken(string core, string normalized)
    {
        // Heurística para adivinar si una palabra es una tecnología: mayúsculas internas, #, +, . o guion.
        if (KnownTechnologies.Contains(normalized)) return true;

        if (normalized.Any(char.IsDigit))
        {
            return normalized.Any(char.IsLetter) && !MeasurementPattern.IsMatch(normalized);
        }

        if (core.Contains('#') || core.Contains('+')) return true;
        if (core.Contains('.') && core.Any(char.IsLetter)) return true;
        if (core.Contains('-') && core.Any(char.IsLetter)) return true;

        for (var i = 1; i < core.Length; i++)
        {
            if (char.IsUpper(core[i]) && char.IsLetter(core[i - 1])) return true;
        }

        var letters = core.Where(char.IsLetter).ToArray();
        return letters.Length is >= 2 and <= 6 && letters.All(char.IsUpper);
    }

    private static IEnumerable<string> SplitWords(string text) =>
        Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}#+.\-]*")
            .Select(m => m.Value)
            .Where(w => w.Length >= 2);
}
