using EmuLibrary.RomTypes;
using EmuLibrary.RomTypes.MultiFile;
using EmuLibrary.RomTypes.Ps3;
using EmuLibrary.RomTypes.SingleFile;
using EmuLibrary.RomTypes.Yuzu;
using Playnite.SDK.Models;
using ProtoBuf.Meta;
using System;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.RomTypes
{
    // Guards the permanent-numbering invariant: an ELGameInfo is protobuf-serialized, base64-encoded,
    // and stored verbatim as a Playnite Game.GameId. If a [ProtoMember] number, a RomType enum value,
    // or the proto subtype offset ever changes incompatibly, existing users' saved GameIds stop
    // deserializing. These tests fail loudly if that happens.
    public class ELGameInfoSerializationTests
    {
        // Fixed so golden GameIds below are deterministic across runs/machines.
        private static readonly Guid FixedMappingId = new Guid("11111111-1111-1111-1111-111111111111");

        public ELGameInfoSerializationTests()
        {
            ELGameInfo.RegisterProtoSubTypes();
        }

        private static T RoundTrip<T>(T info) where T : ELGameInfo
        {
            var gameId = info.AsGameId();
            Assert.StartsWith("!0", gameId);
            // Deserialize as the *base* type (as the plugin does) to prove subtype registration recovers
            // the concrete type from the wire tag.
            return Assert.IsType<T>(ELGameInfo.FromGame<ELGameInfo>(new Game { GameId = gameId }));
        }

        [Fact]
        public void SingleFile_RoundTrips()
        {
            var info = new SingleFileGameInfo { MappingId = FixedMappingId, SourcePath = @"subdir\Some Game (USA).zip" };
            var result = RoundTrip(info);

            Assert.Equal(RomType.SingleFile, result.RomType);
            Assert.Equal(info.MappingId, result.MappingId);
            Assert.Equal(info.SourcePath, result.SourcePath);
        }

        [Fact]
        public void MultiFile_RoundTrips()
        {
            var info = new MultiFileGameInfo
            {
                MappingId = FixedMappingId,
                SourceFilePath = @"Some Game\disc.cue",
                SourceBaseDir = "Some Game",
            };
            var result = RoundTrip(info);

            Assert.Equal(RomType.MultiFile, result.RomType);
            Assert.Equal(info.MappingId, result.MappingId);
            Assert.Equal(info.SourceFilePath, result.SourceFilePath);
            Assert.Equal(info.SourceBaseDir, result.SourceBaseDir);
        }

        [Fact]
        public void Yuzu_RoundTrips()
        {
            var info = new YuzuGameInfo { MappingId = FixedMappingId, TitleId = 0x0100000000010000UL };
            var result = RoundTrip(info);

            Assert.Equal(RomType.Yuzu, result.RomType);
            Assert.Equal(info.MappingId, result.MappingId);
            Assert.Equal(info.TitleId, result.TitleId);
        }

        [Fact]
        public void Ps3_RoundTrips()
        {
            var info = new Ps3GameInfo
            {
                MappingId = FixedMappingId,
                TitleId = "BLES01234",
                SourceFolder = @"BLES01234 - Demon's Souls",
                BaseKind = Ps3BaseKind.Disc,
            };
            var result = RoundTrip(info);

            Assert.Equal(RomType.Ps3, result.RomType);
            Assert.Equal(info.MappingId, result.MappingId);
            Assert.Equal(info.TitleId, result.TitleId);
            Assert.Equal(info.SourceFolder, result.SourceFolder);
            Assert.Equal(info.BaseKind, result.BaseKind);
        }

        // Golden GameId strings. Each encodes the exact bytes a current build produces for a fixed input.
        // If one of these changes, a serialization-affecting change was made and previously-saved
        // libraries may no longer deserialize. Update ONLY with a deliberate, migration-aware change.
        // (Methods stay public for xUnit but must not expose the internal ELGameInfo type in their
        //  signatures, hence the info is constructed inline rather than passed in.)
        [Fact]
        public void SingleFile_GoldenGameId_IsStable()
        {
            var info = new SingleFileGameInfo { MappingId = FixedMappingId, SourcePath = @"subdir\Some Game (USA).zip" };
            Assert.Equal("!0UhwKGnN1YmRpclxTb21lIEdhbWUgKFVTQSkuemlwChIJERERERERERERERERERERERE=", info.AsGameId());
        }

        [Fact]
        public void MultiFile_GoldenGameId_IsStable()
        {
            var info = new MultiFileGameInfo { MappingId = FixedMappingId, SourceFilePath = @"Some Game\disc.cue", SourceBaseDir = "Some Game" };
            Assert.Equal("!0Wh8KElNvbWUgR2FtZVxkaXNjLmN1ZRIJU29tZSBHYW1lChIJERERERERERERERERERERERE=", info.AsGameId());
        }

        [Fact]
        public void Yuzu_GoldenGameId_IsStable()
        {
            var info = new YuzuGameInfo { MappingId = FixedMappingId, TitleId = 0x0100000000010000UL };
            Assert.Equal("!0cgoIgICEgICAgIABChIJERERERERERERERERERERERE=", info.AsGameId());
        }

        [Fact]
        public void Ps3_GoldenGameId_IsStable()
        {
            var info = new Ps3GameInfo
            {
                MappingId = FixedMappingId,
                TitleId = "BLES01234",
                SourceFolder = @"BLES01234 - Demon's Souls",
                BaseKind = Ps3BaseKind.Disc,
            };
            Assert.Equal("!0YiYKCUJMRVMwMTIzNBIZQkxFUzAxMjM0IC0gRGVtb24ncyBTb3VscwoSCRERERERERERERERERERERER", info.AsGameId());
        }

        // Pins each GameInfo subtype to its exact proto field number (RomType value + 10). Catches both a
        // RomType enum renumber and a change to the offset, either of which breaks saved GameIds.
        [Theory]
        [InlineData(typeof(SingleFileGameInfo), 10)] // RomType.SingleFile (0) + 10
        [InlineData(typeof(MultiFileGameInfo), 11)]  // RomType.MultiFile  (1) + 10
        [InlineData(typeof(YuzuGameInfo), 14)]       // RomType.Yuzu       (4) + 10
        [InlineData(typeof(Ps3GameInfo), 12)]        // RomType.Ps3        (2) + 10
        public void ProtoSubType_HasExpectedFieldNumber(Type gameInfoType, int expectedFieldNumber)
        {
            var subType = RuntimeTypeModel.Default[typeof(ELGameInfo)]
                .GetSubtypes()
                .SingleOrDefault(s => s.DerivedType.Type == gameInfoType);

            Assert.NotNull(subType);
            Assert.Equal(expectedFieldNumber, subType.FieldNumber);
        }

        // Every RomType must carry a [RomTypeInfo] attribute and be registered as a proto subtype at
        // (value + 10). Guards against adding a RomType but forgetting to wire it up.
        [Fact]
        public void EveryRomType_IsRegisteredAtOffsetTen()
        {
            var subTypes = RuntimeTypeModel.Default[typeof(ELGameInfo)].GetSubtypes();

            foreach (RomType rt in Enum.GetValues(typeof(RomType)))
            {
                var attr = typeof(RomType).GetField(rt.ToString())
                    .GetCustomAttributes(typeof(RomTypeInfoAttribute), false)
                    .Cast<RomTypeInfoAttribute>()
                    .SingleOrDefault();
                Assert.True(attr != null, $"RomType.{rt} is missing a {nameof(RomTypeInfoAttribute)}.");

                var subType = subTypes.SingleOrDefault(s => s.DerivedType.Type == attr.GameInfoType);
                Assert.True(subType != null, $"RomType.{rt} GameInfo is not registered as a proto subtype.");
                Assert.Equal((int)rt + ELGameInfo.ProtoSubTypeFieldOffset, subType.FieldNumber);
            }
        }
    }
}
