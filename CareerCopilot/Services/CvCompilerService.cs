using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CareerCopilot.Models;
using UglyToad.PdfPig;

namespace CareerCopilot.Services
{
    public class CvCompilerService : ICvCompilerService
    {
        private readonly ILogger<CvCompilerService> _logger;
        private readonly BotConfig _config;
        private readonly ICandidateProfileProvider _profile;

        private const int MaxSummaryChars = 340;
        private const int MaxBulletChars = 240;
        private const int MaxLineChars = 240;
        private const int MaxFieldChars = 90;
        private const int MaxBulletsDefault = 5;

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
            ICandidateProfileProvider profile)
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

            CoalesceSections(eval.Cv);
            ApplyAnchors(eval.Cv, job);
            OrderSections(eval.Cv);

            var candidate = _config.Candidate;
            var baseFileName = BuildFileName(job, candidate);
            var typstFile = Path.Combine(outputDir, $"{baseFileName}.typ");
            var pdfFile = Path.Combine(outputDir, $"{baseFileName}.pdf");

            foreach (var stale in Directory.EnumerateFiles(outputDir, $"{baseFileName}.*.pdf"))
            {
                TryDelete(stale);
            }

            string? lastAttemptPdf = null;

            // Prueba el CV con varios tamaños de letra y recortes hasta que quepa en una sola página A4.
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

            if (string.IsNullOrWhiteSpace(jobPart))
            {
                _logger.LogWarning(
                    "El título de la oferta está vacío; el PDF se nombra con un identificador único.");

                return $"CV_{Guid.NewGuid():N}";
            }

            if (string.IsNullOrWhiteSpace(namePart))
            {
                _logger.LogWarning(
                    "Falta BotConfig__Candidate__FullName; el PDF '{File}' se nombra solo con el puesto. Configura la variable en Render para incluir el nombre.",
                    $"{jobPart}.pdf");
            }

            return SanitizeFileName(template.Replace("{job}", jobPart).Replace("{name}", namePart), 90);
        }

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
                if (section.Items.Any(i => LabelsMatch(i.Label, line.Label))) continue;
                section.Items.Add(new CvItem { Label = line.Label, Text = line.Text });
            }
        }

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

        private static (string Needle, int Rank, string Heading)? ConceptOf(string heading)
        {
            var normalized = CandidateProfileProvider.Normalize(heading);

            foreach (var concept in SectionOrder)
            {
                if (normalized.Contains(concept.Needle, StringComparison.Ordinal)) return concept;
            }

            return null;
        }

        private static bool ItemHasContent(CvItem item)
        {
            var written = new[] { item.Label, item.Text, item.Org, item.Dates, item.Stack, item.Url, item.UrlLabel }
                .Any(value => !string.IsNullOrWhiteSpace(value));

            return written || item.Bullets?.Any(b => !string.IsNullOrWhiteSpace(b)) == true;
        }

        private static bool SameBlock(CvItem left, CvItem right) =>
            TitlesMatch($"{left.Title} {left.Org}", $"{right.Title} {right.Org}");

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

        private void MergeAnchored(CvSection section, List<CvAnchorEntry> anchors, bool keepUnmatched, JobOffer job, int bulletLimit)
        {
            var leftover = new List<CvItem>(section.Items ?? new List<CvItem>());
            var merged = new List<CvItem>(anchors.Count);

            foreach (var anchor in anchors)
            {
                var index = leftover.FindIndex(i => TitlesMatch(i.Title, anchor.Title));
                var item = index >= 0 ? leftover[index] : new CvItem();
                if (index >= 0) leftover.RemoveAt(index);

                item.Title = anchor.Title;
                if (string.IsNullOrWhiteSpace(item.Org)) item.Org = anchor.Org;
                if (string.IsNullOrWhiteSpace(item.Dates)) item.Dates = anchor.Dates;
                if (string.IsNullOrWhiteSpace(item.Stack)) item.Stack = anchor.Stack;

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

        private static List<string> SelectPoints(List<string> pool, List<string> alreadyChosen, JobOffer job, int needed)
        {
            if (needed <= 0 || pool.Count == 0) return new List<string>();

            // Puntúa cada punto del perfil por las palabras que comparte con la oferta y se queda con los mejores.
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

        private static double OverlapScore(string point, HashSet<string> offerWords)
        {
            if (offerWords.Count == 0) return 0;

            var matched = new HashSet<string>(
                SignificantWords(CandidateProfileProvider.Normalize(point)).Where(offerWords.Contains),
                StringComparer.Ordinal);

            if (matched.Count == 0) return 0;

            var weight = matched.Sum(word => word.Length >= 6 ? 1.5 : word.Length >= 4 ? 1.0 : 0.5);

            return weight - point.Length / 10000.0;
        }

        private static HashSet<string> SignificantWords(string normalized) =>
            Regex.Matches(normalized, "[a-z0-9+#]{3,}")
                .Select(m => m.Value)
                .Where(word => !NoiseWords.Contains(word))
                .ToHashSet(StringComparer.Ordinal);

        private static bool SaysSameThing(string left, string right)
        {
            var a = SignificantWords(CandidateProfileProvider.Normalize(left));
            var b = SignificantWords(CandidateProfileProvider.Normalize(right));
            if (a.Count == 0 || b.Count == 0) return false;

            var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);

            if (small.Count < 4) return false;

            if (small.All(big.Contains)) return true;

            var shared = big.Intersect(small, StringComparer.Ordinal).Count();
            return shared / (double)small.Count >= 0.6;
        }

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

        private static bool LabelsMatch(string? left, string? right) =>
            TitlesMatch(left, right) || SaysSameThing(left ?? string.Empty, right ?? string.Empty);

        private static string MatchKey(string? value) =>
            new((value ?? string.Empty)
                .Where(c => char.IsLetterOrDigit(c))
                .Select(char.ToLowerInvariant)
                .ToArray());

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

        private static string DescribeTrim(int dropPriorityAtOrAbove, int maxBullets, int experienceBullets)
        {
            var parts = new List<string>
            {
                dropPriorityAtOrAbove > 0
                    ? $"secciones de prioridad >= {dropPriorityAtOrAbove} eliminadas"
                    : "sin secciones eliminadas"
            };

            if (maxBullets > 0) parts.Add($"máximo {maxBullets} viñetas en proyectos y habilidades");

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

            if (!string.IsNullOrWhiteSpace(doc.Summary))
            {
                body.AppendLine("    #section-heading(\"Perfil Profesional\")");
                body.AppendLine("    #text(size: 1.02em)[" + PrepareText(doc.Summary, MaxSummaryChars) + "]");
            }

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
                  margin: (top: 1.8cm, bottom: 1.0cm, left: 1.4cm, right: 1.4cm)
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
                  // 0.45em en vez de 0.7em: con seis secciones eso son ~1.5em de espacio muerto que
                  // ahora financia el margen superior sin empujar el cuerpo hacia abajo
                  v(0.45em)
                  text(1.12em, weight: "bold", fill: primary, upper(title))
                  v(-0.3em)
                  line(length: 100%, stroke: 0.6pt + rgb("#cbd5e1"))
                  v(0.35em)
                }

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

        private static string SanitizeFileName(string input, int maxLength)
        {
            var invalid = new string(Path.GetInvalidFileNameChars()) + " /\\:*?\"<>|.,&;";
            var ascii = ToAscii(input ?? string.Empty);
            var escaped = Regex.Replace(ascii, "[" + Regex.Escape(invalid) + "]+", "_").Trim('_');
            return escaped.Length > maxLength ? escaped[..maxLength].Trim('_') : escaped;
        }

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
}
