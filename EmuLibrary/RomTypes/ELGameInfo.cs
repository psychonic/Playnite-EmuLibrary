using EmuLibrary.Settings;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using ProtoBuf.Meta;
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
        // Proto subtype field numbers are offset by this from the RomType enum value so they don't
        // collide with ELGameInfo's own [ProtoMember] numbers. This MUST stay stable forever:
        // existing users' serialized GameIds depend on it.
        internal const int ProtoSubTypeFieldOffset = 10;

        private static bool s_protoSubTypesRegistered;

        public abstract RomType RomType { get; }

        [ProtoMember(1)]
        public Guid MappingId { get; set; }

        // Registers each RomType's GameInfo as a protobuf subtype of ELGameInfo, keyed by
        // (RomType value + ProtoSubTypeFieldOffset). Idempotent so it can be called from both the
        // plugin at startup and from tests, guaranteeing both exercise identical numbering.
        internal static void RegisterProtoSubTypes()
        {
            if (s_protoSubTypesRegistered)
                return;

            foreach (var rt in Enum.GetValues(typeof(RomType)).Cast<RomType>())
            {
                var fieldInfo = typeof(RomType).GetField(rt.ToString());
                var romInfo = fieldInfo.GetCustomAttributes(false).OfType<RomTypeInfoAttribute>().FirstOrDefault();
                if (romInfo == null)
                    continue;

                RuntimeTypeModel.Default[typeof(ELGameInfo)].AddSubType((int)rt + ProtoSubTypeFieldOffset, romInfo.GameInfoType);
            }

            s_protoSubTypesRegistered = true;
        }

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
