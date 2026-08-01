using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
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
        private static readonly FieldInfo NetMessageBusWriterField =
            AccessTools.Field(typeof(NetMessageBus), "_writer")
            ?? throw new MissingFieldException(typeof(NetMessageBus).FullName, "_writer");

        private const string ExtensionId = "sts2.multiplayerLimitBreak";
        private const int ExtensionVersion = 1;
        private static readonly Lock ExtensionRegistrationLock = new();
        private static bool _extensionsRegistered;

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<InitialGameInfoHandlerPatch>();
            patcher.RegisterPatch<ClientLobbyJoinRequestDeserializePatch>();
            patcher.RegisterPatch<ClientLobbyJoinRequestHandlerPatch>();
            patcher.RegisterPatch<OtherLobbyJoinRequestHandlerPatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseDeserializePatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseHandlerPatch>();
            patcher.RegisterPatch<PlayerJoinedDeserializePatch>();
            patcher.RegisterPatch<PlayerJoinedHandlerPatch>();
            patcher.RegisterPatch<LobbyBeginRunDeserializePatch>();
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
                    CreatePatch(
                        "mlb_begin_run_bus_serialize",
                        typeof(LobbyBeginRunMessage),
                        typeof(LobbyBeginRunNetMessageBusPatch)),
                ],
                rollbackOnCriticalFailure: true);

            DynamicPatchInfo CreatePatch(string id, Type messageType, Type patchType)
            {
                var prefix = AccessTools.DeclaredMethod(patchType, "Prefix");
                var postfix = AccessTools.DeclaredMethod(patchType, "Postfix")
                              ?? throw new MissingMethodException(patchType.FullName, "Postfix");
                return new(
                    id,
                    serializeDefinition.MakeGenericMethod(messageType),
                    prefix: prefix == null ? null : new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    description: $"Serialize {messageType.Name} with its MLB tail at the non-inlined message-bus boundary");
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
            var fullPlayers = MlbGameApiCompat.ReadPlayers(message);
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
            var writer = NetMessageBusWriterField.GetValue(messageBus) as PacketWriter
                         ?? throw new InvalidOperationException("NetMessageBus has no active packet writer.");
            RitsuNetMessageTailExtensions.Write(writer, message);
            length = (int)Math.Ceiling((float)writer.BitPosition / 8f);
            result = writer.Buffer;
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
                    MlbGameApiCompat.WritePlayers(ref __instance, fullPlayers);

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
                    MlbGameApiCompat.WritePlayer(ref __instance, player);

                if (MlbGameApiCompat.ReadPlayer(__instance).Id == 0)
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

        private static class LobbyBeginRunNetMessageBusPatch
        {
            [HarmonyPriority(Priority.First)]
            internal static void Prefix(ref LobbyBeginRunMessage message,
                out PlayerListSerializeState __state)
            {
                var fullPlayers = MlbGameApiCompat.ReadPlayers(message);
                __state = new(fullPlayers);
                MlbGameApiCompat.WritePlayers(ref message, CreateVanillaProjection(fullPlayers));
            }

            [HarmonyPriority(Priority.Last)]
            internal static void Postfix(
                NetMessageBus __instance,
                LobbyBeginRunMessage message,
                ref int length,
                ref byte[] __result,
                PlayerListSerializeState __state)
            {
                MlbGameApiCompat.WritePlayers(ref message, __state.FullPlayers);
                FinishMessageTail(__instance, message, ref length, ref __result);
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
                    MlbGameApiCompat.WritePlayers(ref __instance, players);
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
