using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace STS2MultiplayerLimitBreak.Network
{
    internal static class SteamLobbyMemberLimit
    {
        public static void TryUpdate(INetGameService netService, int limit)
        {
            try
            {
                if (netService is not NetHostGameService hostService) return;

                object? netHost = hostService.NetHost;
                if (netHost == null) return;

                var lobbyIdProperty = AccessTools.Property(netHost.GetType(), "LobbyId");
                var lobbyId = lobbyIdProperty?.GetValue(netHost);
                if (lobbyId == null) return;

                var steamMatchmakingType = lobbyId.GetType().Assembly.GetType("Steamworks.SteamMatchmaking");
                var setLimitMethod = steamMatchmakingType?.GetMethod("SetLobbyMemberLimit");
                setLimitMethod?.Invoke(null, [lobbyId, limit]);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to update Steam lobby member limit: {ex.Message}");
            }
        }
    }
}
