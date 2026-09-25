using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CareerCopilot.Models;
using UglyToad.PdfPig;

namespace CareerCopilot.Services;

public class CvCompilerService
{
    private readonly ILogger<CvCompilerService> _logger;

    private const int MaxSummaryChars = 480;
    private const int MaxBulletChars = 165;
    private static readonly decimal[] FontSizeLadder = { 8.8m, 8.5m, 8.2m, 7.9m, 7.6m };

    public CvCompilerService(ILogger<CvCompilerService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> GeneratePdfAsync(JobOffer job, EvaluationResult eval, CancellationToken ct)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "GeneratedCVs");
        Directory.CreateDirectory(outputDir);

        var cleanJob = SanitizeFileName(job.Title);
        var cleanCompany = SanitizeFileName(job.Company);
        var baseFileName = $"CV_{cleanJob}_Adrian_Espinola_Gumiel";

        var typstFile = Path.Combine(outputDir, $"{baseFileName}.typ");
        var pdfFile = Path.Combine(outputDir, $"{baseFileName}.pdf");

        var summary = PrepareText(eval.TailoredSummary, MaxSummaryChars);
        var bullets = (eval.TailoredExperience ?? new List<string>())
            .Take(4)
            .Select(b => PrepareText(b, MaxBulletChars))
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        string? lastAttemptPdf = null;

        for (var i = 0; i < FontSizeLadder.Length; i++)
        {
            var fontSize = FontSizeLadder[i];
            var typstContent = BuildTypstContent(summary, bullets, fontSize);

            await File.WriteAllTextAsync(typstFile, typstContent, new UTF8Encoding(false), ct);

            var compiled = await CompileTypstAsync(typstFile, pdfFile, ct);
            if (!compiled)
            {
                return null;
            }

            lastAttemptPdf = pdfFile;
            var pageCount = CountPdfPages(pdfFile);

            if (pageCount <= 1)
            {
                return pdfFile;
            }

            _logger.LogWarning(
                "CV compilado con {Pages} páginas a {Size}pt (intento {Attempt}/{Total}); reintentando con fuente menor.",
                pageCount, fontSize.ToString("0.0", CultureInfo.InvariantCulture), i + 1, FontSizeLadder.Length);
        }

        _logger.LogError(
            "No se logró ajustar el CV a 1 sola página tras {N} intentos; se devuelve la última versión compilada como mejor esfuerzo.",
            FontSizeLadder.Length);

        return lastAttemptPdf;
    }

    private static string BuildTypstContent(string summary, List<string> bullets, decimal fontSize)
    {
        var sizeStr = fontSize.ToString("0.0", CultureInfo.InvariantCulture);

        var experienceBullets = new StringBuilder();
        foreach (var bullet in bullets)
        {
            experienceBullets.AppendLine($"    [{bullet}],");
        }

        if (bullets.Count == 0)
        {
            experienceBullets.AppendLine("    [Contribución técnica adaptada al puesto durante la etapa de prácticas.],");
        }

        const string template = @"#set page(
  paper: ""a4"",
  margin: (top: 1.1cm, bottom: 1.1cm, left: 1.5cm, right: 1.5cm)
)

#set text(
  font: (""Segoe UI"", ""Arial""),
  size: __FONT_SIZE__pt,
  fill: rgb(""#111827""),
  lang: ""es""
)

#set par(justify: true, leading: 0.65em)

#let primary = rgb(""#0f172a"")
#let accent = rgb(""#1d4ed8"")
#let muted = rgb(""#475569"")

#let section-heading(title) = {
  v(0.55em)
  text(1.1em, weight: ""bold"", fill: primary, upper(title))
  v(-0.35em)
  line(length: 100%, stroke: 0.6pt + rgb(""#cbd5e1""))
  v(0.2em)
}

// --- CABECERA ---
#align(center)[
  #text(19pt, weight: ""bold"", fill: primary)[ADRIÁN ESPÍNOLA GUMIEL] \
  #v(2pt)
  #text(10.5pt, weight: ""semibold"", fill: accent)[Desarrollador Backend .NET / C\#] \
  #v(3pt)
  #text(8.5pt, fill: muted)[
    691 77 96 27 #h(8pt) | #h(8pt)
    #link(""mailto:espinolagumieladrian@gmail.com"")[espinolagumieladrian\@gmail.com] #h(8pt) | #h(8pt)
    #link(""https://github.com/idarkar3000"")[github.com/idarkar3000] #h(8pt) | #h(8pt)
    #link(""https://linkedin.com/in/adrian-espinola-gumiel"")[linkedin.com/in/adrian-espinola-gumiel]
  ]
]

#v(0.15em)

// --- PERFIL PROFESIONAL ---
#section-heading(""Perfil Profesional"")
#text(size: 1em)[
  __SUMMARY__
]

// --- EXPERIENCIA LABORAL ---
#section-heading(""Experiencia Laboral"")

#grid(
  columns: (1fr, auto),
  [
    #text(weight: ""bold"", size: 1.05em, fill: primary)[Desarrollador Backend .NET (Prácticas)] #text(weight: ""medium"", fill: muted)[ — EPAM Neoris]
  ],
  [
    #text(size: 0.95em, weight: ""semibold"", fill: muted)[Marzo 2026 - Junio 2026]
  ]
)
#v(0.15em)
#list(
  marker: [•],
  body-indent: 0.6em,
__BULLETS__)

// --- PROYECTOS DESTACADOS EN .NET ---
#section-heading(""Proyectos Destacados en .NET"")

#block[
  #grid(
    columns: (1fr, auto),
    [
      #text(weight: ""bold"", size: 1.03em, fill: primary)[RadarChollos] #text(size: 0.95em, fill: muted)[ — Monitorización y Automatización en Tiempo Real] \
      #v(-2pt)
      #text(size: 0.9em, style: ""italic"", fill: accent)[C\# .NET 9 | Minimal APIs | EF Core | SQLite | Docker | Telegram.Bot | Serilog | Render]
    ],
    [
      #text(size: 0.93em)[#link(""https://github.com/idarkar3000/RadarChollos"")[github.com/.../RadarChollos]]
    ]
  )
  #v(0.12em)
  #list(
    marker: [•],
    body-indent: 0.6em,
    [Diseñé y desplegué un servicio permanente contenedorizado con Docker para análisis concurrente de fuentes con alertas automáticas vía Telegram en menos de 1 segundo.],
    [Implementé un BackgroundService tolerante a fallos con reconexión automática ante caídas de red y endpoints de salud (/health) de supervisión continua.],
    [Optimicé la capa de persistencia en SQLite con EF Core aplicando índices únicos anti-duplicados y un algoritmo de reciclaje de IDs eliminados.]
  )
]

#v(0.3em)
#block[
  #grid(
    columns: (1fr, auto),
    [
      #text(weight: ""bold"", size: 1.03em, fill: primary)[Pdf_Signer] #text(size: 0.95em, fill: muted)[ — Manipulación y Firma Digital de Documentos] \
      #v(-2pt)
      #text(size: 0.9em, style: ""italic"", fill: accent)[C\# .NET | WPF | MVVM | Docker (Testing) | PdfiumViewer | PdfPig]
    ],
    [
      #text(size: 0.93em)[#link(""https://github.com/idarkar3000/Pdf_Signer"")[github.com/.../Pdf_Signer]]
    ]
  )
  #v(0.12em)
  #list(
    marker: [•],
    body-indent: 0.6em,
    [Desarrollé una aplicación de escritorio nativa en WPF bajo arquitectura MVVM para visualización fluida de ficheros PDF de alta densidad.],
    [Implementé el parseo vectorial de documentos y el estampado interactivo de firmas digitales optimizando el flujo de memoria en el procesamiento de bytes.]
  )
]

// --- HABILIDADES TÉCNICAS (DISEÑO HORIZONTAL NATURAL) ---
#section-heading(""Habilidades Técnicas"")

#set list(marker: [•], body-indent: 0.5em)
#v(0.1em)

- #text(weight: ""bold"", fill: primary)[Lenguajes & Backend:] C\#, \.NET / \.NET Core, ASP\.NET Core (Web APIs, Minimal APIs), LINQ, Async/Await, WPF / XAML.
- #text(weight: ""bold"", fill: primary)[Frontend & Web:] Angular, JavaScript, HTML5, CSS3, integración de APIs REST y políticas CORS.
- #text(weight: ""bold"", fill: primary)[Arquitectura & Datos:] Microservicios, CQRS, Inyección de Dependencias, SQL Server, IBM Informix, SQLite, EF Core, Dapper.
- #text(weight: ""bold"", fill: primary)[DevOps & Herramientas:] Docker, Azure DevOps (CI/CD Pipelines), Git, GitHub, Swagger / OpenAPI, xUnit, Postman, Render.
- #text(weight: ""bold"", fill: primary)[Metodologías & Competencias:] Scrum / Agile, trabajo en equipo técnico, resolución analítica y aprendizaje continuo.

// --- FORMACIÓN ACADÉMICA E IDIOMAS ---
#grid(
  columns: (1fr, 130pt),
  gutter: 18pt,
  [
    #section-heading(""Formación Académica"")
    #grid(
      columns: (1fr, auto),
      [
        #text(weight: ""bold"", fill: primary)[Ingeniería en Diseño y Desarrollo de Videojuegos] \
        #text(size: 0.94em, fill: muted)[Universidad Rey Juan Carlos (URJC)]
      ],
      [
        #text(size: 0.94em, fill: muted)[2022 - 2026]
      ]
    )
    #v(0.2em)
    #grid(
      columns: (1fr, auto),
      [
        #text(weight: ""bold"", fill: primary)[Técnico Superior en Animación 3D, Juegos y E.I.] \
        #text(size: 0.94em, fill: muted)[Premio Extraordinario / Excelencia Académica]
      ],
      [
        #text(size: 0.94em, fill: muted)[2019 - 2021]
      ]
    )
  ],
  [
    #section-heading(""Idiomas"")
    #v(0.1em)
    - *Español:* Nativo
    - *Inglés:* B2 (TOEIC 805 / 990)
  ]
)
";

        return template
            .Replace("__FONT_SIZE__", sizeStr)
            .Replace("__SUMMARY__", summary)
            .Replace("__BULLETS__", experienceBullets.ToString());
    }

    private async Task<bool> CompileTypstAsync(string typstFile, string pdfFile, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "typst",
                Arguments = $"compile \"{typstFile}\" \"{pdfFile}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdOutTask, stdErrTask);

            if (process.ExitCode == 0 && File.Exists(pdfFile))
            {
                return true;
            }

            _logger.LogError(
                "Error compilando Typst (exit code {Code}): {Error}",
                process.ExitCode, stdErrTask.Result);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se encontró 'typst' en el PATH del sistema.");
            return false;
        }
    }

    private int CountPdfPages(string pdfFile)
    {
        try
        {
            using var document = PdfDocument.Open(pdfFile);
            return document.NumberOfPages;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo contar las páginas del PDF generado ({File}).", Path.GetFileName(pdfFile));
            return 1;
        }
    }

    private static string PrepareText(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = text
            .Trim('[', ']', ' ', '"', '\r', '\n', '•', '-')
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("\r", " ");

        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim();

        var truncated = TruncateAtWordBoundary(normalized, maxChars);

        return EscapeTypst(truncated);
    }

    private static string TruncateAtWordBoundary(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        var cut = text[..maxChars];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > maxChars / 2)
        {
            cut = cut[..lastSpace];
        }

        return cut.TrimEnd('.', ',', ';', ' ') + "…";
    }

    private static string EscapeTypst(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '#': sb.Append(@"\#"); break;
                case '$': sb.Append(@"\$"); break;
                case '_': sb.Append(@"\_"); break;
                case '*': sb.Append(@"\*"); break;
                case '`': sb.Append(@"\`"); break;
                case '<': sb.Append(@"\<"); break;
                case '>': sb.Append(@"\>"); break;
                case '[': sb.Append(@"\["); break;
                case ']': sb.Append(@"\]"); break;
                case '@': sb.Append(@"\@"); break;
                case '~': sb.Append(@"\~"); break;
                case '^': sb.Append(@"\^"); break;
                case '\'': sb.Append(@"\'"); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = new string(Path.GetInvalidFileNameChars()) + " /\\:*?\"<>|.,&;";
        var escaped = Regex.Replace(input, "[" + Regex.Escape(invalid) + "]+", "_");
        return escaped.Trim('_').Length > 25 ? escaped.Trim('_')[..25] : escaped.Trim('_');
    }
}