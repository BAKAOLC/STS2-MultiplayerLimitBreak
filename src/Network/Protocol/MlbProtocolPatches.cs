using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Networking.MessageExtensions;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

// ReSharper disable ClassNeverInstantiated.Local
// ReSharper disable InconsistentNaming
// ReSharper disable RedundantAssignment
// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedType.Local

namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal static class MlbProtocolPatches
    {
        private static readonly FieldInfo? MaxPlayersField =
            AccessTools.Field(typeof(StartRunLobby), "<MaxPlayers>k__BackingField");
        private static readonly AccessTools.FieldRef<NetMessageBus, PacketWriter> NetMessageBusWriterRef =
            AccessTools.FieldRefAccess<NetMessageBus, PacketWriter>("_writer");
        private static readonly AccessTools.FieldRef<NetMessageBus, PacketReader> NetMessageBusReaderRef =
            AccessTools.FieldRefAccess<NetMessageBus, PacketReader>("_reader");

        private const string ExtensionId = "sts2.multiplayerLimitBreak";
        private const int ExtensionVersion = 1;
        private static readonly Lock ExtensionRegistrationLock = new();
        private static bool _extensionsRegistered;

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<InitialGameInfoHandlerPatch>();
            patcher.RegisterPatch<NetMessageBusDeserializePatch>();
            patcher.RegisterPatch<ClientLobbyJoinRequestHandlerPatch>();
            patcher.RegisterPatch<OtherLobbyJoinRequestHandlerPatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseHandlerPatch>();
            patcher.RegisterPatch<PlayerJoinedHandlerPatch>();
            patcher.RegisterPatch<LobbyBeginRunHandlerPatch>();
            patcher.RegisterPatch<StartRunLobbyConstructorPatch>();
            patcher.RegisterPatch<StartRunLobbyCleanUpPatch>();
            patcher.RegisterPatch<HostClientDisconnectedPatch>();
        }

        public static bool ApplyDynamicPatches(ModPatcher patcher)
        {
            var serializeDefinition = AccessTools.DeclaredMethod(
                typeof(NetMessageBus),
                nameof(NetMessageBus.SerializeMessage));
            if (serializeDefinition is not { IsGenericMethodDefinition: true })
                throw new MissingMethodException(
                    typeof(NetMessageBus).FullName,
                    nameof(NetMessageBus.SerializeMessage));

            var hostBroadcastDefinition = AccessTools.GetDeclaredMethods(typeof(NetHostGameService))
                .SingleOrDefault(method =>
                    method.Name == nameof(NetHostGameService.SendMessage) &&
                    method is { IsGenericMethodDefinition: true } &&
                    method.GetParameters().Length == 1);
            if (hostBroadcastDefinition == null)
                throw new MissingMethodException(
                    typeof(NetHostGameService).FullName,
                    nameof(NetHostGameService.SendMessage));

            return patcher.ApplyDynamicPatches(
                [
                    CreatePatch(
                        "mlb_join_request_bus_serialize",
                        typeof(ClientLobbyJoinRequestMessage),
                        typeof(ClientLobbyJoinRequestNetMessageBusPatch)),
                    CreatePatch(
                        "mlb_join_response_bus_serialize",
                        typeof(ClientLobbyJoinResponseMessage),
                        typeof(ClientLobbyJoinResponseNetMessageBusPatch)),
                    CreatePatch(
                        "mlb_player_joined_bus_serialize",
                        typeof(PlayerJoinedMessage),
                        typeof(PlayerJoinedNetMessageBusPatch)),
                    CreateBeginRunProjectionPatch(
                        "mlb_begin_run_bus_serialize",
                        typeof(LobbyBeginRunMessage),
                        typeof(LobbyBeginRunNetMessageBusPatch)),
                ],
                rollbackOnCriticalFailure: true);

            DynamicPatchInfo CreatePatch(string id, Type messageType, Type patchType)
            {
                var prefix = AccessTools.DeclaredMethod(patchType, "Prefix");
                var postfix = AccessTools.DeclaredMethod(patchType, "Postfix");
                if (prefix == null && postfix == null)
                    throw new MissingMethodException(patchType.FullName, "Prefix or Postfix");
                return new(
                    id,
                    serializeDefinition.MakeGenericMethod(messageType),
                    prefix: prefix == null ? null : new HarmonyMethod(prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(postfix),
                    description: $"Serialize {messageType.Name} with its MLB tail at the non-inlined message-bus boundary");
            }

            DynamicPatchInfo CreateBeginRunProjectionPatch(string id, Type messageType, Type patchType)
            {
                var prefix = AccessTools.DeclaredMethod(patchType, "Prefix")
                             ?? throw new MissingMethodException(patchType.FullName, "Prefix");
                return new(
                    id,
                    hostBroadcastDefinition.MakeGenericMethod(messageType),
                    prefix: new HarmonyMethod(prefix),
                    description: "Project LobbyBeginRunMessage at the host broadcast boundary before serialization");
            }
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
            var fullPlayers = MlbGameApiCompat.ReadPlayers(message);
            var rejection = MlbOutboundJoinRejection.TryPeek();
            var payload = rejection != null
                ? new MlbJoinResponsePayload(null, rejection)
                : new MlbJoinResponsePayload(
                    CreateSnapshot(
                        fullPlayers,
                        fullPlayers.Any(player => player.SlotId >= Const.VanillaPlayerLimit)),
                    null);
            return MlbLobbyPayloadCodec.WriteJoinResponse(payload);
        }

        private static byte[] WritePlayerJoinedExtension(PlayerJoinedMessage message)
        {
            var player = MlbGameApiCompat.ReadPlayer(message);
            var state = MlbLobbyProtocolRegistry.TryGetCurrentHostState();
            return MlbLobbyPayloadCodec.WritePlayerJoined(new(
                new(player.Id, state?.GetCapability(player.Id)),
                player.SlotId >= Const.VanillaPlayerLimit ? player : null));
        }

        private static byte[] WriteBeginRunExtension(LobbyBeginRunMessage message)
        {
            var lobby = MlbLobbyProtocolRegistry.TryGetCurrentHostLobby()
                        ?? throw new InvalidOperationException("No active MLB host lobby was found.");
            var fullPlayers = MlbGameApiCompat.ReadLobbyPlayers(lobby);
            return MlbLobbyPayloadCodec.WriteSnapshot(CreateSnapshot(
                fullPlayers,
                fullPlayers.Any(player => player.SlotId >= Const.VanillaPlayerLimit)));
        }

        private static MlbLobbySnapshot CreateSnapshot(
            IReadOnlyList<MlbLobbyPlayerData> players,
            bool includePlayers)
        {
            var state = MlbLobbyProtocolRegistry.TryGetCurrentHostState()
                        ?? throw new InvalidOperationException("No active MLB host lobby protocol state was found.");
            return state.CreateSnapshot(players, includePlayers);
        }

        private static List<MlbLobbyPlayerData> CreateVanillaProjection(
            IReadOnlyList<MlbLobbyPlayerData> players)
        {
            var projection = players.Take(Const.VanillaPlayerLimit).ToList();
            var usedSlots = projection
                .Where(player => player.SlotId is >= 0 and < Const.VanillaPlayerLimit)
                .Select(player => player.SlotId)
                .ToHashSet();
            var nextFreeSlot = 0;

            for (var index = 0; index < projection.Count; index++)
            {
                var player = projection[index];
                if (player.SlotId is >= 0 and < Const.VanillaPlayerLimit)
                    continue;

                while (usedSlots.Contains(nextFreeSlot))
                    nextFreeSlot++;
                projection[index] = player with { SlotId = nextFreeSlot };
                usedSlots.Add(nextFreeSlot);
            }

            return projection;
        }

        private static void FinishMessageTail<T>(
            NetMessageBus messageBus,
            T message,
            ref int length,
            ref byte[] result)
            where T : INetMessage
        {
            var writer = NetMessageBusWriterRef(messageBus);
            RitsuNetMessageTailExtensions.Write(writer, message);
            length = checked((int)(((long)writer.BitPosition + 7) / 8));
            result = writer.Buffer;
        }

        private sealed class NetMessageBusDeserializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_tail_bus_deserialize";
            public static string Description =>
                "Read MLB lobby tails after each complete vanilla message deserialization pipeline";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(
                        typeof(NetMessageBus),
                        nameof(NetMessageBus.TryDeserializeMessage),
                        [typeof(byte[]), typeof(INetMessage).MakeByRefType(), typeof(ulong?).MakeByRefType()]),
                ];
            }

            private static void Postfix(NetMessageBus __instance, bool __result, ref INetMessage? message)
            {
                if (!__result || message == null)
                    return;

                switch (message)
                {
                    case ClientLobbyJoinRequestMessage:
                        RitsuNetMessageTailExtensions.Read<ClientLobbyJoinRequestMessage>(GetReader());
                        break;
                    case ClientLobbyJoinResponseMessage joinResponse:
                        RitsuNetMessageTailExtensions.Read<ClientLobbyJoinResponseMessage>(GetReader());
                        var joinPayload = MlbInboundPayloads.DequeueJoinResponse();
                        if (joinPayload?.Snapshot?.FullPlayers is { } joinedPlayers)
                            MlbGameApiCompat.WritePlayers(ref joinResponse, joinedPlayers);
                        MlbInboundPayloads.EnqueueJoinResponse(joinPayload);
                        message = joinResponse;
                        break;
                    case PlayerJoinedMessage playerJoined:
                        RitsuNetMessageTailExtensions.Read<PlayerJoinedMessage>(GetReader());
                        var joinedPayload = MlbInboundPayloads.DequeuePlayerJoined();
                        if (joinedPayload?.ExtendedPlayer is { } joinedPlayer)
                            MlbGameApiCompat.WritePlayer(ref playerJoined, joinedPlayer);
                        MlbInboundPayloads.EnqueuePlayerJoined(joinedPayload);
                        message = playerJoined;
                        break;
                }

                PacketReader GetReader()
                {
                    return NetMessageBusReaderRef(__instance);
                }
            }
        }

        private static bool CanUseVanillaRoster(IReadOnlyList<MlbLobbyPlayerData> players)
        {
            return players.Count <= Const.VanillaPlayerLimit &&
                   players.All(player => player.SlotId is >= 0 and < Const.VanillaPlayerLimit) &&
                   players.Select(player => player.SlotId).Distinct().Count() == players.Count;
        }

        private static bool CanRestoreExtendedRoster(MlbPeerCapability? capability)
        {
            return capability is { } value && value.Supports(MlbPeerCapability.Local.MaxProtocol);
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
                    ascension = lobby.Ascension,
                    dailyTime = lobby.DailyTime,
                    seed = lobby.Seed,
                    modifiers = lobby.Modifiers.Select(static modifier => modifier.ToSerializable()).ToList(),
                };
                MlbGameApiCompat.WritePlayers(ref response, MlbGameApiCompat.ReadLobbyPlayers(lobby));
                using (MlbOutboundJoinRejection.Push(rejection))
                    ((INetHostGameService)lobby.NetService).SendMessage(response, senderId);
            }

            ((INetHostGameService)lobby.NetService).DisconnectClient(senderId, NetError.ModMismatch);
            MlbLobbyToasts.ShowHostRejection(lobby.NetService, senderId, rejection);
        }

        private static MlbJoinRejection? ValidateExpansion(
            MlbLobbyProtocolState state,
            IReadOnlyList<MlbLobbyPlayerData> players,
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
                .Where(player => state.GetCapability(player.Id) is not { } capability ||
                                 !capability.Supports(MlbPeerCapability.Local.MaxProtocol))
                .Select(player => player.Id)
                .ToArray();
            if (existingBlockers.Length > 0)
                return new(MlbJoinRejectionReason.ExistingIncompatiblePeers, existingBlockers);

            var allPeers = players.Select(player => player.Id).Append(senderId).ToArray();
            if (!state.TryActivate(allPeers, out var incompatiblePeers))
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
                    MlbGameApiCompat.GetNetService(__instance),
                    MlbInboundPayloads.DequeueInitialCapability());
            }
        }

        private static class ClientLobbyJoinRequestNetMessageBusPatch
        {
            [HarmonyPriority(Priority.Last)]
            internal static void Postfix(
                NetMessageBus __instance,
                ClientLobbyJoinRequestMessage message,
                ref int length,
                ref byte[] __result)
            {
                FinishMessageTail(__instance, message, ref length, ref __result);
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

                var players = MlbGameApiCompat.ReadLobbyPlayers(__instance);
                var requiresExpansion = players.Count >= Const.VanillaPlayerLimit;
                if (!requiresExpansion)
                {
                    if (CanUseVanillaRoster(players) || CanRestoreExtendedRoster(capability))
                        return true;

                    var highSlotPlayers = players
                        .Where(player => player.SlotId is < 0 or >= Const.VanillaPlayerLimit)
                        .Select(player => player.Id)
                        .ToArray();
                    SendRejection(
                        __instance,
                        senderId,
                        new(MlbJoinRejectionReason.UnsafeVanillaRoster, highSlotPlayers),
                        capability != null);
                    return false;
                }

                var rejection = ValidateExpansion(state, players, senderId, capability);
                if (rejection == null)
                    return true;

                SendRejection(__instance, senderId, rejection, capability != null);
                return false;
            }

            private static void Postfix(StartRunLobby __instance, ulong senderId)
            {
                if (__instance.NetService.Type != NetGameType.Host ||
                    MlbGameApiCompat.ReadLobbyPlayers(__instance).All(player => player.Id != senderId))
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

        private readonly record struct PlayerListSerializeState(List<MlbLobbyPlayerData> FullPlayers);

        private static class ClientLobbyJoinResponseNetMessageBusPatch
        {
            [HarmonyPriority(Priority.First)]
            internal static void Prefix(ref ClientLobbyJoinResponseMessage message,
                out PlayerListSerializeState __state)
            {
                var fullPlayers = MlbGameApiCompat.ReadPlayers(message);
                __state = new(fullPlayers);
                MlbGameApiCompat.WritePlayers(ref message, CreateVanillaProjection(fullPlayers));
            }

            [HarmonyPriority(Priority.Last)]
            internal static void Postfix(
                NetMessageBus __instance,
                ClientLobbyJoinResponseMessage message,
                ref int length,
                ref byte[] __result,
                PlayerListSerializeState __state)
            {
                MlbGameApiCompat.WritePlayers(ref message, __state.FullPlayers);
                FinishMessageTail(__instance, message, ref length, ref __result);
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
                var netService = MlbGameApiCompat.GetNetService(__instance);
                var payload = MlbInboundPayloads.DequeueJoinResponse();
                if (payload?.Rejection is { } rejection)
                {
                    MlbLobbyToasts.ShowClientRejection(netService, rejection);
                    netService.Disconnect(NetError.ModMismatch);
                    return false;
                }

                if (payload?.Snapshot is { } snapshot)
                {
                    MlbLobbyProtocolRegistry.StageClientSnapshot(netService, snapshot);
                    return true;
                }

                if (MlbLobbyProtocolRegistry.GetRemoteHostCapability(netService) != null)
                {
                    MlbLobbyToasts.ShowClientRejection(
                        netService,
                        new(MlbJoinRejectionReason.ProtocolMismatch, []));
                    netService.Disconnect(NetError.ModMismatch);
                    return false;
                }

                return true;
            }
        }

        private readonly record struct PlayerJoinedSerializeState(MlbLobbyPlayerData FullPlayer);

        private static class PlayerJoinedNetMessageBusPatch
        {
            [HarmonyPriority(Priority.First)]
            internal static void Prefix(
                ref PlayerJoinedMessage message,
                out PlayerJoinedSerializeState __state)
            {
                var fullPlayer = MlbGameApiCompat.ReadPlayer(message);
                __state = new(fullPlayer);
                if (fullPlayer.SlotId < Const.VanillaPlayerLimit)
                    return;

                var placeholder = fullPlayer with { Id = 0, SlotId = 0 };
                MlbGameApiCompat.WritePlayer(ref message, placeholder);
            }

            [HarmonyPriority(Priority.Last)]
            internal static void Postfix(
                NetMessageBus __instance,
                ref int length,
                ref byte[] __result,
                PlayerJoinedSerializeState __state)
            {
                var message = default(PlayerJoinedMessage);
                MlbGameApiCompat.WritePlayer(ref message, __state.FullPlayer);
                FinishMessageTail(__instance, message, ref length, ref __result);
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

            [HarmonyPriority(Priority.First)]
            private static bool Prefix(StartRunLobby __instance, PlayerJoinedMessage message)
            {
                if (MlbGameApiCompat.ReadPlayer(message).Id == 0)
                {
                    RejectUnsafeExpandedMessage(__instance);
                    return false;
                }

                if (MlbInboundPayloads.DequeuePlayerJoined() is not { } payload)
                    return true;
                MlbLobbyProtocolRegistry.GetOrCreate(__instance)
                    .RecordCapability(payload.Capability.PeerId, payload.Capability.Capability);
                return true;
            }
        }

        private static class LobbyBeginRunNetMessageBusPatch
        {
            [HarmonyPriority(Priority.First)]
            internal static void Prefix(ref LobbyBeginRunMessage message)
            {
                var fullPlayers = MlbGameApiCompat.ReadPlayers(message);
                MlbGameApiCompat.WritePlayers(ref message, CreateVanillaProjection(fullPlayers));
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
            private static bool Prefix(StartRunLobby __instance, ref LobbyBeginRunMessage message)
            {
                var snapshot = MlbInboundPayloads.DequeueBeginRun();
                var state = MlbLobbyProtocolRegistry.GetOrCreate(__instance);
                var lobbyPlayers = MlbGameApiCompat.ReadLobbyPlayers(__instance);
                if (snapshot == null)
                {
                    if (state.ExtendedProtocolActive || lobbyPlayers.Count > Const.VanillaPlayerLimit)
                    {
                        RejectUnsafeExpandedMessage(__instance);
                        return false;
                    }

                    return true;
                }

                if (snapshot.FullPlayers is { } fullPlayers)
                {
                    if (!HasSameRoster(lobbyPlayers, fullPlayers))
                    {
                        RejectUnsafeExpandedMessage(__instance);
                        return false;
                    }

                    MlbGameApiCompat.WritePlayers(ref message, fullPlayers);
                }
                else if (lobbyPlayers.Count > Const.VanillaPlayerLimit ||
                         snapshot.Capabilities.Count > Const.VanillaPlayerLimit)
                {
                    RejectUnsafeExpandedMessage(__instance);
                    return false;
                }

                state.ApplySnapshot(snapshot);
                RuntimeMultiplayerSettings.ApplyRemoteHostSettings(snapshot.ExtraPlayerScalingMultiplier);
                return true;
            }

            private static bool HasSameRoster(
                IReadOnlyList<MlbLobbyPlayerData> lobbyPlayers,
                IReadOnlyList<MlbLobbyPlayerData> snapshotPlayers)
            {
                return lobbyPlayers.Count == snapshotPlayers.Count &&
                       lobbyPlayers.Select(player => player.Id).ToHashSet()
                           .SetEquals(snapshotPlayers.Select(player => player.Id));
            }
        }

        private static void RejectUnsafeExpandedMessage(StartRunLobby lobby)
        {
            MlbLobbyToasts.ShowClientRejection(
                lobby.NetService,
                new(MlbJoinRejectionReason.ProtocolMismatch, []));
            lobby.NetService.Disconnect(NetError.ModMismatch);
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

                var players = MlbGameApiCompat.ReadLobbyPlayers(__instance);
                var wasAccepted = players.Any(player => player.Id == playerId);
                var hadUnsupported = wasAccepted && state.GetCapability(playerId) == null;
                var remainingPlayers = players.Where(player => player.Id != playerId).ToArray();
                var restoredVanillaAdmission = wasAccepted &&
                                               !CanUseVanillaRoster(players) &&
                                               remainingPlayers.Length < Const.VanillaPlayerLimit &&
                                               CanUseVanillaRoster(remainingPlayers);
                state.RemovePeer(playerId);
                if (hadUnsupported &&
                    remainingPlayers.All(player => state.GetCapability(player.Id) != null))
                    MlbLobbyToasts.ShowExpansionAvailable(__instance.NetService);
                if (restoredVanillaAdmission)
                    MlbLobbyToasts.ShowVanillaAdmissionRestored(__instance.NetService);
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
