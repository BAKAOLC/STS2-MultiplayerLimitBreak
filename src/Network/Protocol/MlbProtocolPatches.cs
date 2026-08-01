using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Networking.MessageExtensions;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal static class MlbProtocolPatches
    {
        private static readonly FieldInfo? MaxPlayersField =
            AccessTools.Field(typeof(StartRunLobby), "<MaxPlayers>k__BackingField");

        private const string ExtensionId = "sts2.multiplayerLimitBreak";
        private const int ExtensionVersion = 1;
        private static readonly Lock ExtensionRegistrationLock = new();
        private static bool _extensionsRegistered;

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<InitialGameInfoHandlerPatch>();
            patcher.RegisterPatch<ClientLobbyJoinRequestSerializePatch>();
            patcher.RegisterPatch<ClientLobbyJoinRequestDeserializePatch>();
            patcher.RegisterPatch<ClientLobbyJoinRequestHandlerPatch>();
            patcher.RegisterPatch<OtherLobbyJoinRequestHandlerPatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseSerializePatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseDeserializePatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseHandlerPatch>();
            patcher.RegisterPatch<PlayerJoinedSerializePatch>();
            patcher.RegisterPatch<PlayerJoinedDeserializePatch>();
            patcher.RegisterPatch<PlayerJoinedHandlerPatch>();
            patcher.RegisterPatch<LobbyBeginRunSerializePatch>();
            patcher.RegisterPatch<LobbyBeginRunDeserializePatch>();
            patcher.RegisterPatch<LobbyBeginRunHandlerPatch>();
            patcher.RegisterPatch<StartRunLobbyConstructorPatch>();
            patcher.RegisterPatch<StartRunLobbyCleanUpPatch>();
            patcher.RegisterPatch<HostClientDisconnectedPatch>();
        }

        public static void InitializeExtensions()
        {
            lock (ExtensionRegistrationLock)
            {
                if (_extensionsRegistered)
                    return;

                RitsuNetMessageTailExtensions.RegisterBytes<InitialGameInfoMessage>(
                    ExtensionId,
                    ExtensionVersion,
                    static _ => MlbLobbyPayloadCodec.WriteCapability(MlbPeerCapability.Local),
                    static (version, payload) => MlbInboundPayloads.EnqueueInitialCapability(
                        ReadCapabilityExtension(version, payload)));
                RitsuNetMessageTailExtensions.RegisterBytes<ClientLobbyJoinRequestMessage>(
                    ExtensionId,
                    ExtensionVersion,
                    static _ => MlbLobbyPayloadCodec.WriteCapability(MlbPeerCapability.Local),
                    static (version, payload) => MlbInboundPayloads.EnqueueJoinCapability(
                        ReadCapabilityExtension(version, payload)));
                RitsuNetMessageTailExtensions.RegisterBytes<ClientLobbyJoinResponseMessage>(
                    ExtensionId,
                    ExtensionVersion,
                    WriteJoinResponseExtension,
                    static (version, payload) => MlbInboundPayloads.EnqueueJoinResponse(
                        version == ExtensionVersion
                            ? MlbLobbyPayloadCodec.ReadJoinResponse(payload.Span)
                            : new(null, new(MlbJoinRejectionReason.ProtocolMismatch, []))));
                RitsuNetMessageTailExtensions.RegisterBytes<PlayerJoinedMessage>(
                    ExtensionId,
                    ExtensionVersion,
                    WritePlayerJoinedExtension,
                    static (version, payload) => MlbInboundPayloads.EnqueuePlayerJoined(
                        version == ExtensionVersion
                            ? MlbLobbyPayloadCodec.ReadPlayerJoined(payload.Span)
                            : null));
                RitsuNetMessageTailExtensions.RegisterBytes<LobbyBeginRunMessage>(
                    ExtensionId,
                    ExtensionVersion,
                    WriteBeginRunExtension,
                    static (version, payload) => MlbInboundPayloads.EnqueueBeginRun(
                        version == ExtensionVersion
                            ? MlbLobbyPayloadCodec.ReadSnapshot(payload.Span)
                            : null));
                _extensionsRegistered = true;
            }
        }

        private static MlbPeerCapability ReadCapabilityExtension(
            int version,
            ReadOnlyMemory<byte> payload)
        {
            return version == ExtensionVersion
                ? MlbLobbyPayloadCodec.ReadCapability(payload.Span)
                : new(byte.MaxValue, byte.MaxValue, $"unsupported-extension-{version}");
        }

        private static byte[] WriteJoinResponseExtension(ClientLobbyJoinResponseMessage message)
        {
            var fullPlayers = message.playersInLobby
                              ?? throw new InvalidOperationException("Lobby join response has no player list.");
            var rejection = MlbOutboundJoinRejection.TryPeek();
            var payload = rejection != null
                ? new MlbJoinResponsePayload(null, rejection)
                : new MlbJoinResponsePayload(
                    CreateSnapshot(
                        fullPlayers,
                        fullPlayers.Any(player => player.slotId >= Const.VanillaPlayerLimit)),
                    null);
            return MlbLobbyPayloadCodec.WriteJoinResponse(payload);
        }

        private static byte[] WritePlayerJoinedExtension(PlayerJoinedMessage message)
        {
            var player = message.lobbyPlayer;
            var state = MlbLobbyProtocolRegistry.TryGetCurrentHostState();
            return MlbLobbyPayloadCodec.WritePlayerJoined(new(
                new(player.id, state?.GetCapability(player.id)),
                player.slotId >= Const.VanillaPlayerLimit ? player : null));
        }

        private static byte[] WriteBeginRunExtension(LobbyBeginRunMessage message)
        {
            var fullPlayers = message.playersInLobby
                              ?? throw new InvalidOperationException("Lobby begin-run message has no player list.");
            return MlbLobbyPayloadCodec.WriteSnapshot(CreateSnapshot(
                fullPlayers,
                fullPlayers.Any(player => player.slotId >= Const.VanillaPlayerLimit)));
        }

        private static MlbLobbySnapshot CreateSnapshot(
            IReadOnlyList<StartRunLobbyPlayer> players,
            bool includePlayers)
        {
            var state = MlbLobbyProtocolRegistry.TryGetCurrentHostState()
                        ?? throw new InvalidOperationException("No active MLB host lobby protocol state was found.");
            return state.CreateSnapshot(players, includePlayers);
        }

        private static List<StartRunLobbyPlayer> CreateVanillaProjection(
            IReadOnlyList<StartRunLobbyPlayer> players)
        {
            return players.Where(player => player.slotId < Const.VanillaPlayerLimit)
                .Take(Const.VanillaPlayerLimit)
                .ToList();
        }

        private static void SendRejection(
            StartRunLobby lobby,
            ulong senderId,
            MlbJoinRejection rejection,
            bool canReceiveStructuredReason)
        {
            if (canReceiveStructuredReason)
            {
                var response = new ClientLobbyJoinResponseMessage
                {
                    playersInLobby = lobby.Players,
                    ascension = lobby.Ascension,
                    dailyTime = lobby.DailyTime,
                    seed = lobby.Seed,
                    modifiers = lobby.Modifiers.Select(static modifier => modifier.ToSerializable()).ToList(),
                };
                using (MlbOutboundJoinRejection.Push(rejection))
                    ((INetHostGameService)lobby.NetService).SendMessage(response, senderId);
            }

            ((INetHostGameService)lobby.NetService).DisconnectClient(senderId, NetError.ModMismatch);
            MlbLobbyToasts.ShowHostRejection(lobby.NetService, senderId, rejection);
        }

        private static int FindFirstAvailableSlot(IReadOnlyList<StartRunLobbyPlayer> players)
        {
            for (var slot = 0; slot < Const.PlayerLimit; slot++)
                if (players.All(player => player.slotId != slot))
                    return slot;
            return Const.PlayerLimit;
        }

        private static MlbJoinRejection? ValidateExpansion(
            MlbLobbyProtocolState state,
            IReadOnlyList<StartRunLobbyPlayer> players,
            ulong senderId,
            MlbPeerCapability? requesterCapability)
        {
            if (requesterCapability == null)
                return new(
                    state.ExtendedProtocolActive
                        ? MlbJoinRejectionReason.ExtendedSessionRequiresProtocol
                        : MlbJoinRejectionReason.JoiningPeerUnsupported,
                    [senderId]);

            if (state.ExtendedProtocolActive && !requesterCapability.Value.Supports(state.SelectedProtocol))
                return new(MlbJoinRejectionReason.ProtocolMismatch, [senderId]);

            var existingBlockers = players
                .Where(player => state.GetCapability(player.id) is not { } capability ||
                                 !capability.Supports(MlbPeerCapability.Local.MaxProtocol))
                .Select(player => player.id)
                .ToArray();
            if (existingBlockers.Length > 0)
                return new(MlbJoinRejectionReason.ExistingIncompatiblePeers, existingBlockers);

            var allPeers = players.Select(player => player.id).Append(senderId).ToArray();
            if (!state.TryActivate(allPeers, out _, out var incompatiblePeers))
                return new(MlbJoinRejectionReason.ProtocolMismatch,
                    incompatiblePeers.Length > 0 ? incompatiblePeers : allPeers);

            return null;
        }

        private sealed class InitialGameInfoHandlerPatch : IPatchMethod
        {
            public static string PatchId => "mlb_initial_info_capability_handler";
            public static string Description => "Bind the host MLB capability to the active join flow";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(JoinFlow), "HandleInitialGameInfoMessage", [typeof(InitialGameInfoMessage), typeof(ulong)])];
            }

            private static void Prefix(JoinFlow __instance)
            {
                MlbLobbyProtocolRegistry.SetRemoteHostCapability(
                    __instance.NetService,
                    MlbInboundPayloads.DequeueInitialCapability());
            }
        }

        private sealed class ClientLobbyJoinRequestSerializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_join_request_capability_serialize";
            public static string Description => "Append MLB capability to the original lobby join request";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(ClientLobbyJoinRequestMessage), nameof(ClientLobbyJoinRequestMessage.Serialize), [typeof(PacketWriter)])];
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ClientLobbyJoinRequestMessage __instance, PacketWriter writer)
            {
                RitsuNetMessageTailExtensions.Write(writer, __instance);
            }
        }

        private sealed class ClientLobbyJoinRequestDeserializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_join_request_capability_deserialize";
            public static string Description => "Read MLB capability from the original lobby join request";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(ClientLobbyJoinRequestMessage), nameof(ClientLobbyJoinRequestMessage.Deserialize), [typeof(PacketReader)])];
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(PacketReader reader)
            {
                RitsuNetMessageTailExtensions.Read<ClientLobbyJoinRequestMessage>(reader);
            }
        }

        private sealed class ClientLobbyJoinRequestHandlerPatch : IPatchMethod
        {
            public static string PatchId => "mlb_join_request_protocol_gate";
            public static string Description => "Validate all peer capabilities before expanding the lobby";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(StartRunLobby), "HandleClientLobbyJoinRequestMessage",
                    [typeof(ClientLobbyJoinRequestMessage), typeof(ulong)])];
            }

            [HarmonyPriority(Priority.First)]
            private static bool Prefix(StartRunLobby __instance, ulong senderId)
            {
                if (__instance.NetService.Type != NetGameType.Host)
                    return true;

                if (__instance.MaxPlayers != Const.PlayerLimit)
                    MaxPlayersField?.SetValue(__instance, Const.PlayerLimit);

                var state = MlbLobbyProtocolRegistry.GetOrCreate(__instance);
                var capability = MlbInboundPayloads.DequeueJoinCapability();
                state.RecordCapability(senderId, capability);

                var nextSlot = FindFirstAvailableSlot(__instance.Players);
                var requiresExpansion = state.ExtendedProtocolActive ||
                                        nextSlot >= Const.VanillaPlayerLimit ||
                                        __instance.Players.Any(player => player.slotId >= Const.VanillaPlayerLimit);
                if (!requiresExpansion)
                    return true;

                var rejection = ValidateExpansion(state, __instance.Players, senderId, capability);
                if (rejection == null)
                    return true;

                SendRejection(__instance, senderId, rejection, capability != null);
                return false;
            }

            private static void Postfix(StartRunLobby __instance, ulong senderId)
            {
                if (__instance.NetService.Type != NetGameType.Host ||
                    __instance.Players.All(player => player.id != senderId))
                    return;

                var state = MlbLobbyProtocolRegistry.GetOrCreate(__instance);
                if (state.GetCapability(senderId) == null)
                    MlbLobbyToasts.ShowIncompatibleAccepted(__instance.NetService, senderId);
            }
        }

        private sealed class OtherLobbyJoinRequestHandlerPatch : IPatchMethod
        {
            public static string PatchId => "mlb_other_lobby_join_request_capability_discard";
            public static string Description => "Discard MLB capability evidence outside start-run lobbies";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(LoadRunLobby), "HandleClientLobbyJoinRequestMessage",
                        [typeof(ClientLobbyJoinRequestMessage), typeof(ulong)]),
                    new(typeof(RunLobby), "HandleClientLobbyJoinRequestMessage",
                        [typeof(ClientLobbyJoinRequestMessage), typeof(ulong)]),
                ];
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix()
            {
                MlbInboundPayloads.DequeueJoinCapability();
            }
        }

        private readonly record struct PlayerListSerializeState(List<StartRunLobbyPlayer> FullPlayers);

        private sealed class ClientLobbyJoinResponseSerializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_join_response_tail_serialize";
            public static string Description => "Append the authoritative MLB lobby snapshot or rejection";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(ClientLobbyJoinResponseMessage), nameof(ClientLobbyJoinResponseMessage.Serialize), [typeof(PacketWriter)])];
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix(ref ClientLobbyJoinResponseMessage __instance,
                out PlayerListSerializeState __state)
            {
                var fullPlayers = __instance.playersInLobby?.ToList()
                                  ?? throw new InvalidOperationException("Lobby join response has no player list.");
                __state = new(fullPlayers);
                __instance.playersInLobby = CreateVanillaProjection(fullPlayers);
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref ClientLobbyJoinResponseMessage __instance,
                PacketWriter writer,
                PlayerListSerializeState __state)
            {
                __instance.playersInLobby = __state.FullPlayers;
                RitsuNetMessageTailExtensions.Write(writer, __instance);
            }
        }

        private sealed class ClientLobbyJoinResponseDeserializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_join_response_tail_deserialize";
            public static string Description => "Restore the MLB lobby snapshot before original join handling";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(ClientLobbyJoinResponseMessage), nameof(ClientLobbyJoinResponseMessage.Deserialize), [typeof(PacketReader)])];
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref ClientLobbyJoinResponseMessage __instance, PacketReader reader)
            {
                RitsuNetMessageTailExtensions.Read<ClientLobbyJoinResponseMessage>(reader);
                var decoded = MlbInboundPayloads.DequeueJoinResponse();
                if (decoded?.Snapshot?.FullPlayers is { } fullPlayers)
                    __instance.playersInLobby = fullPlayers.ToList();

                MlbInboundPayloads.EnqueueJoinResponse(decoded);
            }
        }

        private sealed class ClientLobbyJoinResponseHandlerPatch : IPatchMethod
        {
            public static string PatchId => "mlb_join_response_tail_handler";
            public static string Description => "Apply MLB join state or surface a structured rejection";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(JoinFlow), "HandleJoinResponseMessage",
                    [typeof(ClientLobbyJoinResponseMessage), typeof(ulong)])];
            }

            [HarmonyPriority(Priority.First)]
            private static bool Prefix(JoinFlow __instance)
            {
                var payload = MlbInboundPayloads.DequeueJoinResponse();
                if (payload?.Rejection is { } rejection)
                {
                    MlbLobbyToasts.ShowClientRejection(__instance.NetService, rejection);
                    __instance.NetService.Disconnect(NetError.ModMismatch);
                    return false;
                }

                if (payload?.Snapshot is { } snapshot)
                {
                    MlbLobbyProtocolRegistry.StageClientSnapshot(__instance.NetService, snapshot);
                    return true;
                }

                if (MlbLobbyProtocolRegistry.GetRemoteHostCapability(__instance.NetService) != null)
                {
                    MlbLobbyToasts.ShowClientRejection(
                        __instance.NetService,
                        new(MlbJoinRejectionReason.ProtocolMismatch, []));
                    __instance.NetService.Disconnect(NetError.ModMismatch);
                    return false;
                }

                return true;
            }
        }

        private readonly record struct PlayerJoinedSerializeState(StartRunLobbyPlayer FullPlayer, bool Extended);

        private sealed class PlayerJoinedSerializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_player_joined_tail_serialize";
            public static string Description => "Append MLB capability and extended player data to PlayerJoinedMessage";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(PlayerJoinedMessage), nameof(PlayerJoinedMessage.Serialize), [typeof(PacketWriter)])];
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix(ref PlayerJoinedMessage __instance, out PlayerJoinedSerializeState __state)
            {
                var fullPlayer = __instance.lobbyPlayer;
                var extended = fullPlayer.slotId >= Const.VanillaPlayerLimit;
                __state = new(fullPlayer, extended);
                if (!extended)
                    return;

                var placeholder = fullPlayer;
                placeholder.id = 0;
                placeholder.slotId = 0;
                __instance.lobbyPlayer = placeholder;
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref PlayerJoinedMessage __instance,
                PacketWriter writer,
                PlayerJoinedSerializeState __state)
            {
                __instance.lobbyPlayer = __state.FullPlayer;
                RitsuNetMessageTailExtensions.Write(writer, __instance);
            }
        }

        private sealed class PlayerJoinedDeserializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_player_joined_tail_deserialize";
            public static string Description => "Restore MLB player data before original PlayerJoined handling";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(PlayerJoinedMessage), nameof(PlayerJoinedMessage.Deserialize), [typeof(PacketReader)])];
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref PlayerJoinedMessage __instance, PacketReader reader)
            {
                RitsuNetMessageTailExtensions.Read<PlayerJoinedMessage>(reader);
                var decoded = MlbInboundPayloads.DequeuePlayerJoined();
                if (decoded?.ExtendedPlayer is { } player)
                    __instance.lobbyPlayer = player;

                if (__instance.lobbyPlayer.id == 0)
                    throw new InvalidDataException("Extended PlayerJoinedMessage is missing a valid MLB tail.");
                MlbInboundPayloads.EnqueuePlayerJoined(decoded);
            }
        }

        private sealed class PlayerJoinedHandlerPatch : IPatchMethod
        {
            public static string PatchId => "mlb_player_joined_tail_handler";
            public static string Description => "Apply synchronized MLB capability state before original player-join handling";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(StartRunLobby), "HandlePlayerJoinedMessage",
                    [typeof(PlayerJoinedMessage), typeof(ulong)])];
            }

            private static void Prefix(StartRunLobby __instance)
            {
                if (MlbInboundPayloads.DequeuePlayerJoined() is not { } payload)
                    return;
                MlbLobbyProtocolRegistry.GetOrCreate(__instance)
                    .RecordCapability(payload.Capability.PeerId, payload.Capability.Capability);
            }
        }

        private sealed class LobbyBeginRunSerializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_begin_run_tail_serialize";
            public static string Description => "Append the final authoritative MLB lobby snapshot";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(LobbyBeginRunMessage), nameof(LobbyBeginRunMessage.Serialize), [typeof(PacketWriter)])];
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix(ref LobbyBeginRunMessage __instance,
                out PlayerListSerializeState __state)
            {
                var fullPlayers = __instance.playersInLobby?.ToList()
                                  ?? throw new InvalidOperationException("Lobby begin-run message has no player list.");
                __state = new(fullPlayers);
                __instance.playersInLobby = CreateVanillaProjection(fullPlayers);
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref LobbyBeginRunMessage __instance,
                PacketWriter writer,
                PlayerListSerializeState __state)
            {
                __instance.playersInLobby = __state.FullPlayers;
                RitsuNetMessageTailExtensions.Write(writer, __instance);
            }
        }

        private sealed class LobbyBeginRunDeserializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_begin_run_tail_deserialize";
            public static string Description => "Restore the final MLB player snapshot before original run start handling";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(LobbyBeginRunMessage), nameof(LobbyBeginRunMessage.Deserialize), [typeof(PacketReader)])];
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref LobbyBeginRunMessage __instance, PacketReader reader)
            {
                RitsuNetMessageTailExtensions.Read<LobbyBeginRunMessage>(reader);
                var snapshot = MlbInboundPayloads.DequeueBeginRun();
                if (snapshot?.FullPlayers is { } players)
                    __instance.playersInLobby = players.ToList();
                MlbInboundPayloads.EnqueueBeginRun(snapshot);
            }
        }

        private sealed class LobbyBeginRunHandlerPatch : IPatchMethod
        {
            public static string PatchId => "mlb_begin_run_tail_handler";
            public static string Description => "Apply final MLB settings before original run start handling";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(StartRunLobby), "HandleLobbyBeginRunMessage",
                    [typeof(LobbyBeginRunMessage), typeof(ulong)])];
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix(StartRunLobby __instance)
            {
                var snapshot = MlbInboundPayloads.DequeueBeginRun();
                var state = MlbLobbyProtocolRegistry.GetOrCreate(__instance);
                if (snapshot == null)
                {
                    if (state.ExtendedProtocolActive)
                        throw new InvalidDataException("Expanded lobby begin-run message is missing its MLB tail.");
                    return;
                }

                state.ApplySnapshot(snapshot);
                RuntimeMultiplayerSettings.ApplyRemoteHostSettings(snapshot.ExtraPlayerScalingMultiplier);
            }
        }

        private sealed class StartRunLobbyConstructorPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_protocol_session_create";
            public static string Description => "Create an MLB protocol state bound to the StartRunLobby lifecycle";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(
                        typeof(StartRunLobby),
                        ".ctor",
                        [typeof(GameMode), typeof(INetGameService), typeof(IStartRunLobbyListener), typeof(int)],
                        MethodType.Constructor),
                    new(
                        typeof(StartRunLobby),
                        ".ctor",
                        [typeof(GameMode), typeof(INetGameService), typeof(IStartRunLobbyListener),
                            typeof(TimeServerResult), typeof(int)],
                        MethodType.Constructor),
                ];
            }

            private static void Postfix(StartRunLobby __instance)
            {
                MlbLobbyProtocolRegistry.GetOrCreate(__instance);
            }
        }

        private sealed class StartRunLobbyCleanUpPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_protocol_session_cleanup";
            public static string Description => "Clear MLB protocol evidence when the lobby ends";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp), [typeof(bool), typeof(NetError)])];
            }

            private static void Postfix(StartRunLobby __instance)
            {
                MlbLobbyProtocolRegistry.CleanUp(__instance);
            }
        }

        private sealed class HostClientDisconnectedPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_protocol_peer_cleanup";
            public static string Description => "Remove disconnected peers from the MLB capability roster";

            public static ModPatchTarget[] GetTargets()
            {
                return [new(typeof(StartRunLobby), "OnDisconnectedFromClientAsHost")];
            }

            private static void Prefix(StartRunLobby __instance, ulong playerId)
            {
                if (!MlbLobbyProtocolRegistry.TryGet(__instance, out var state))
                    return;

                var wasAccepted = __instance.Players.Any(player => player.id == playerId);
                var hadUnsupported = wasAccepted && state.GetCapability(playerId) == null;
                state.RemovePeer(playerId);
                if (hadUnsupported &&
                    __instance.Players.Where(player => player.id != playerId)
                        .All(player => state.GetCapability(player.id) != null))
                    MlbLobbyToasts.ShowExpansionAvailable(__instance.NetService);
            }
        }
    }

    internal static class MlbInboundPayloads
    {
        private static readonly AsyncLocal<Queue<MlbPeerCapability?>?> InitialCapabilities = new();
        private static readonly AsyncLocal<Queue<MlbPeerCapability?>?> JoinCapabilities = new();
        private static readonly AsyncLocal<Queue<MlbJoinResponsePayload?>?> JoinResponses = new();
        private static readonly AsyncLocal<Queue<MlbPlayerJoinedPayload?>?> PlayerJoinedPayloads = new();
        private static readonly AsyncLocal<Queue<MlbLobbySnapshot?>?> BeginRunPayloads = new();

        public static void EnqueueInitialCapability(MlbPeerCapability? value) => Enqueue(InitialCapabilities, value);
        public static MlbPeerCapability? DequeueInitialCapability() => Dequeue(InitialCapabilities);
        public static void EnqueueJoinCapability(MlbPeerCapability? value) => Enqueue(JoinCapabilities, value);
        public static MlbPeerCapability? DequeueJoinCapability() => Dequeue(JoinCapabilities);
        public static void EnqueueJoinResponse(MlbJoinResponsePayload? value) => Enqueue(JoinResponses, value);
        public static MlbJoinResponsePayload? DequeueJoinResponse() => Dequeue(JoinResponses);
        public static void EnqueuePlayerJoined(MlbPlayerJoinedPayload? value) => Enqueue(PlayerJoinedPayloads, value);
        public static MlbPlayerJoinedPayload? DequeuePlayerJoined() => Dequeue(PlayerJoinedPayloads);
        public static void EnqueueBeginRun(MlbLobbySnapshot? value) => Enqueue(BeginRunPayloads, value);
        public static MlbLobbySnapshot? DequeueBeginRun() => Dequeue(BeginRunPayloads);

        private static void Enqueue<T>(AsyncLocal<Queue<T>?> storage, T value)
        {
            (storage.Value ??= new()).Enqueue(value);
        }

        private static T? Dequeue<T>(AsyncLocal<Queue<T>?> storage)
        {
            return storage.Value is { Count: > 0 } queue ? queue.Dequeue() : default;
        }
    }

    internal static class MlbOutboundJoinRejection
    {
        private static readonly AsyncLocal<Stack<MlbJoinRejection>?> Rejections = new();

        public static IDisposable Push(MlbJoinRejection rejection)
        {
            var stack = Rejections.Value ??= new();
            stack.Push(rejection);
            return new Scope(stack);
        }

        public static MlbJoinRejection? TryPeek()
        {
            return Rejections.Value is { Count: > 0 } stack ? stack.Peek() : null;
        }

        private sealed class Scope(Stack<MlbJoinRejection> stack) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (stack.Count > 0)
                    stack.Pop();
            }
        }
    }
}
