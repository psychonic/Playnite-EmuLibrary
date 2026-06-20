using EmuLibrary.Util.Metadata;
using Playnite.SDK.Models;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.Util.Metadata
{
    public class ExternalGameMetadataTests
    {
        private static string[] Names(System.Collections.Generic.IEnumerable<MetadataProperty> props) =>
            props.OfType<MetadataNameProperty>().Select(p => p.Name).ToArray();

        [Fact]
        public void ApplyTo_ShadowsFileDerivedName_WhenExternalHasName()
        {
            var game = new GameMetadata() { Name = "FILE NAME" };
            new ExternalGameMetadata() { Name = "Pretty Name" }.ApplyTo(game);
            Assert.Equal("Pretty Name", game.Name);
        }

        [Fact]
        public void ApplyTo_KeepsFileDerivedName_WhenExternalHasNoName()
        {
            var game = new GameMetadata() { Name = "FILE NAME" };
            new ExternalGameMetadata() { Description = "desc" }.ApplyTo(game);
            Assert.Equal("FILE NAME", game.Name);
            Assert.Equal("desc", game.Description);
        }

        [Fact]
        public void ApplyTo_FillsCollectionsAndReleaseDate()
        {
            var game = new GameMetadata();
            new ExternalGameMetadata()
            {
                Developers = new[] { "Dev A" },
                Publishers = new[] { "Pub B" },
                Genres = new[] { "Action", "RPG" },
                ReleaseDate = new ReleaseDate(2017, 3, 3),
            }.ApplyTo(game);

            Assert.Equal(new[] { "Dev A" }, Names(game.Developers));
            Assert.Equal(new[] { "Pub B" }, Names(game.Publishers));
            Assert.Equal(new[] { "Action", "RPG" }, Names(game.Genres).OrderBy(x => x).ToArray());
            Assert.Equal(new ReleaseDate(2017, 3, 3), game.ReleaseDate);
        }

        [Fact]
        public void Coalesce_TakesFirstNonEmptyPerField_HighestPriorityFirst()
        {
            var high = new ExternalGameMetadata() { Name = "High Name" };
            var low = new ExternalGameMetadata()
            {
                Name = "Low Name",
                Description = "Low Desc",
                Genres = new[] { "Action" },
            };

            var merged = ExternalGameMetadata.Coalesce(high, low);

            Assert.Equal("High Name", merged.Name);     // high wins where present
            Assert.Equal("Low Desc", merged.Description); // falls through to low
            Assert.Equal(new[] { "Action" }, merged.Genres.ToArray());
        }
    }
}
