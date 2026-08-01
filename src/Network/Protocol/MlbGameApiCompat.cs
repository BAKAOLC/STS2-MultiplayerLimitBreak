using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal readonly record struct MlbLobbyPlayerData(
        ulong Id,
        int SlotId,
        CharacterModel Character,
        SerializableUnlockState UnlockState,
        int MaxMultiplayerAscensionUnlocked,
        object? VersionInfo,
        bool IsReady);

    internal static class MlbGameApiCompat
    {
        private const string NewPlayerTypeName =
            "MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer";
        private const string LegacyPlayerTypeName =
            "MegaCrit.Sts2.Core.Entities.Multiplayer.LobbyPlayer";

        private static readonly Type PlayerType =
            AccessTools.TypeByName(NewPlayerTypeName)
            ?? AccessTools.TypeByName(LegacyPlayerTypeName)
            ?? throw new TypeLoadException("Could not find the start-run lobby player type.");

        private static readonly FieldInfo PlayerIdField = RequirePlayerField("id");
        private static readonly FieldInfo PlayerSlotIdField = RequirePlayerField("slotId");
        private static readonly FieldInfo PlayerCharacterField = RequirePlayerField("character");
        private static readonly FieldInfo PlayerUnlockStateField = RequirePlayerField("unlockState");
        private static readonly FieldInfo PlayerMaxAscensionField =
            RequirePlayerField("maxMultiplayerAscensionUnlocked");
        private static readonly FieldInfo? PlayerVersionInfoField = AccessTools.Field(PlayerType, "versionInfo");
        private static readonly FieldInfo PlayerReadyField = RequirePlayerField("isReady");

        private static readonly PropertyInfo LobbyPlayersProperty =
            AccessTools.Property(typeof(StartRunLobby), nameof(StartRunLobby.Players))
            ?? throw new MissingMemberException(typeof(StartRunLobby).FullName, nameof(StartRunLobby.Players));

        private static readonly PropertyInfo JoinFlowNetServiceProperty =
            AccessTools.Property(typeof(JoinFlow), "NetService")
            ?? throw new MissingMemberException(typeof(JoinFlow).FullName, "NetService");

        private static readonly FieldInfo JoinResponsePlayersField =
            RequireField(typeof(ClientLobbyJoinResponseMessage), "playersInLobby");
        private static readonly FieldInfo BeginRunPlayersField =
            RequireField(typeof(LobbyBeginRunMessage), "playersInLobby");
        private static readonly FieldInfo PlayerJoinedPlayerField =
            RequireField(typeof(PlayerJoinedMessage), "lobbyPlayer");

        public static Type RuntimePlayerType => PlayerType;

        public static INetGameService GetNetService(JoinFlow joinFlow)
        {
            return JoinFlowNetServiceProperty.GetValue(joinFlow) as INetGameService
                   ?? throw new InvalidOperationException("JoinFlow has no active network service.");
        }

        public static List<MlbLobbyPlayerData> ReadLobbyPlayers(StartRunLobby lobby)
        {
            return ReadPlayerList(LobbyPlayersProperty.GetValue(lobby));
        }

        public static List<MlbLobbyPlayerData> ReadPlayers(ClientLobbyJoinResponseMessage message)
        {
            return ReadPlayerList(JoinResponsePlayersField.GetValue(message));
        }

        public static void WritePlayers(
            ref ClientLobbyJoinResponseMessage message,
            IReadOnlyList<MlbLobbyPlayerData> players)
        {
            SetStructField(ref message, JoinResponsePlayersField, CreatePlayerList(players));
        }

        public static List<MlbLobbyPlayerData> ReadPlayers(LobbyBeginRunMessage message)
        {
            return ReadPlayerList(BeginRunPlayersField.GetValue(message));
        }

        public static void WritePlayers(
            ref LobbyBeginRunMessage message,
            IReadOnlyList<MlbLobbyPlayerData> players)
        {
            SetStructField(ref message, BeginRunPlayersField, CreatePlayerList(players));
        }

        public static MlbLobbyPlayerData ReadPlayer(PlayerJoinedMessage message)
        {
            return ReadPlayer(PlayerJoinedPlayerField.GetValue(message)
                              ?? throw new InvalidDataException("PlayerJoinedMessage has no lobby player."));
        }

        public static void WritePlayer(ref PlayerJoinedMessage message, MlbLobbyPlayerData player)
        {
            SetStructField(ref message, PlayerJoinedPlayerField, CreatePlayer(player));
        }

        public static void WriteVersionInfo(PacketWriter writer, MlbLobbyPlayerData player)
        {
            if (PlayerVersionInfoField == null)
                return;
            if (player.VersionInfo is not IPacketSerializable versionInfo)
                throw new InvalidDataException("Lobby player is missing version information.");
            versionInfo.Serialize(writer);
        }

        public static object? ReadVersionInfo(PacketReader reader)
        {
            if (PlayerVersionInfoField == null)
                return null;
            if (Activator.CreateInstance(PlayerVersionInfoField.FieldType) is not IPacketSerializable versionInfo)
                throw new InvalidDataException("Lobby player version information is not packet serializable.");
            versionInfo.Deserialize(reader);
            return versionInfo;
        }

        private static List<MlbLobbyPlayerData> ReadPlayerList(object? rawPlayers)
        {
            if (rawPlayers is not IEnumerable enumerable)
                throw new InvalidDataException("Lobby message has no player list.");

            var players = new List<MlbLobbyPlayerData>();
            foreach (var player in enumerable)
                players.Add(ReadPlayer(player
                                       ?? throw new InvalidDataException("Lobby player list contains null.")));
            return players;
        }

        private static MlbLobbyPlayerData ReadPlayer(object player)
        {
            if (player.GetType() != PlayerType)
                throw new InvalidDataException($"Unexpected lobby player type: {player.GetType().FullName}.");

            return new(
                (ulong)PlayerIdField.GetValue(player)!,
                (int)PlayerSlotIdField.GetValue(player)!,
                (CharacterModel)PlayerCharacterField.GetValue(player)!,
                (SerializableUnlockState)PlayerUnlockStateField.GetValue(player)!,
                (int)PlayerMaxAscensionField.GetValue(player)!,
                PlayerVersionInfoField?.GetValue(player),
                (bool)PlayerReadyField.GetValue(player)!);
        }

        private static object CreatePlayer(MlbLobbyPlayerData data)
        {
            var player = Activator.CreateInstance(PlayerType)
                         ?? throw new InvalidOperationException("Could not create a lobby player value.");
            PlayerIdField.SetValue(player, data.Id);
            PlayerSlotIdField.SetValue(player, data.SlotId);
            PlayerCharacterField.SetValue(player, data.Character);
            PlayerUnlockStateField.SetValue(player, data.UnlockState);
            PlayerMaxAscensionField.SetValue(player, data.MaxMultiplayerAscensionUnlocked);
            if (PlayerVersionInfoField != null)
                PlayerVersionInfoField.SetValue(
                    player,
                    data.VersionInfo ?? Activator.CreateInstance(PlayerVersionInfoField.FieldType));
            PlayerReadyField.SetValue(player, data.IsReady);
            return player;
        }

        private static object CreatePlayerList(IEnumerable<MlbLobbyPlayerData> players)
        {
            var listType = typeof(List<>).MakeGenericType(PlayerType);
            var list = (IList)(Activator.CreateInstance(listType)
                               ?? throw new InvalidOperationException("Could not create a lobby player list."));
            foreach (var player in players)
                list.Add(CreatePlayer(player));
            return list;
        }

        private static void SetStructField<T>(ref T value, FieldInfo field, object fieldValue) where T : struct
        {
            object boxed = value;
            field.SetValue(boxed, fieldValue);
            value = (T)boxed;
        }

        private static FieldInfo RequirePlayerField(string name)
        {
            return AccessTools.Field(PlayerType, name)
                   ?? throw new MissingFieldException(PlayerType.FullName, name);
        }

        private static FieldInfo RequireField(Type type, string name)
        {
            return AccessTools.Field(type, name)
                   ?? throw new MissingFieldException(type.FullName, name);
        }
    }
}
