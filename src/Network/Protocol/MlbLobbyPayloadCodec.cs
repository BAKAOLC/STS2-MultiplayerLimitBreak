using System.Text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal readonly record struct MlbPeerCapabilityEntry(
        ulong PeerId,
        MlbPeerCapability? Capability);

    internal sealed record MlbLobbySnapshot(
        bool ExtendedProtocolActive,
        byte SelectedProtocol,
        double ExtraPlayerScalingMultiplier,
        IReadOnlyList<MlbPeerCapabilityEntry> Capabilities,
        IReadOnlyList<MlbLobbyPlayerData>? FullPlayers);

    internal sealed record MlbJoinRejection(
        MlbJoinRejectionReason Reason,
        IReadOnlyList<ulong> BlockingPeerIds);

    internal sealed record MlbJoinResponsePayload(
        MlbLobbySnapshot? Snapshot,
        MlbJoinRejection? Rejection);

    internal sealed record MlbPlayerJoinedPayload(
        MlbPeerCapabilityEntry Capability,
        MlbLobbyPlayerData? ExtendedPlayer);

    internal static class MlbLobbyPayloadCodec
    {
        private const int MaxCapabilityEntries = Const.PlayerLimit;
        private const int CapabilityVersionLengthBits = 7;
        private const int MaxCapabilityVersionBytes = 64;

        public static byte[] WriteCapability(MlbPeerCapability capability)
        {
            return Write(writer => WriteCapability(writer, capability));
        }

        public static MlbPeerCapability ReadCapability(ReadOnlySpan<byte> payload)
        {
            return Read(payload, static reader => ReadCapability(reader));
        }

        public static byte[] WriteJoinResponse(MlbJoinResponsePayload payload)
        {
            return Write(writer =>
            {
                writer.WriteBool(payload.Rejection != null);
                if (payload.Rejection is { } rejection)
                {
                    writer.WriteByte((byte)rejection.Reason);
                    writer.WriteInt(rejection.BlockingPeerIds.Count, 5);
                    foreach (var peerId in rejection.BlockingPeerIds)
                        writer.WriteULong(peerId);
                    return;
                }

                WriteSnapshot(writer, payload.Snapshot
                                      ?? throw new InvalidDataException("Accepted join response has no snapshot."));
            });
        }

        public static MlbJoinResponsePayload ReadJoinResponse(ReadOnlySpan<byte> payload)
        {
            return Read<MlbJoinResponsePayload>(payload, reader =>
            {
                if (reader.ReadBool())
                {
                    var reason = (MlbJoinRejectionReason)reader.ReadByte();
                    var count = reader.ReadInt(5);
                    var blockers = new List<ulong>(count);
                    for (var i = 0; i < count; i++)
                        blockers.Add(reader.ReadULong());
                    return new(null, new(reason, blockers));
                }

                return new(ReadSnapshot(reader), null);
            });
        }

        public static byte[] WritePlayerJoined(MlbPlayerJoinedPayload payload)
        {
            return Write(writer =>
            {
                WriteCapabilityEntry(writer, payload.Capability);
                writer.WriteBool(payload.ExtendedPlayer.HasValue);
                if (payload.ExtendedPlayer is { } player)
                    WritePlayer(writer, player);
            });
        }

        public static MlbPlayerJoinedPayload ReadPlayerJoined(ReadOnlySpan<byte> payload)
        {
            return Read<MlbPlayerJoinedPayload>(payload, reader =>
            {
                var capability = ReadCapabilityEntry(reader);
                MlbLobbyPlayerData? player = reader.ReadBool() ? ReadPlayer(reader) : null;
                return new(capability, player);
            });
        }

        public static byte[] WriteSnapshot(MlbLobbySnapshot snapshot)
        {
            return Write(writer => WriteSnapshot(writer, snapshot));
        }

        public static MlbLobbySnapshot ReadSnapshot(ReadOnlySpan<byte> payload)
        {
            return Read(payload, static reader => ReadSnapshot(reader));
        }

        private static void WriteSnapshot(PacketWriter writer, MlbLobbySnapshot snapshot)
        {
            writer.WriteBool(snapshot.ExtendedProtocolActive);
            writer.WriteByte(snapshot.SelectedProtocol);
            writer.WriteDouble(snapshot.ExtraPlayerScalingMultiplier);
            writer.WriteInt(snapshot.Capabilities.Count, 5);
            foreach (var capability in snapshot.Capabilities)
                WriteCapabilityEntry(writer, capability);

            writer.WriteBool(snapshot.FullPlayers != null);
            if (snapshot.FullPlayers == null)
                return;

            writer.WriteInt(snapshot.FullPlayers.Count, 5);
            foreach (var player in snapshot.FullPlayers)
                WritePlayer(writer, player);
        }

        private static MlbLobbySnapshot ReadSnapshot(PacketReader reader)
        {
            var active = reader.ReadBool();
            var protocol = reader.ReadByte();
            var multiplier = reader.ReadDouble();
            var capabilityCount = reader.ReadInt(5);
            if (capabilityCount is < 0 or > MaxCapabilityEntries)
                throw new InvalidDataException($"Invalid MLB capability count: {capabilityCount}.");

            var capabilities = new List<MlbPeerCapabilityEntry>(capabilityCount);
            for (var i = 0; i < capabilityCount; i++)
                capabilities.Add(ReadCapabilityEntry(reader));

            List<MlbLobbyPlayerData>? players = null;
            if (reader.ReadBool())
            {
                var playerCount = reader.ReadInt(5);
                if (playerCount is < 0 or > Const.PlayerLimit)
                    throw new InvalidDataException($"Invalid MLB player count: {playerCount}.");
                players = new(playerCount);
                for (var i = 0; i < playerCount; i++)
                    players.Add(ReadPlayer(reader));
            }

            ValidateSnapshot(active, protocol, capabilities, players);
            return new(active, protocol, multiplier, capabilities, players);
        }

        private static void ValidateSnapshot(
            bool active,
            byte protocol,
            IReadOnlyList<MlbPeerCapabilityEntry> capabilities,
            IReadOnlyList<MlbLobbyPlayerData>? players)
        {
            if (active && !MlbPeerCapability.Local.Supports(protocol))
                throw new InvalidDataException($"Unsupported active MLB protocol version: {protocol}.");
            if (capabilities.Select(entry => entry.PeerId).Distinct().Count() != capabilities.Count)
                throw new InvalidDataException("MLB snapshot contains duplicate capability peer IDs.");
            if (capabilities.Count > Const.VanillaPlayerLimit && (!active || players == null))
                throw new InvalidDataException("Expanded MLB snapshot is missing its complete player roster.");
            if (players == null)
                return;
            if (players.Select(player => player.Id).Distinct().Count() != players.Count)
                throw new InvalidDataException("MLB snapshot contains duplicate player IDs.");
            if (players.Count != capabilities.Count ||
                !players.Select(player => player.Id).ToHashSet()
                    .SetEquals(capabilities.Select(entry => entry.PeerId)))
                throw new InvalidDataException("MLB snapshot player and capability rosters do not match.");
            if (players.Select(player => player.SlotId).Distinct().Count() != players.Count ||
                players.Any(player => player.SlotId is < 0 or >= Const.PlayerLimit))
                throw new InvalidDataException("MLB snapshot contains invalid or duplicate player slots.");
        }

        private static void WriteCapabilityEntry(PacketWriter writer, MlbPeerCapabilityEntry entry)
        {
            writer.WriteULong(entry.PeerId);
            writer.WriteBool(entry.Capability.HasValue);
            if (entry.Capability is { } capability)
                WriteCapability(writer, capability);
        }

        private static MlbPeerCapabilityEntry ReadCapabilityEntry(PacketReader reader)
        {
            var peerId = reader.ReadULong();
            return new(peerId, reader.ReadBool() ? ReadCapability(reader) : null);
        }

        private static void WriteCapability(PacketWriter writer, MlbPeerCapability capability)
        {
            var versionBytes = Encoding.UTF8.GetBytes(capability.ModVersion);
            if (versionBytes.Length > MaxCapabilityVersionBytes)
                throw new InvalidDataException(
                    $"MLB version identifier is {versionBytes.Length} bytes; maximum is {MaxCapabilityVersionBytes} bytes.");

            writer.WriteByte(capability.MinProtocol);
            writer.WriteByte(capability.MaxProtocol);
            writer.WriteInt(versionBytes.Length, CapabilityVersionLengthBits);
            writer.WriteBytes(versionBytes, versionBytes.Length);
        }

        private static MlbPeerCapability ReadCapability(PacketReader reader)
        {
            var minProtocol = reader.ReadByte();
            var maxProtocol = reader.ReadByte();
            var versionLength = reader.ReadInt(CapabilityVersionLengthBits);
            if (versionLength is < 0 or > MaxCapabilityVersionBytes)
                throw new InvalidDataException($"Invalid MLB version identifier length: {versionLength}.");
            var versionBytes = new byte[versionLength];
            reader.ReadBytes(versionBytes, versionLength);
            var modVersion = Encoding.UTF8.GetString(versionBytes);
            if (minProtocol == 0 || maxProtocol < minProtocol)
                throw new InvalidDataException(
                    $"Invalid MLB protocol range {minProtocol}..{maxProtocol} from version '{modVersion}'.");
            return new(minProtocol, maxProtocol, modVersion);
        }

        private static void WritePlayer(PacketWriter writer, MlbLobbyPlayerData player)
        {
            writer.WriteULong(player.Id);
            writer.WriteInt(player.SlotId, Const.SlotIdBits);
            writer.WriteModel(player.Character);
            writer.Write(player.UnlockState);
            writer.WriteInt(player.MaxMultiplayerAscensionUnlocked);
            MlbGameApiCompat.WriteVersionInfo(writer, player);
            MlbGameApiCompat.WriteIsModded(writer, player);
            writer.WriteBool(player.IsReady);
        }

        private static MlbLobbyPlayerData ReadPlayer(PacketReader reader)
        {
            var id = reader.ReadULong();
            var slotId = reader.ReadInt(Const.SlotIdBits);
            var character = reader.ReadModel<CharacterModel>();
            var unlockState = reader.Read<MegaCrit.Sts2.Core.Unlocks.SerializableUnlockState>();
            var maxAscension = reader.ReadInt();
            var versionInfo = MlbGameApiCompat.ReadVersionInfo(reader);
            var isModded = MlbGameApiCompat.ReadIsModded(reader, versionInfo);
            var isReady = reader.ReadBool();
            return new(id, slotId, character, unlockState, maxAscension, versionInfo, isModded, isReady);
        }

        private static byte[] Write(Action<PacketWriter> write)
        {
            var writer = new PacketWriter { WarnOnGrow = false };
            write(writer);
            writer.ZeroByteRemainder();
            return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
        }

        private static T Read<T>(ReadOnlySpan<byte> payload, Func<PacketReader, T> read)
        {
            var reader = new PacketReader();
            reader.Reset(payload.ToArray());
            return read(reader);
        }
    }
}
