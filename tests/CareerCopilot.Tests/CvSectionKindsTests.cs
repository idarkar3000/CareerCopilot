using CareerCopilot.Models;
using FluentAssertions;

namespace CareerCopilot.Tests
{
    public class CvSectionKindsTests
    {
        [Theory]
        [InlineData("entries", CvSectionKinds.Entries)]
        [InlineData("entry", CvSectionKinds.Entries)]
        [InlineData("experience", CvSectionKinds.Entries)]
        [InlineData("proyectos", CvSectionKinds.Entries)]
        [InlineData("  ENTRIES  ", CvSectionKinds.Entries)]
        [InlineData("texts", CvSectionKinds.Texts)]
        [InlineData("loquesea", CvSectionKinds.Texts)]
        [InlineData(null, CvSectionKinds.Texts)]
        public void Normalize_DebeMapearElTipo(string? kind, string esperado)
        {
            CvSectionKinds.Normalize(kind).Should().Be(esperado);
        }
    }
}
