using CareerCopilot.Models;
using CareerCopilot.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CareerCopilot.Tests
{
    public class CvSanitizerTests
    {
        private const string Profile = """
            # Perfil de prueba

            Backend junior con C# y .NET. Bases de datos SQL con SQL Server.
            """;

        [Fact]
        public void Sanitize_DebeDescartarTecnologiaNoRespaldada()
        {
            var sanitizer = CreateSanitizer();
            var document = new CvDocument
            {
                Summary = "Experiencia con Kubernetes gestionando clústeres.",
                Sections =
                [
                    new CvSection
                    {
                        Heading = "Habilidades Técnicas",
                        Kind = CvSectionKinds.Texts,
                        Items = [new CvItem { Text = "Kubernetes" }]
                    }
                ]
            };

            var violations = sanitizer.Sanitize(document);

            violations.Should().Contain("kubernetes");
            document.Summary.Should().BeEmpty();
            document.Sections.Should().BeEmpty();
        }

        [Fact]
        public void Sanitize_DebeMantenerTecnologiaDelPerfil()
        {
            var sanitizer = CreateSanitizer();
            var document = new CvDocument
            {
                Summary = "Desarrollador backend con C# y .NET.",
                Sections =
                [
                    new CvSection
                    {
                        Heading = "Habilidades Técnicas",
                        Kind = CvSectionKinds.Texts,
                        Items = [new CvItem { Label = "Bases de datos", Text = "SQL en SQL Server" }]
                    }
                ]
            };

            var violations = sanitizer.Sanitize(document);

            violations.Should().BeEmpty();
            document.Summary.Should().Be("Desarrollador backend con C# y .NET.");
            document.Sections.Should().HaveCount(1);
        }

        private static CvSanitizer CreateSanitizer()
        {
            var profilePath = Path.Combine(Path.GetTempPath(), $"careercopilot-profile-{Guid.NewGuid():N}.md");
            File.WriteAllText(profilePath, Profile);

            var config = new BotConfig { CandidateProfileFile = profilePath };
            var provider = new CandidateProfileProvider(config, NullLogger<CandidateProfileProvider>.Instance);

            return new CvSanitizer(provider, NullLogger<CvSanitizer>.Instance);
        }
    }
}
