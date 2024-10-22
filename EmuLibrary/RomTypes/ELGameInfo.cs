using EmuLibrary.Settings;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace EmuLibrary.RomTypes
{
    [ProtoContract]
    internal abstract class ELGameInfo
    {
        public abstract RomType RomType { get; }

        [ProtoMember(1)]
        public Guid MappingId { get; set; }

        public EmulatorMapping Mapping
        {
            get
            {
                return Settings.Settings.Instance.Mappings.FirstOrDefault(m => m.MappingId == MappingId);
            }
        }

        public string AsGameId()
        {
            using (var ms = new MemoryStream())
            {
                Serializer.Serialize(ms, this);
                return string.Format("!0{0}", Convert.ToBase64String(ms.ToArray()));
            }
        }
        
        // Format:
        // Exclamation point (!) followed by version (char), followed by base64 encoded, ProtoBuf serialized ELGameInfo

        public static T FromGame<T>(Game game) where T : ELGameInfo
        {
            return FromGameIdString<T>(game.GameId);
        }

        public static T FromGameMetadata<T>(GameMetadata game) where T : ELGameInfo
        {
            return FromGameIdString<T>(game.GameId);
        }

        private static T FromGameIdString<T>(string gameId) where T : ELGameInfo
        {
            Debug.Assert(gameId != null, "GameId is null");
            Debug.Assert(gameId.Length > 0, "GameId is empty");
            Debug.Assert(gameId[0] == '!', "GameId is not in expected format. (Legacy game that didn't get converted?)");
            Debug.Assert(gameId.Length > 2, $"GameId is too short ({gameId.Length} chars)");
            Debug.Assert(gameId[1] == '0', $"GameId is marked as being serialized ProtoBuf, but of invalid version. (Expected 0, got {gameId[1]})");

            return Serializer.Deserialize<T>(Convert.FromBase64String(gameId.Substring(2)).AsSpan());
        }

        public abstract InstallController GetInstallController(Game game, IEmuLibrary emuLibrary);
        public abstract UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary);

        protected abstract IEnumerable<string> GetDescriptionLines();

        public string ToDescriptiveString(Game g)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Game: {g.Name}");
            sb.AppendLine($"Type: {GetType()}");
            sb.AppendLine($"{nameof(RomType)}: {RomType} ({(int)RomType})");
            sb.AppendLine($"{nameof(MappingId)}: {MappingId}");

            GetDescriptionLines().ForEach(l => sb.AppendLine(l));

            var mapping = Mapping;
            if (mapping != null)
            {
                sb.AppendLine();
                sb.AppendLine("Mapping Info:");
                mapping.GetDescriptionLines().ForEach(l => sb.AppendLine($"    {l}"));
            }

            return sb.ToString();
        }

        public abstract void BrowseToSource();
    }
}
