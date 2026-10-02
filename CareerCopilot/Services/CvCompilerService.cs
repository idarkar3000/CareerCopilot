using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CareerCopilot.Models;
using UglyToad.PdfPig;

namespace CareerCopilot.Services;

/// <summary>
/// Maqueta en Typst el documento que devuelve Gemini. No fija ningún contenido: solo coloca las
/// secciones generadas. Para que quepa en una página A4 primero recorta por prioridad y luego
/// baja el cuerpo de letra.
/// </summary>
public class CvCompilerService
{
    private readonly ILogger<CvCompilerService> _logger;
    private readonly BotConfig _config;
    private readonly CandidateProfileProvider _profile;

    private const int MaxSummaryChars = 340;   // tres líneas; el prompt pide 200-280 y esto es el seguro
    private const int MaxBulletChars = 240;    // 2 líneas
    private const int MaxLineChars = 240;      // línea tipo "Lenguajes: ..."
    private const int MaxFieldChars = 90;      // título, empresa, fechas, stack
    private const int MaxBulletsDefault = 5;

    /// <summary>
    /// Niveles de ajuste. El primero no recorta nada y cada uno siguiente quita las secciones con
    /// prioridad igual o superior a la indicada. Los cuatro primeros solo bajan el cuerpo de letra,
    /// así que Idiomas no se sacrifica hasta 8.1pt. Después ya se quita contenido, y la experiencia
    /// mantiene sus viñetas hasta el penúltimo nivel.
    /// </summary>
    private static readonly (decimal FontSize, int DropPriorityAtOrAbove, int MaxBullets, int ExperienceBullets)[] FitLadder =
    {
        (9.0m, 0, 0, 5),
        (8.7m, 0, 0, 5),
        (8.4m, 0, 0, 5),
        (8.1m, 0, 0, 5),
        (8.1m, 3, 0, 5),
        (8.1m, 2, 4, 5),
        (7.8m, 2, 3, 4)
    };

    /// <summary>
    /// Concepto de cada sección y orden en que se maquetan. La clave es lo que se busca dentro del
    /// título normalizado, así que "EXPERIENCIA LABORAL (EPAM)" y "Experiencia Profesional" caen
    /// en la misma entrada. También fija la cabecera canónica que se imprime.
    /// </summary>
    private static readonly (string Needle, int Rank, string Heading)[] SectionOrder =
    {
        ("experienc", 0, "Experiencia Laboral"),
        ("proyect", 1, "Proyectos"),
        ("habilidad", 2, "Habilidades Técnicas"),
        ("tecnolog", 2, "Tecnologías"),
        ("conoc", 2, "Conocimientos"),
        ("formaci", 3, "Formación Académica"),
        ("estudi", 3, "Formación Académica"),
        ("titul", 3, "Formación Académica"),
        ("certific", 3, "Certificaciones"),
        ("idioma", 4, "Idiomas"),
        ("lengua", 4, "Idiomas")
    };

    public CvCompilerService(
        ILogger<CvCompilerService> logger,
        BotConfig config,
        CandidateProfileProvider profile)
    {
        _logger = logger;
        _config = config;
        _profile = profile;
    }

    public async Task<string?> GeneratePdfAsync(JobOffer job, EvaluationResult eval, CancellationToken ct)
    {
        if (eval.Cv == null || eval.Cv.Sections.Count == 0)
        {
            _logger.LogError("No hay documento de CV que compilar para '{Title}'.", job.Title);
            return null;
        }

        var outputDir = ResolveOutputDir();
        Directory.CreateDirectory(outputDir);

        // Fusionar antes de aplicar las anclas: si no, la experiencia que llega del perfil se
        // pegaría a la primera de las secciones repetidas y el resto se quedaría sin imprimir.
        CoalesceSections(eval.Cv);
        ApplyAnchors(eval.Cv, job);
        OrderSections(eval.Cv);

        var candidate = _config.Candidate;
        var baseFileName = BuildFileName(job, candidate);
        var typstFile = Path.Combine(outputDir, $"{baseFileName}.typ");
        var pdfFile = Path.Combine(outputDir, $"{baseFileName}.pdf");

        // Los .typ de intentos anteriores se borran, solo vale el PDF final
        foreach (var stale in Directory.EnumerateFiles(outputDir, $"{baseFileName}.*.pdf"))
        {
            TryDelete(stale);
        }

        string? lastAttemptPdf = null;

        for (var i = 0; i < FitLadder.Length; i++)
        {
            var (fontSize, dropPriorityAtOrAbove, maxBullets, experienceBullets) = FitLadder[i];

            var sections = ApplyTrim(eval.Cv.Sections, dropPriorityAtOrAbove, maxBullets, experienceBullets);
            var typstContent = BuildTypstContent(eval.Cv, sections, fontSize);

            await File.WriteAllTextAsync(
                typstFile,
                typstContent,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ct);

            if (!await CompileTypstAsync(typstFile, pdfFile, ct))
            {
                return null;
            }

            lastAttemptPdf = pdfFile;
            var pageCount = CountPdfPages(pdfFile);

            if (pageCount <= 1)
            {
                if (i > 0)
                {
                    _logger.LogInformation(
                        "CV ajustado en el intento {Attempt}/{Total}: {Size}pt{Recorte}.",
                        i + 1, FitLadder.Length, fontSize, DescribeTrim(dropPriorityAtOrAbove, maxBullets, experienceBullets));
                }
                return pdfFile;
            }

            _logger.LogWarning(
                "CV compilado con {Pages} páginas (intento {Attempt}/{Total}: {Size}pt{Recorte}); reajustando.",
                pageCount, i + 1, FitLadder.Length, fontSize, DescribeTrim(dropPriorityAtOrAbove, maxBullets, experienceBullets));
        }

        _logger.LogError(
            "El CV de '{Title}' no cabe en una sola página A4 ni con el recorte y cuerpo de letra mínimos. No se envía PDF.",
            job.Title);

        if (!string.IsNullOrEmpty(lastAttemptPdf)) TryDelete(lastAttemptPdf);
        return null;
    }

    private string ResolveOutputDir()
    {
        var configured = _config.Candidate.OutputDir;
        if (string.IsNullOrWhiteSpace(configured)) configured = "GeneratedCVs";
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    private string BuildFileName(JobOffer job, CandidateConfig candidate)
    {
        var template = string.IsNullOrWhiteSpace(candidate.PdfFileNameTemplate)
            ? "{job}_{name}"
            : candidate.PdfFileNameTemplate;

        var namePart = SanitizeFileName(candidate.FullName, 40);
        var jobPart = SanitizeFileName(job.Title, 45);

        // Sin puesto no hay nada que nombrar: un identificador único evita que dos archivos
        // distintos acaben con el mismo nombre. Ojo, el identificador son 35 caracteres, así que
        // aquí no se puede recortar a 40.
        if (string.IsNullOrWhiteSpace(jobPart))
        {
            _logger.LogWarning(
                "El título de la oferta está vacío; el PDF se nombra con un identificador único.");

            return $"CV_{Guid.NewGuid():N}";
        }

        // Sin nombre el fichero queda con el puesto y un aviso. Es determinista a propósito: dos
        // ofertas del mismo puesto se sobrescriben, pero nunca se acumulan CV sin identificar.
        if (string.IsNullOrWhiteSpace(namePart))
        {
            _logger.LogWarning(
                "Falta BotConfig__Candidate__FullName; el PDF '{File}' se nombra solo con el puesto. Configura la variable en Render para incluir el nombre.",
                $"{jobPart}.pdf");
        }

        return SanitizeFileName(template.Replace("{job}", jobPart).Replace("{name}", namePart), 90);
    }

    /// <summary>
    /// Rellena el CV con los datos del bloque de anclas del perfil (empresa, fechas, stack, enlace
    /// y descripción) para que no dependan de lo que conteste Gemini. Los proyectos que no estén
    /// en el perfil se quitan. Si no hay anclas, el documento se deja como viene.
    /// </summary>
    private void ApplyAnchors(CvDocument cv, JobOffer job)
    {
        var anchors = _profile.Anchors;
        if (anchors.IsEmpty) return;

        cv.Sections ??= new List<CvSection>();

        if (anchors.Experience.Count > 0)
        {
            var experience = FindSection(cv.Sections, "experienc");
            if (experience is null)
            {
                experience = new CvSection { Heading = "Experiencia Laboral", Kind = "entries", Priority = 1 };
                cv.Sections.Insert(0, experience);
                _logger.LogInformation("El modelo no devolvió sección de experiencia; se genera desde las anclas del perfil.");
            }

            MergeAnchored(experience, anchors.Experience, keepUnmatched: true, job, BulletLimit("experienc"));
        }

        if (anchors.Projects.Count > 0)
        {
            var projects = FindSection(cv.Sections, "proyect");
            if (projects is null)
            {
                projects = new CvSection { Heading = "Proyectos", Kind = "entries", Priority = 2 };
                cv.Sections.Add(projects);
                _logger.LogInformation("El modelo no devolvió sección de proyectos; se genera desde las anclas del perfil.");
            }

            MergeAnchored(projects, anchors.Projects, keepUnmatched: false, job, BulletLimit("proyect"));
        }

        MergeProfileSection(cv, "habilidad", anchors.Profile.Skills, "Habilidades Técnicas", 1);
        MergeProfileSection(cv, "formaci", anchors.Profile.Education, "Formación Académica", 2);
        MergeProfileSection(cv, "idioma", anchors.Profile.Languages, "Idiomas", 3);
    }

    /// <summary>
    /// Asegura que habilidades, formación e idiomas estén aunque el modelo no los mencione. Si la
    /// sección existe, solo se añaden las líneas cuya etiqueta falta; si no, se crea desde el perfil.
    /// Es lo mismo que hace LocalCvBuilder, para que el CV sea igual de completo llegue de donde llegue.
    /// </summary>
    private void MergeProfileSection(CvDocument cv, string headingNeedle, List<CvAnchorLine> lines, string heading, int priority)
    {
        if (lines.Count == 0) return;

        var existing = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Label) && !string.IsNullOrWhiteSpace(l.Text))
            .ToList();
        if (existing.Count == 0) return;

        cv.Sections ??= new List<CvSection>();

        var section = FindTextSection(cv.Sections, headingNeedle);
        if (section is null)
        {
            section = new CvSection { Heading = heading, Kind = CvSectionKinds.Texts, Priority = priority };
            cv.Sections.Add(section);
            _logger.LogInformation("El modelo no devolvió '{Heading}'; se genera desde las anclas del perfil.", heading);
        }

        section.Items ??= new List<CvItem>();
        foreach (var line in existing)
        {
            // Si la sección ya tiene la línea —con el nombre que puso el modelo— no se añade otra
            if (section.Items.Any(i => LabelsMatch(i.Label, line.Label))) continue;
            section.Items.Add(new CvItem { Label = line.Label, Text = line.Text });
        }
    }

    /// <summary>Escoge cuántas viñetas lleva cada tipo de bloque.</summary>
    private int BulletLimit(string headingNeedle)
    {
        var value = headingNeedle switch
        {
            "experienc" => _config.ExperienceBullets,
            "proyect" => _config.ProjectBullets,
            _ => 0
        };

        return value > 0 ? value : MaxBulletsDefault;
    }

    /// <summary>
    /// El modelo a veces devuelve la misma sección repetida ("Experiencia Laboral" cuatro veces)
    /// dejando las últimas sin contenido, y además las titula de mil maneras distintas
    /// ("Experiencia", "Experiencia Profesional", "EXPERIENCIA LABORAL (EPAM)"). Por eso se agrupa
    /// por concepto y no por texto: se queda con la primera del mismo concepto, le pone la cabecera
    /// canónica y absorbe los bloques con contenido de las demás, descartando los vacíos.
    /// </summary>
    private void CoalesceSections(CvDocument cv)
    {
        if (cv.Sections is null || cv.Sections.Count < 2) return;

        var kept = new List<CvSection>();
        var byConcept = new Dictionary<string, CvSection>(StringComparer.Ordinal);

        foreach (var section in cv.Sections)
        {
            var heading = section.Heading ?? string.Empty;
            if (CandidateProfileProvider.Normalize(heading).Length == 0) continue;

            var concept = ConceptOf(heading);
            var key = concept?.Needle ?? CandidateProfileProvider.Normalize(heading);

            if (!byConcept.TryGetValue(key, out var target))
            {
                byConcept[key] = section;
                kept.Add(section);
                continue;
            }

            target.Items ??= new List<CvItem>();
            var useful = section.Items?.Where(ItemHasContent).ToList() ?? [];
            foreach (var item in useful)
            {
                if (!target.Items.Any(existing => SameBlock(existing, item))) target.Items.Add(item);
            }

            _logger.LogInformation(
                "Sección repetida '{Heading}' del mismo grupo que '{Target}': fusionada, {Used} bloques útiles y {Empty} vacíos descartados.",
                section.Heading, target.Heading, useful.Count, (section.Items?.Count ?? 0) - useful.Count);
        }

        // Las secciones de entradas y proyectos tienen que maquetarse como bloques; si el modelo
        // las devolvió como lista de líneas, el texto se imprimiría sin viñetas ni empresa.
        foreach (var (needle, _, _) in SectionOrder.Where(s => s.Rank <= 1))
        {
            var section = kept.FirstOrDefault(s => ConceptOf(s.Heading ?? string.Empty)?.Needle == needle);
            if (section is not null && CvSectionKinds.Normalize(section.Kind) != CvSectionKinds.Entries)
            {
                _logger.LogInformation(
                    "Sección '{Heading}': el modelo la devolvió como '{Kind}' y se maqueta como entradas.",
                    section.Heading, section.Kind);

                section.Kind = CvSectionKinds.Entries;
            }
        }

        // Cabecera canónica para que "Experiencia Profesional" y "Experiencia Laboral" no salgan
        // con dos títulos distintos aunque sean dos secciones distintas.
        foreach (var section in kept)
        {
            var canonical = ConceptOf(section.Heading ?? string.Empty)?.Heading;
            if (!string.IsNullOrEmpty(canonical) && !string.Equals(section.Heading, canonical, StringComparison.Ordinal))
            {
                section.Heading = canonical;
            }
        }

        if (kept.Count != cv.Sections.Count) cv.Sections = kept;
    }

    /// <summary>Concepto al que pertenece un título de sección, o null si no es uno de los conocidos.</summary>
    private static (string Needle, int Rank, string Heading)? ConceptOf(string heading)
    {
        var normalized = CandidateProfileProvider.Normalize(heading);

        foreach (var concept in SectionOrder)
        {
            if (normalized.Contains(concept.Needle, StringComparison.Ordinal)) return concept;
        }

        return null;
    }

    /// <summary>
    /// Un bloque sirve si tiene algo escrito además del título. Un item que solo lleva "Desarrollador
    /// Backend" y nada más es el patrón que deja el modelo cuando repite la sección, así que no cuenta.
    /// </summary>
    private static bool ItemHasContent(CvItem item)
    {
        var written = new[] { item.Label, item.Text, item.Org, item.Dates, item.Stack, item.Url, item.UrlLabel }
            .Any(value => !string.IsNullOrWhiteSpace(value));

        return written || item.Bullets?.Any(b => !string.IsNullOrWhiteSpace(b)) == true;
    }

    /// <summary>Dos bloques son el mismo si dicen lo mismo del mismo sitio.</summary>
    private static bool SameBlock(CvItem left, CvItem right) =>
        TitlesMatch($"{left.Title} {left.Org}", $"{right.Title} {right.Org}");

    /// <summary>
    /// Fija el orden de las secciones. El modelo las devuelve en el orden que quiere, así que sin
    /// esto un CV puede acabar con los idiomas arriba. La experiencia manda.
    /// </summary>
    private void OrderSections(CvDocument cv)
    {
        if (cv.Sections is null || cv.Sections.Count < 2) return;

        var before = cv.Sections.ToList();

        cv.Sections = cv.Sections
            .Select((section, index) => new { section, index, rank = RankOf(section) })
            .OrderBy(x => x.rank)
            .ThenBy(x => x.index)
            .Select(x => x.section)
            .ToList();

        if (!before.SequenceEqual(cv.Sections))
        {
            _logger.LogInformation(
                "Secciones reordenadas para tratar la experiencia primero: {Order}.",
                string.Join(" > ", cv.Sections.Select(s => s.Heading)));
        }
    }

    private static int RankOf(CvSection section) => ConceptOf(section.Heading ?? string.Empty)?.Rank ?? 5;

    /// <summary>Como FindSection pero para secciones de líneas, que no son kind = entries.</summary>
    private static CvSection? FindTextSection(List<CvSection> sections, string headingNeedle)
    {
        foreach (var section in sections)
        {
            if (CvSectionKinds.Normalize(section.Kind) != CvSectionKinds.Texts) continue;

            var heading = CandidateProfileProvider.Normalize(section.Heading ?? string.Empty);
            if (heading.Contains(headingNeedle, StringComparison.Ordinal)) return section;
        }

        return null;
    }

    private static CvSection? FindSection(List<CvSection> sections, string headingNeedle)
    {
        foreach (var section in sections)
        {
            if (section.Kind is not null && !section.Kind.Equals("entries", StringComparison.OrdinalIgnoreCase)) continue;

            var heading = CandidateProfileProvider.Normalize(section.Heading ?? string.Empty);
            if (heading.Contains(headingNeedle, StringComparison.Ordinal)) return section;
        }

        return null;
    }

    /// <summary>
    /// Deja la sección en el orden de las anclas. Si el modelo no trajo un bloque equivalente se
    /// crea desde el ancla, así la experiencia y los proyectos del perfil salen siempre.
    /// </summary>
    private void MergeAnchored(CvSection section, List<CvAnchorEntry> anchors, bool keepUnmatched, JobOffer job, int bulletLimit)
    {
        var leftover = new List<CvItem>(section.Items ?? new List<CvItem>());
        var merged = new List<CvItem>(anchors.Count);

        foreach (var anchor in anchors)
        {
            // Solo se reutiliza el bloque del modelo si su título casa con el del perfil. Si no
            // casa se crea uno nuevo desde el ancla, así no se arrastran las viñetas de otro
            // proyecto a uno que no le corresponde
            var index = leftover.FindIndex(i => TitlesMatch(i.Title, anchor.Title));
            var item = index >= 0 ? leftover[index] : new CvItem();
            if (index >= 0) leftover.RemoveAt(index);

            item.Title = anchor.Title;
            if (string.IsNullOrWhiteSpace(item.Org)) item.Org = anchor.Org;
            if (string.IsNullOrWhiteSpace(item.Dates)) item.Dates = anchor.Dates;
            if (string.IsNullOrWhiteSpace(item.Stack)) item.Stack = anchor.Stack;

            // El enlace es un dato del perfil, no del modelo: si hay ancla, su URL y su texto
            // prevalecen siempre. Antes solo se aplicaba cuando el modelo no traia URL, y como
            // este siempre trae una, el texto del perfil se descartaba y se imprimia el suyo,
            // que llega inflado con el org y el stack pegados detras.
            if (!string.IsNullOrWhiteSpace(anchor.Url))
            {
                item.Url = anchor.Url;
                item.UrlLabel = string.IsNullOrWhiteSpace(anchor.UrlLabel) ? item.UrlLabel : anchor.UrlLabel;
                if (!string.IsNullOrWhiteSpace(item.UrlLabel)) item.FromProfile = true;
            }

            item.Bullets = BuildBullets(anchor, item, job, bulletLimit);
            merged.Add(item);
        }

        if (keepUnmatched) merged.AddRange(leftover);

        section.Items = merged;
        _logger.LogInformation(
            "Anclas aplicadas a '{Heading}': {Count} bloques ({Detail}).",
            section.Heading, merged.Count,
            string.Join(", ", merged.Select(i => $"{i.Title}: {(i.Bullets?.Count ?? 0)} viñetas, {(string.IsNullOrWhiteSpace(i.Url) ? "sin enlace" : "con enlace")}")));
    }

    /// <summary>
    /// Viñetas de un bloque. Se queda con las que traiga el modelo y, si no llegan al número
    /// pedido, las completa con los puntos del perfil. El fallback solo se usa si no queda ninguna.
    /// </summary>
    private List<string> BuildBullets(CvAnchorEntry anchor, CvItem item, JobOffer job, int limit)
    {
        var chosen = (item.Bullets ?? new List<string>())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .ToList();

        var pool = anchor.Points.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        foreach (var point in SelectPoints(pool, chosen, job, limit - chosen.Count))
        {
            chosen.Add(point);
        }

        if (chosen.Count == 0 && !string.IsNullOrWhiteSpace(anchor.Fallback))
        {
            chosen.Add(anchor.Fallback);
        }

        return chosen.Take(limit > 0 ? limit : chosen.Count).ToList();
    }

    /// <summary>
    /// Elige del fondo de puntos los que mejor encajen con la oferta: puntúa cada uno por las
    /// palabras que comparte con el título y la descripción y, a igualdad, se queda con el que va
    /// antes en el perfil.
    /// </summary>
    private static List<string> SelectPoints(List<string> pool, List<string> alreadyChosen, JobOffer job, int needed)
    {
        if (needed <= 0 || pool.Count == 0) return new List<string>();

        var offer = CandidateProfileProvider.Normalize($"{job.Title} {job.Description}");
        var offerWords = SignificantWords(offer);

        return pool
            .Where(point => !alreadyChosen.Any(chosen => SaysSameThing(chosen, point)))
            .Select((point, index) => new { point, index, score = OverlapScore(point, offerWords) })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(needed)
            .Select(x => x.point)
            .ToList();
    }

    /// <summary>Número de palabras con contenido que comparten el punto y la oferta.</summary>
    private static double OverlapScore(string point, HashSet<string> offerWords)
    {
        if (offerWords.Count == 0) return 0;

        var matched = new HashSet<string>(
            SignificantWords(CandidateProfileProvider.Normalize(point)).Where(offerWords.Contains),
            StringComparer.Ordinal);

        if (matched.Count == 0) return 0;

        // Las palabras técnicas ("vertical", "informix", "cqrs") pesan más que las comunes ("cliente")
        var weight = matched.Sum(word => word.Length >= 6 ? 1.5 : word.Length >= 4 ? 1.0 : 0.5);

        // Rompe los empates hacia delante: lo que más cuenta en un bloque va arriba
        return weight - point.Length / 10000.0;
    }

    /// <summary>Palabras sin valor para comparar: artículos, preposiciones y palabras de oferta.</summary>
    private static HashSet<string> SignificantWords(string normalized) =>
        Regex.Matches(normalized, "[a-z0-9+#]{3,}")
            .Select(m => m.Value)
            .Where(word => !NoiseWords.Contains(word))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Si dos frases cuentan lo mismo: una está dentro de la otra o comparten casi todo el texto.
    /// Sirve para no repetir lo que el modelo ya escribió.
    /// </summary>
    private static bool SaysSameThing(string left, string right)
    {
        var a = SignificantWords(CandidateProfileProvider.Normalize(left));
        var b = SignificantWords(CandidateProfileProvider.Normalize(right));
        if (a.Count == 0 || b.Count == 0) return false;

        var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);

        // Con menos de 4 palabras con contenido puede ser casualidad, así que no se compara
        if (small.Count < 4) return false;

        if (small.All(big.Contains)) return true;

        var shared = big.Intersect(small, StringComparer.Ordinal).Count();
        return shared / (double)small.Count >= 0.6;
    }

    /// <summary>Palabras sin valor para comparar: artículos, preposiciones y jerga de oferta.</summary>
    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "and", "api", "app", "anos", "base", "buscamos", "caso", "cliente", "clientes", "como",
        "con", "conocimiento", "datos", "de", "del", "desde", "dentro", "desarrollo", "donde",
        "cada", "entre", "equipo", "este", "esta", "esto", "experiencia", "gente", "grupo",
        "hacer", "hacia", "idea", "igual", "implementacion", "la", "las", "le", "los", "mas",
        "mayor", "mediante", "mejor", "mercado", "mismo", "multinacional", "negocio", "nivel",
        "nuestra", "nuestro", "oferta", "orden", "otro", "para", "pero", "persona", "poder", "por",
        "porque", "primer", "proyecto", "propio", "puesto", "punto", "que", "real", "saber",
        "salario", "sector", "sido", "sin", "sobre", "solo", "sistema", "sistemas", "trabajando",
        "trabajo", "trafico", "ultimo", "usted", "valor", "valores", "varios", "vez", "yo"
    };

    /// <summary>
    /// Compara títulos sin acentos, espacios ni signos. Deja que uno contenga al otro ("Nombre
    /// (detalle)" casa con "Nombre"), pero pide cuatro caracteres mínimo para que palabras cortas
    /// tipo "CV" no emparejen nada.
    /// </summary>
    private static bool TitlesMatch(string? left, string? right)
    {
        var a = MatchKey(left);
        var b = MatchKey(right);

        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Equals(b, StringComparison.Ordinal)) return true;

        var shortest = Math.Min(a.Length, b.Length);
        return shortest >= 4 &&
               (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    /// <summary>
    /// Igual que TitlesMatch pero para las líneas de texto (formación, idiomas, habilidades), donde
    /// el modelo escribe el nombre oficial completo y el perfil lo guarda abreviado: "Técnico
    /// Superior en Animación 3D y Videojuegos" frente a "Técnico Superior en Animación 3D, Juegos y
    /// Entornos Interactivos". Ahí la contención falla porque las dos cadenas divergen justo donde
    /// una acaba y la otra sigue, así que se comparan las palabras con contenido: si comparten la
    /// mayoría, son la misma línea y no se añade la del perfil por encima.
    /// </summary>
    private static bool LabelsMatch(string? left, string? right) =>
        TitlesMatch(left, right) || SaysSameThing(left ?? string.Empty, right ?? string.Empty);

    private static string MatchKey(string? value) =>
        new((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c))
            .Select(char.ToLowerInvariant)
            .ToArray());

    /// <summary>
    /// Cuántas viñetas caben en un bloque. La experiencia es la última en recortarse y los
    /// proyectos se quedan por debajo de su límite, así que al apretar sale antes información de
    /// los proyectos que del trabajo.
    /// </summary>
    private int BulletLimitFor(CvSection section, int maxBullets, int experienceBullets)
    {
        var heading = CandidateProfileProvider.Normalize(section.Heading ?? string.Empty);

        if (heading.Contains("experienc", StringComparison.Ordinal))
        {
            var limit = experienceBullets > 0 ? experienceBullets : _config.ExperienceBullets;
            return maxBullets > 0 ? Math.Max(maxBullets, limit) : limit;
        }

        if (heading.Contains("proyect", StringComparison.Ordinal))
        {
            var limit = _config.ProjectBullets > 0 ? _config.ProjectBullets : 3;
            return maxBullets > 0 ? Math.Min(maxBullets, limit) : limit;
        }

        return maxBullets > 0 ? maxBullets : MaxBulletsDefault;
    }

    /// <summary>
    /// Describe lo que recorta un peldaño. Los cuatro primeros solo bajan el cuerpo de letra, y sin
    /// esto el log parecía anunciar que se habían eliminado secciones cuando no se había quitado nada.
    /// </summary>
    private static string DescribeTrim(int dropPriorityAtOrAbove, int maxBullets, int experienceBullets)
    {
        var parts = new List<string>
        {
            dropPriorityAtOrAbove > 0
                ? $"secciones de prioridad >= {dropPriorityAtOrAbove} eliminadas"
                : "sin secciones eliminadas"
        };

        if (maxBullets > 0) parts.Add($"máximo {maxBullets} viñetas en proyectos y habilidades");

        // El primer peldaño deja la experiencia en MaxBulletsDefault; solo se avisa cuando baja
        if (experienceBullets > 0 && experienceBullets < MaxBulletsDefault)
        {
            parts.Add($"{experienceBullets} viñetas en la experiencia");
        }

        return ", " + string.Join(", ", parts);
    }

    private List<CvSection> ApplyTrim(List<CvSection> sections, int dropPriorityAtOrAbove, int maxBullets, int experienceBullets)
    {
        var result = new List<CvSection>();

        foreach (var section in sections)
        {
            if (dropPriorityAtOrAbove > 0 && section.Priority >= dropPriorityAtOrAbove) continue;

            var copy = new CvSection
            {
                Heading = section.Heading,
                Priority = section.Priority,
                Kind = section.Kind,
                Items = new List<CvItem>()
            };

            var limit = BulletLimitFor(section, maxBullets, experienceBullets);

            foreach (var item in section.Items)
            {
                var copyItem = new CvItem
                {
                    Label = item.Label,
                    Text = item.Text,
                    Title = item.Title,
                    Org = item.Org,
                    Dates = item.Dates,
                    Stack = item.Stack,
                    Url = item.Url,
                    UrlLabel = item.UrlLabel,
                    FromProfile = item.FromProfile,
                    Bullets = item.Bullets is null ? null : new List<string>(item.Bullets)
                };

                if (copyItem.Bullets is { Count: > 0 })
                {
                    if (copyItem.Bullets.Count > limit)
                    {
                        copyItem.Bullets = copyItem.Bullets.Take(limit).ToList();
                    }
                }

                copy.Items.Add(copyItem);
            }

            if (copy.Items.Count > 0) result.Add(copy);
        }

        return result;
    }

    private string BuildTypstContent(CvDocument doc, List<CvSection> sections, decimal fontSize)
    {
        var candidate = _config.Candidate;
        var body = new StringBuilder();

        // --- PERFIL PROFESIONAL ---
        if (!string.IsNullOrWhiteSpace(doc.Summary))
        {
            body.AppendLine("    #section-heading(\"Perfil Profesional\")");
            body.AppendLine("    #text(size: 1.02em)[" + PrepareText(doc.Summary, MaxSummaryChars) + "]");
        }

        // --- SECCIONES GENERADAS POR GEMINI ---
        foreach (var section in sections)
        {
            body.AppendLine("    #section-heading(" + TypstString(PrepareText(section.Heading, MaxFieldChars)) + ")");

            if (CvSectionKinds.Normalize(section.Kind) == CvSectionKinds.Entries)
            {
                foreach (var item in section.Items)
                {
                    body.AppendLine(BuildEntryBlock(item));
                }
            }
            else
            {
                foreach (var item in section.Items)
                {
                    body.AppendLine(BuildTextBlock(item));
                }
            }

            body.AppendLine("    #v(0.25em)");
        }

        // --- PIE DE PÁGINA (ciudad y disponibilidad vienen de la configuración) ---
        var footer = BuildFooterNote(candidate);
        if (!string.IsNullOrWhiteSpace(footer))
        {
            body.AppendLine("    #v(0.2em)");
            body.AppendLine("    #text(size: 0.85em, style: \"italic\", fill: muted)[" + footer + "]");
        }

        var fontSizeText = fontSize.ToString("0.0", CultureInfo.InvariantCulture);

        return $$"""
            #set page(
              paper: "a4",
              margin: (top: 1.5cm, bottom: 1.0cm, left: 1.4cm, right: 1.4cm)
            )

            #set text(
              font: ("Segoe UI", "Arial"),
              size: {{fontSizeText}}pt,
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
            {{BuildHeaderBlock(doc, candidate)}}

            #v(0.3em)

            {{body.ToString().TrimEnd()}}
            """;
    }

    private static string BuildEntryBlock(CvItem item)
    {
        var title = PrepareText(item.Title, MaxFieldChars);
        var org = PrepareText(item.Org, MaxFieldChars);
        var dates = PrepareText(item.Dates, MaxFieldChars);
        var stack = PrepareText(item.Stack, MaxFieldChars);

        var heading = string.IsNullOrWhiteSpace(org)
            ? "[" + title + "]"
            : "[" + title + " #text(weight: \"medium\", size: 0.95em, fill: muted)[ \\u{2014} " + org + "]]";

        var stackLine = string.IsNullOrWhiteSpace(stack)
            ? string.Empty
            : "\n    #v(-2pt)\n    #text(size: 0.88em, style: \"italic\", fill: accent)[" + stack + "]";

        var rightContent = dates;
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            // El texto del perfil se imprime entero aunque sea largo: es el dato correcto y
            // recortarlo dejaria "github.com/.../Pdf_Signer". El del modelo, que puede venir
            // inflado con el stack pegado, si pasa por el limite de campo.
            var label = string.IsNullOrWhiteSpace(item.UrlLabel)
                ? "github.com"
                : item.FromProfile
                    ? PrepareText(item.UrlLabel, int.MaxValue)
                    : PrepareText(item.UrlLabel, MaxFieldChars);

            var url = item.Url.Replace("\\", string.Empty).Replace("\"", string.Empty);
            var link = "#link(\"" + url + "\")[" + label + "]";
            rightContent = string.IsNullOrWhiteSpace(rightContent) ? link : rightContent + " #linebreak() " + link;
        }

        var rightCell = "[" + rightContent + "]";
        var hasRight = !string.IsNullOrWhiteSpace(rightContent);

        var bullets = new StringBuilder();
        if (item.Bullets is { Count: > 0 })
        {
            var prepared = item.Bullets
                .Select(b => PrepareText(b, MaxBulletChars))
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .ToList();

            if (prepared.Count > 0)
            {
                bullets.Append("\n    #v(0.1em)\n    #list(marker: [\\u{2022}], body-indent: 0.6em");
                foreach (var bullet in prepared)
                {
                    bullets.Append(",\n      [" + bullet + "]");
                }
                bullets.Append("\n    )");
            }
        }

        var grid = hasRight
            ? $"""
                #grid(
                    columns: (1fr, auto),
                    [
                      #text(weight: "bold", size: 1.06em, fill: primary){heading}
                    ],
                    [
                      #text(size: 0.92em, weight: "semibold", fill: muted){rightCell}
                    ]
                  )
                """
            : $"""
                #text(weight: "bold", size: 1.06em, fill: primary){heading}
                """;

        return $"""
                #block[
                  {grid}{stackLine}{bullets}
                ]
                """;
    }

    private static string BuildTextBlock(CvItem item)
    {
        var text = PrepareText(item.Text, MaxLineChars);
        var label = PrepareText(item.Label, MaxFieldChars);

        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.IsNullOrWhiteSpace(label) ? string.Empty : label;
            label = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var content = string.IsNullOrWhiteSpace(label)
            ? "[" + text + "]"
            : "[" + "*" + label + ":* " + text + "]";

        return "    #list(marker: [\\u{2022}], body-indent: 0.5em, " + content + ")";
    }

    /// <summary>
    /// Cabecera del CV. Se monta línea a línea porque el modelo devuelve a veces el titular vacío:
    /// si se imprimiera el bloque entero quedaría una línea en blanco de 19pt y el contacto se
    /// separaba del nombre sin motivo.
    /// </summary>
    private static string BuildHeaderBlock(CvDocument doc, CandidateConfig candidate)
    {
        var lines = new List<string>();
        var fullName = candidate.FullName.Trim();
        var headline = string.IsNullOrWhiteSpace(doc.Headline) ? candidate.Headline.Trim() : doc.Headline.Trim();
        var contact = BuildContactLine(candidate);

        if (fullName.Length > 0)
        {
            lines.Add($"      #text(19pt, weight: \"bold\", fill: primary)[{EscapeTypst(fullName.ToUpperInvariant())}]");
        }

        if (headline.Length > 0)
        {
            lines.Add($"      #v(2pt)");
            lines.Add($"      #text(10.5pt, weight: \"semibold\", fill: accent)[{EscapeTypst(headline)}]");
        }

        if (contact.Length > 0)
        {
            lines.Add($"      #v(3pt)");
            lines.Add($"      #text(8.5pt, fill: muted)[{contact}]");
        }

        return lines.Count == 0
            ? string.Empty
            : "    #align(center)[\n      " + string.Join("\n      ", lines) + "\n    ]";
    }

    private static string BuildContactLine(CandidateConfig candidate)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(candidate.Phone))
        {
            parts.Add(EscapeTypst(candidate.Phone.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(candidate.Email))
        {
            var email = EscapeTypst(candidate.Email.Trim());
            parts.Add($"#link(\"mailto:{candidate.Email.Trim()}\")[{email}]");
        }

        foreach (var link in candidate.Links ?? new List<ContactLink>())
        {
            if (string.IsNullOrWhiteSpace(link.Url) || string.IsNullOrWhiteSpace(link.Label)) continue;
            parts.Add($"#link(\"{link.Url.Trim()}\")[{EscapeTypst(link.Label.Trim())}]");
        }

        return string.Join(" #h(8pt) | #h(8pt) ", parts);
    }

    private static string BuildFooterNote(CandidateConfig candidate)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(candidate.City)) parts.Add(EscapeTypst(candidate.City.Trim()));
        if (!string.IsNullOrWhiteSpace(candidate.Availability)) parts.Add(EscapeTypst(candidate.Availability.Trim()));
        return string.Join(" \\u{00B7} ", parts);
    }

    /// <summary>Escapa el texto para poder escribirlo como cadena en Typst.</summary>
    private static string TypstString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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
            .Trim('[', ']', ' ', '"', '\r', '\n', '\u2022', '-')
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

        return cut.TrimEnd('.', ',', ';', ' ') + "\u2026";
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

    /// <summary>
    /// Limpia un texto para poder usarlo como nombre de fichero. Las tildes y la eñe se traducen
    /// a ASCII porque los nombres con acentos fallan al compartir por correo o al subirlos a
    /// sistemas de ficheros antiguos: "Adrián Espínola" se queda en "Adrian_Espinola".
    /// </summary>
    private static string SanitizeFileName(string input, int maxLength)
    {
        var invalid = new string(Path.GetInvalidFileNameChars()) + " /\\:*?\"<>|.,&;";
        var ascii = ToAscii(input ?? string.Empty);
        var escaped = Regex.Replace(ascii, "[" + Regex.Escape(invalid) + "]+", "_").Trim('_');
        return escaped.Length > maxLength ? escaped[..maxLength].Trim('_') : escaped;
    }

    /// <summary>
    /// Descompone los signos diacríticos y quita lo que no sea ASCII imprimible. Se hace en dos
    /// pasos porque quitar primero los caracteres no ASCII deja la base ya separada de las tildes.
    /// </summary>
    private static string ToAscii(string value)
    {
        var decomposed = value
            .Normalize(NormalizationForm.FormD)
            .Where(c => !CharUnicodeInfo.GetUnicodeCategory(c).Equals(UnicodeCategory.NonSpacingMark));

        var ascii = new string(decomposed.ToArray());
        var sb = new StringBuilder(ascii.Length);

        foreach (var c in ascii)
        {
            sb.Append(c is >= ' ' and <= '~' ? c : '_');
        }

        return sb.ToString();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo borrar el PDF sobrante '{File}'.", Path.GetFileName(path));
        }
    }
}