using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace STS2MultiplayerLimitBreak.Network
{
    internal static class MultiplayerLimitPatches
    {
        private static readonly FieldInfo? MaxPlayersField =
            AccessTools.Field(typeof(StartRunLobby), "<MaxPlayers>k__BackingField");

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<StartENetHostMaxClientsPatch>();
            patcher.RegisterPatch<StartSteamHostMaxClientsPatch>();
            patcher.RegisterPatch<ConnectedToClientAsHostMaxPlayersPatch>();
            patcher.RegisterPatch<ClientLobbyJoinRequestMaxPlayersPatch>();
            patcher.RegisterPatch<HostPeerReadySettingsSyncPatch>();
            patcher.RegisterPatch<ClientInitializeSettingsResetPatch>();
            patcher.RegisterPatch<ClientDisconnectedSettingsResetPatch>();
        }

        private static void SetLobbyLimit(StartRunLobby lobby)
        {
            if (!RuntimeMultiplayerSettings.LimitBreakEnabled || lobby.NetService.Type != NetGameType.Host) return;

            RuntimeMultiplayerSettings.PublishHostSettings(lobby.NetService, "lobby_limit");

            if (lobby.MaxPlayers != Const.PlayerLimit) MaxPlayersField?.SetValue(lobby, Const.PlayerLimit);

            SteamLobbyMemberLimit.TryUpdate(lobby.NetService, Const.PlayerLimit);
        }

        private sealed class StartENetHostMaxClientsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_start_enet_host_max_clients";

            public static string Description => "Raise ENet host client limit";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NetHostGameService), nameof(NetHostGameService.StartENetHost)),
                ];
            }

            private static void Prefix(NetHostGameService __instance, ref int maxClients)
            {
                RuntimeMultiplayerSettings.PublishHostSettings(__instance, "start_enet_host");
                if (RuntimeMultiplayerSettings.LimitBreakEnabled) maxClients = Math.Max(maxClients, Const.PlayerLimit);
            }
        }

        private sealed class StartSteamHostMaxClientsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_start_steam_host_max_clients";

            public static string Description => "Raise Steam host client limit";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NetHostGameService), nameof(NetHostGameService.StartSteamHost)),
                ];
            }

            private static void Prefix(NetHostGameService __instance, ref int maxClients)
            {
                RuntimeMultiplayerSettings.PublishHostSettings(__instance, "start_steam_host");
                if (RuntimeMultiplayerSettings.LimitBreakEnabled) maxClients = Math.Max(maxClients, Const.PlayerLimit);
            }
        }

        private sealed class ConnectedToClientAsHostMaxPlayersPatch : IPatchMethod
        {
            public static string PatchId => "mlb_connected_to_client_as_host_max_players";

            public static string Description => "Keep StartRunLobby MaxPlayers raised before host accepts clients";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(StartRunLobby), "OnConnectedToClientAsHost"),
                ];
            }

            private static void Prefix(StartRunLobby __instance)
            {
                SetLobbyLimit(__instance);
            }
        }

        private sealed class ClientLobbyJoinRequestMaxPlayersPatch : IPatchMethod
        {
            public static string PatchId => "mlb_client_lobby_join_request_max_players";

            public static string Description => "Keep StartRunLobby MaxPlayers raised before join request handling";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(StartRunLobby), "HandleClientLobbyJoinRequestMessage"),
                ];
            }

            private static void Prefix(StartRunLobby __instance)
            {
                SetLobbyLimit(__instance);
            }
        }

        private sealed class HostPeerReadySettingsSyncPatch : IPatchMethod
        {
            public static string PatchId => "mlb_host_peer_ready_settings_sync";

            public static string Description => "Broadcast host limit-break settings after a peer becomes broadcast-ready";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NetHostGameService), nameof(NetHostGameService.SetPeerReadyForBroadcasting)),
                ];
            }

            private static void Postfix(NetHostGameService __instance)
            {
                RuntimeMultiplayerSettings.PublishHostSettings(__instance, "peer_ready");
            }
        }

        private sealed class ClientInitializeSettingsResetPatch : IPatchMethod
        {
            public static string PatchId => "mlb_client_initialize_settings_reset";

            public static string Description => "Clear cached host settings before a client connection starts";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NetClientGameService), nameof(NetClientGameService.Initialize)),
                ];
            }

            private static void Prefix()
            {
                RuntimeMultiplayerSettings.ClearRemoteHostSettings();
            }
        }

        private sealed class ClientDisconnectedSettingsResetPatch : IPatchMethod
        {
            public static string PatchId => "mlb_client_disconnected_settings_reset";

            public static string Description => "Clear cached host settings after client disconnect";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost)),
                ];
            }

            private static void Postfix()
            {
                RuntimeMultiplayerSettings.ClearRemoteHostSettings();
            }
        }
    }
}
