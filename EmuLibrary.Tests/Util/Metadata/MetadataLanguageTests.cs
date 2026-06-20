using EmuLibrary.Util.Metadata;
using Xunit;

namespace EmuLibrary.Tests.Util.Metadata
{
    public class MetadataLanguageTests
    {
        [Theory]
        [InlineData("en_US", "en")]
        [InlineData("pt_BR", "pt")]
        [InlineData("de-DE", "de")]
        [InlineData("ja", "ja")]
        [InlineData("", "en")]
        [InlineData(null, "en")]
        public void TwoLetter_ExtractsLanguageHead_DefaultsToEnglish(string input, string expected)
        {
            Assert.Equal(expected, MetadataLanguage.TwoLetter(input));
        }

        [Theory]
        [InlineData("en_US", "US.en")]
        [InlineData("de_DE", "DE.de")]
        [InlineData("ja_JP", "JP.ja")]
        [InlineData("pt_BR", "PT.pt")]
        public void TitleDbFile_MapsKnownLanguages(string input, string expected)
        {
            Assert.Equal(expected, MetadataLanguage.TitleDbFile(input));
        }

        [Theory]
        [InlineData("xx_XX")]
        [InlineData("tlh")] // Klingon: not a titledb language
        public void TitleDbFile_FallsBackToEnglish_ForUnknownLanguages(string input)
        {
            Assert.Equal(MetadataLanguage.DefaultTitleDbFile, MetadataLanguage.TitleDbFile(input));
        }

        [Theory]
        [InlineData("en_US", "EN")]
        [InlineData("ja_JP", "JA")]
        [InlineData(null, "EN")]
        public void GameTdbLocale_IsUppercaseTwoLetter(string input, string expected)
        {
            Assert.Equal(expected, MetadataLanguage.GameTdbLocale(input));
        }
    }
}
