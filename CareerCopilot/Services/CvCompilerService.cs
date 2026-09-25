using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CareerCopilot.Models;
using UglyToad.PdfPig;

namespace CareerCopilot.Services;

public class CvCompilerService
{
    private readonly ILogger<CvCompilerService> _logger;

    // Aumentamos el presupuesto de caracteres para que el texto sea más rico y llene el espacio
    private const int MaxSummaryChars = 850;      // Permite un párrafo contundente de 4-6 líneas
    private const int MaxBulletChars = 240;       // Permite viñetas explicativas de 2 líneas completas

    // Escala de tamaños de fuente adaptativa para 1 sola página A4
    private static readonly decimal[] FontSizeLadder = { 9.0m, 8.7m, 8.4m, 8.1m, 7.8m };

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
        var baseFileName = $"CV_{cleanJob}_{cleanCompany}_Adrian_Espinola";

        var typstFile = Path.Combine(outputDir, $"{baseFileName}.typ");
        var pdfFile = Path.Combine(outputDir, $"{baseFileName}.pdf");

        var summary = PrepareText(eval.TailoredSummary, MaxSummaryChars);
        var bullets = (eval.TailoredExperience ?? new List<string>())
            .Take(5) // Permitimos hasta 5 viñetas para EPAM Neoris
            .Select(b => PrepareText(b, MaxBulletChars))
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        string? lastAttemptPdf = null;

        for (var i = 0; i < FontSizeLadder.Length; i++)
        {
            var fontSize = FontSizeLadder[i];
            var typstContent = BuildTypstContent(summary, bullets, fontSize);

            await File.WriteAllTextAsync(typstFile, typstContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);

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
                "CV compilado con {Pages} páginas a {Size}pt (intento {Attempt}/{Total}); reajustando fuente.",
                pageCount, fontSize, i + 1, FontSizeLadder.Length);
        }

        return lastAttemptPdf;
    }

    private static string BuildTypstContent(string summary, List<string> bullets, decimal fontSize)
    {
        var experienceBullets = new StringBuilder();
        foreach (var bullet in bullets)
        {
            experienceBullets.AppendLine($"    [{bullet}],");
        }
        if (bullets.Count == 0) { experienceBullets.AppendLine("    [Desarrollo de microservicios backend con Minimal APIs, CQRS, Dapper y SQL Server durante el periodo de prácticas.],"); }
        return $$"""
#set page(
  paper: "a4",
  margin: (top: 1.0cm, bottom: 1.0cm, left: 1.4cm, right: 1.4cm)
)

#set text(
  font: ("Segoe UI", "Arial"),
  size: {{fontSize}}pt,
  fill: rgb("#111827"),
  lang: "es"
)

#set par(justify: true, leading: 0.68em)

#let primary = rgb("#0f172a")
#let accent = rgb("#1d4ed8")
#let muted = rgb("#475569")

#let section-heading(title) = {
  v(0.7em)
  text(1.12em, weight: "bold", fill: primary, upper(title))
  v(-0.3em)
  line(length: 100%, stroke: 0.6pt + rgb("#cbd5e1"))
  v(0.35em)
}

// --- CABECERA ---
#align(center)[
  #text(19pt, weight: "bold", fill: primary)[ADRIÁN ESPÍNOLA GUMIEL] \
  #v(2pt)
  #text(10.5pt, weight: "semibold", fill: accent)[Desarrollador Backend .NET / C\#] \
  #v(3pt)
  #text(8.5pt, fill: muted)[
    691 77 96 27 #h(8pt) | #h(8pt)
    #link("mailto:espinolagumieladrian@gmail.com")[espinolagumieladrian\@gmail.com] #h(8pt) | #h(8pt)
    #link("https://github.com/idarkar3000")[github.com/idarkar3000] #h(8pt) | #h(8pt)
    #link("https://linkedin.com/in/adrian-espinola-gumiel")[linkedin.com/in/adrian-espinola-gumiel]
  ]
]

#v(0.3em)

// --- SOBRE MÍ / PERFIL PROFESIONAL (ESPACIO AMPLIADO) ---
#section-heading("Perfil Profesional")
#text(size: 1.02em)[
  {{summary}}
]

// --- EXPERIENCIA LABORAL (EPAM NEORIS DESTACADA) ---
#section-heading("Experiencia Laboral")

#grid(
  columns: (1fr, auto),
  [
    #text(weight: "bold", size: 1.06em, fill: primary)[Desarrollador Backend .NET (Prácticas)] #text(weight: "medium", fill: muted)[ — EPAM Neoris]
  ],
  [
    #text(size: 0.95em, weight: "semibold", fill: muted)[Marzo 2026 – Junio 2026]
  ]
)
#v(0.25em)
#list(
  marker: [•],
  body-indent: 0.6em,
{{experienceBullets}}
)

// --- PROYECTOS DESTACADOS EN .NET ---
#section-heading("Proyectos Destacados en .NET")

#block[
  #grid(
    columns: (1fr, auto),
    [
      #text(weight: "bold", size: 1.02em, fill: primary)[RadarChollos] #text(size: 0.93em, fill: muted)[ — Bot de Monitorización y Alertas en Tiempo Real] \
      #v(-2pt)
      #text(size: 0.88em, style: "italic", fill: accent)[C\# .NET 9 | IHostedService | EF Core | SQLite | Docker | Telegram.Bot | Serilog | xUnit | FluentAssertions | Render]
    ],
    [
      #text(size: 0.9em)[#link("https://github.com/idarkar3000/RadarChollos")[github.com/.../RadarChollos]]
    ]
  )
  #v(0.12em)
  #list(
    marker: [•],
    body-indent: 0.6em,
    [Diseñé un servicio en segundo plano con IHostedService que rastrea periódicamente ofertas y envía alertas inmediatas por Telegram, expuesto junto a un endpoint de salud en ASP.NET Core.],
    [Implementé un motor de reglas configurable (EF Core + SQLite) con palabras clave, exclusiones, límites de precio y normalización de comercios como Amazon, PcComponentes o MediaMarkt.],
    [Desarrollé comandos interactivos de Telegram (/add, /list, /delete) para gestionar filtros en caliente sin reiniciar el servicio, restringidos a un usuario verificado.],
    [Empaqueté la app con Docker multi-stage, la desplegué en Render con keep-alive vía UptimeRobot y logging estructurado con Serilog; cubrí la lógica crítica con pruebas xUnit y FluentAssertions.]
  )
]

#v(0.25em)
#block[
  #grid(
    columns: (1fr, auto),
    [
      #text(weight: "bold", size: 1.02em, fill: primary)[Pdf_Signer] #text(size: 0.93em, fill: muted)[ — Manipulación y Firma Digital de Documentos] \
      #v(-2pt)
      #text(size: 0.88em, style: "italic", fill: accent)[C\# .NET 10 | WPF | PdfiumViewer | PdfPig]
    ],
    [
      #text(size: 0.9em)[#link("https://github.com/idarkar3000/Pdf_Signer")[github.com/.../Pdf_Signer]]
    ]
  )
  #v(0.12em)
  #list(
    marker: [•],
    body-indent: 0.6em,
    [Desarrollé una aplicación de escritorio nativa en WPF (.NET 10) con visor PDF interactivo: zoom, navegación de páginas y vista adaptable.],
    [Implementé un lienzo de firma manuscrita con modo borrador y grosor configurable, con estampado dinámico para arrastrar, reescalar y posicionar en tiempo real.],
    [Añadí Drag & Drop para abrir documentos y funcionalidad Deshacer/Rehacer (Ctrl+Z / Ctrl+Y) tanto en el dibujo de la firma como en su posicionamiento.],
    [Garanticé una exportación final vectorizada que preserva la integridad del documento y la transparencia de la firma, combinando PdfiumViewer y PdfPig.]
  )
]

// --- HABILIDADES TÉCNICAS ---
#section-heading("Habilidades Técnicas")

#set list(marker: [•], body-indent: 0.5em)
#v(0.1em)

- #text(weight: "bold", fill: primary)[Lenguajes & Frameworks:] C\#, \.NET / \.NET Core (9 y 10), ASP\.NET Core (Web APIs, Minimal APIs), LINQ, Async/Await, WPF / XAML.
- #text(weight: "bold", fill: primary)[Arquitectura & Patrones:] Microservicios, Vertical Slice Architecture, CQRS, IHostedService / Background Services, Inyección de Dependencias, JWT, FluentValidation, Middlewares globales.
- #text(weight: "bold", fill: primary)[Bases de Datos & ORMs:] IBM Informix, SQL Server, SQLite, Entity Framework Core, Dapper, Mapster (MapsterConfig), Optimización de Consultas e Índices.
- #text(weight: "bold", fill: primary)[Testing & Calidad:] xUnit, FluentAssertions, Swagger / OpenAPI, Postman.
- #text(weight: "bold", fill: primary)[DevOps & Herramientas:] Docker (builds multi-stage), Azure DevOps (CI/CD, Kanban, Git Flow, Pull Requests), Git, GitHub, Render, UptimeRobot, Telegram.Bot SDK, Serilog.
- #text(weight: "bold", fill: primary)[Metodologías & Competencias:] Scrum / Ágil, integración con Angular, herramientas de IA asistida para desarrollo, trabajo en equipo técnico y aprendizaje continuo.

// --- FORMACIÓN ACADÉMICA E IDIOMAS ---
#grid(
  columns: (1fr, 130pt),
  gutter: 18pt,
  [
    #section-heading("Formación Académica")
    #grid(
      columns: (1fr, auto),
      [
        #text(weight: "bold", fill: primary)[Ingeniería en Diseño y Desarrollo de Videojuegos] \
        #text(size: 0.94em, fill: muted)[Universidad Rey Juan Carlos (URJC)]
      ],
      [
        #text(size: 0.94em, fill: muted)[2022 – 2026]
      ]
    )
    #v(0.2em)
    #grid(
      columns: (1fr, auto),
      [
        #text(weight: "bold", fill: primary)[Técnico Superior en Animación 3D, Juegos y E.I.] \
        #text(size: 0.94em, fill: muted)[Premio Extraordinario / Excelencia Académica]
      ],
      [
        #text(size: 0.94em, fill: muted)[2019 – 2021]
      ]
    )
  ],
  [
    #section-heading("Idiomas")
    #v(0.1em)
    - *Español:* Nativo
    - *Inglés:* B2 (TOEIC 805 / 990)
  ]
)
""";
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
            _logger.LogWarning(ex, "No se pudo contar las páginas del PDF ({File}).", Path.GetFileName(pdfFile));
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