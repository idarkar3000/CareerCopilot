using CareerCopilot.Services;
using FluentAssertions;

namespace CareerCopilot.Tests
{
    public class CandidateProfileProviderTests
    {
        [Theory]
        [InlineData("C#", "c")]
        [InlineData(".NET", "net")]
        [InlineData("ASP.NET Core", "aspnet core")]
        [InlineData("Node.js", "nodejs")]
        [InlineData("C++", "c")]
        [InlineData("  SQL  ", "sql")]
        public void Normalize_DebeQuitarPuntuacionTecnica(string entrada, string esperado)
        {
            CandidateProfileProvider.Normalize(entrada).Should().Be(esperado);
        }

        [Fact]
        public void Normalize_DebeIgnorarTildes()
        {
            CandidateProfileProvider.Normalize("Programación").Should().Be("programacion");
        }
    }
}
