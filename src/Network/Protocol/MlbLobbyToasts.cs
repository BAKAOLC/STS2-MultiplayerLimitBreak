using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Ui.Toast;

namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal static class MlbLobbyToasts
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<string, DateTime> LastShownByKey = [];
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

        public static void ShowIncompatibleAccepted(INetGameService netService, ulong peerId)
        {
            if (netService.Type != NetGameType.Host || !ShouldShow($"accepted:{peerId}"))
                return;

            var name = GetPlayerName(netService, peerId);
            ShowWarning(
                Format("toast.incompatibleAccepted.body",
                    "Player {0} does not support the current multiplayer extension protocol. The room cannot safely expand beyond 4 players while they remain.",
                    name),
                Text("toast.incompatibleAccepted.title", "Expansion limited"));
        }

        public static void ShowHostRejection(
            INetGameService netService,
            ulong joiningPeerId,
            MlbJoinRejection rejection)
        {
            if (netService.Type != NetGameType.Host)
                return;

            var joiningName = GetPlayerName(netService, joiningPeerId);
            var blockerNames = rejection.BlockingPeerIds
                .Where(peerId => peerId != joiningPeerId)
                .Select(peerId => GetPlayerName(netService, peerId))
                .ToArray();
            var blockerKey = string.Join(",", rejection.BlockingPeerIds.Order());
            if (!ShouldShow($"rejected:{joiningPeerId}:{rejection.Reason}:{blockerKey}"))
                return;

            if (rejection.Reason == MlbJoinRejectionReason.ExistingIncompatiblePeers && blockerNames.Length > 0)
            {
                ShowWarning(
                    Format("toast.expansionBlocked.body",
                        "The room already contains incompatible players: {0}. {1}'s join was rejected to prevent unsafe expansion.",
                        string.Join(", ", blockerNames),
                        joiningName),
                    Text("toast.expansionBlocked.title", "Cannot expand room"));
                return;
            }

            if (rejection.Reason == MlbJoinRejectionReason.UnsafeVanillaRoster)
            {
                ShowWarning(
                    Format("toast.unsafeVanillaRosterRejected.body",
                        "The room still contains players in expanded slots: {0}. {1}'s join was rejected because their client cannot safely reconstruct this lobby state.",
                        blockerNames.Length > 0 ? string.Join(", ", blockerNames) : "unknown players",
                        joiningName),
                    Text("toast.unsafeVanillaRosterRejected.title", "Unsafe original-client join rejected"));
                return;
            }

            var bodyKey = rejection.Reason == MlbJoinRejectionReason.ProtocolMismatch
                ? "toast.protocolMismatchRejected.body"
                : "toast.unsupportedRejected.body";
            var bodyFallback = rejection.Reason == MlbJoinRejectionReason.ProtocolMismatch
                ? "Player {0} uses an incompatible multiplayer extension protocol and was rejected."
                : "Player {0} does not support the required multiplayer extension protocol and was rejected.";
            ShowWarning(
                Format(bodyKey, bodyFallback, joiningName),
                Text("toast.unsupportedRejected.title", "Incompatible player rejected"));
        }

        public static void ShowClientRejection(
            INetGameService netService,
            MlbJoinRejection rejection)
        {
            var blockerNames = rejection.BlockingPeerIds
                .Select(peerId => GetPlayerName(netService, peerId))
                .ToArray();
            var body = rejection.Reason switch
            {
                MlbJoinRejectionReason.ExistingIncompatiblePeers when blockerNames.Length > 0 =>
                    Format("toast.clientExistingBlockers.body",
                        "The room cannot safely expand because it contains incompatible players: {0}. The host rejected your join.",
                        string.Join(", ", blockerNames)),
                MlbJoinRejectionReason.ProtocolMismatch =>
                    Text("toast.clientProtocolMismatch.body",
                        "Your multiplayer extension protocol is incompatible with this room. The host rejected your join."),
                MlbJoinRejectionReason.UnsafeVanillaRoster =>
                    Format("toast.clientUnsafeVanillaRoster.body",
                        "This room still contains players in expanded slots: {0}. Your client cannot safely reconstruct the current lobby state, so the host rejected your join.",
                        blockerNames.Length > 0 ? string.Join(", ", blockerNames) : "unknown players"),
                _ => Text("toast.clientUnsupported.body",
                    "This room requires a supported multiplayer extension protocol. The host rejected your join."),
            };

            ShowWarning(body, Text("toast.clientRejected.title", "Unable to join expanded room"));
        }

        public static void ShowExpansionAvailable(INetGameService netService)
        {
            if (netService.Type != NetGameType.Host || !ShouldShow("expansion_available"))
                return;

            RitsuToastService.ShowInfo(
                Text("toast.expansionAvailable.body",
                    "All remaining players support multiplayer expansion. The room can now safely hold up to 16 players."),
                Text("toast.expansionAvailable.title", "Room can now expand"));
        }

        public static void ShowVanillaAdmissionRestored(INetGameService netService)
        {
            if (netService.Type != NetGameType.Host || !ShouldShow("vanilla_admission_restored"))
                return;

            RitsuToastService.ShowInfo(
                Text("toast.vanillaAdmissionRestored.body",
                    "No players remain in expanded slots. Original clients can now safely join while the room has fewer than 4 players."),
                Text("toast.vanillaAdmissionRestored.title", "Original-client joining restored"));
        }

        public static void ClearSession()
        {
            lock (Gate)
                LastShownByKey.Clear();
        }

        private static bool ShouldShow(string key)
        {
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (LastShownByKey.TryGetValue(key, out var last) && now - last < RepeatWindow)
                    return false;
                LastShownByKey[key] = now;
                return true;
            }
        }

        internal static string GetPlayerName(INetGameService netService, ulong peerId)
        {
            try
            {
                var value = PlatformUtil.GetPlayerNameRaw(netService.Platform, peerId)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();
                if (string.IsNullOrWhiteSpace(value))
                    return $"Player {peerId}";
                return value.Length <= 48 ? value : value[..48] + "…";
            }
            catch
            {
                return $"Player {peerId}";
            }
        }

        private static void ShowWarning(string body, string title)
        {
            RitsuToastService.ShowWarning(body, title);
        }

        private static string Text(string key, string fallback)
        {
            return ModSettingsLocalization.Get(key, fallback);
        }

        private static string Format(string key, string fallback, params object[] args)
        {
            return string.Format(Text(key, fallback), args);
        }
    }
}
