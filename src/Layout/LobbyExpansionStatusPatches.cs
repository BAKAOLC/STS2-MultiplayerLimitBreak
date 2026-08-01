using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using STS2MultiplayerLimitBreak.Network.Protocol;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace STS2MultiplayerLimitBreak.Layout
{
    internal static class LobbyExpansionStatusPatches
    {
        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<RemotePlayerContainerInitializePatch>();
            patcher.RegisterPatch<RemotePlayerContainerChangedPatch>();
        }

        private sealed class RemotePlayerContainerChangedPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_expansion_status_refresh";

            public static string Description => "Refresh expansion compatibility after lobby player changes";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NRemoteLobbyPlayerContainer), nameof(NRemoteLobbyPlayerContainer.OnPlayerConnected),
                        [MlbGameApiCompat.RuntimePlayerType]),
                    new(typeof(NRemoteLobbyPlayerContainer), nameof(NRemoteLobbyPlayerContainer.OnPlayerDisconnected),
                        [MlbGameApiCompat.RuntimePlayerType]),
                ];
            }

            private static void Postfix(NRemoteLobbyPlayerContainer __instance)
            {
                MlbLobbyExpansionStatusPanel.RefreshAttached(__instance);
            }
        }

        private sealed class RemotePlayerContainerInitializePatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_expansion_status";

            public static string Description => "Show persistent expansion compatibility in multiplayer lobbies";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NRemoteLobbyPlayerContainer), nameof(NRemoteLobbyPlayerContainer.Initialize),
                        [typeof(StartRunLobby), typeof(bool)]),
                ];
            }

            private static void Postfix(NRemoteLobbyPlayerContainer __instance, StartRunLobby lobby)
            {
                MlbLobbyExpansionStatusPanel.AttachOrRebind(__instance, lobby);
            }
        }
    }

    internal sealed partial class MlbLobbyExpansionStatusPanel : PanelContainer
    {
        private const string NodeName = "MlbExpansionStatus";
        private static readonly ConditionalWeakTable<NRemoteLobbyPlayer, MlbLobbyBlockerMarker> PlayerMarkers = new();

        private readonly MegaLabel _label = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill,
            MouseFilter = MouseFilterEnum.Stop,
            MinFontSize = 12,
            MaxFontSize = 22,
        };

        private NRemoteLobbyPlayerContainer? _playerContainer;
        private StartRunLobby? _lobby;
        private MlbLobbyProtocolState? _state;
        private bool _reportedRefreshFailure;

        public static void AttachOrRebind(NRemoteLobbyPlayerContainer container, StartRunLobby lobby)
        {
            var panel = container.GetNodeOrNull<MlbLobbyExpansionStatusPanel>(NodeName);
            if (lobby.NetService.Type == NetGameType.Singleplayer)
            {
                panel?.Deactivate();
                return;
            }

            if (panel == null)
            {
                panel = new()
                {
                    Name = NodeName,
                    MouseFilter = MouseFilterEnum.Pass,
                    ZIndex = 20,
                    AnchorLeft = 0f,
                    AnchorTop = 0f,
                    AnchorRight = 1f,
                    AnchorBottom = 0f,
                    OffsetLeft = 4f,
                    OffsetTop = -42f,
                    OffsetRight = -4f,
                    OffsetBottom = -6f,
                };
                container.AddChildSafely(panel);
            }

            panel.Bind(container, lobby);
        }

        public static void RefreshAttached(NRemoteLobbyPlayerContainer container)
        {
            container.GetNodeOrNull<MlbLobbyExpansionStatusPanel>(NodeName)?.RefreshSafely();
        }

        public override void _Ready()
        {
            AddThemeStyleboxOverride("panel", CreatePanelStyle(new(0.18f, 0.58f, 0.38f, 1f)));
            ApplyResolvedGameFont();
            _label.AddThemeColorOverride("font_color", new(0.95f, 0.97f, 0.94f, 1f));
            this.AddChildSafely(_label);
            RefreshSafely();
        }

        public override void _ExitTree()
        {
            Unsubscribe();
        }

        private void Bind(NRemoteLobbyPlayerContainer container, StartRunLobby lobby)
        {
            Unsubscribe();
            Visible = true;
            _reportedRefreshFailure = false;
            _playerContainer = container;
            _lobby = lobby;
            _state = MlbLobbyProtocolRegistry.GetOrCreate(lobby);
            _state.Changed += OnLobbyChanged;
            RefreshSafely();
        }

        private void Deactivate()
        {
            Unsubscribe();
            Visible = false;
        }

        private void Unsubscribe()
        {
            if (_state != null)
                _state.Changed -= OnLobbyChanged;
            _state = null;
            _lobby = null;
        }

        private void OnLobbyChanged()
        {
            RefreshSafely();
        }

        private void RefreshSafely()
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                if (_reportedRefreshFailure)
                    return;
                _reportedRefreshFailure = true;
                Log.Warn($"Failed to refresh the lobby expansion status: {ex.Message}");
            }
        }

        private void Refresh()
        {
            if (_lobby == null || _state == null || _playerContainer == null || !IsInsideTree())
                return;

            var players = MlbGameApiCompat.ReadLobbyPlayers(_lobby);
            var status = _state.GetExpansionStatus(players.Select(static player => player.Id));
            var blockerIds = status.BlockingPeerIds.ToHashSet();
            var blockerNames = players
                .Where(player => blockerIds.Contains(player.Id))
                .Select(player => MlbLobbyToasts.GetPlayerName(_lobby.NetService, player.Id))
                .ToArray();

            _label.SetTextAutoSize(status.Availability switch
            {
                MlbExpansionAvailability.Active => Format(
                    "lobbyStatus.active",
                    "[Multiplayer Limit Break] Active ({0}/{1})",
                    players.Count,
                    Const.PlayerLimit),
                MlbExpansionAvailability.Blocked => Format(
                    "lobbyStatus.blocked",
                    "[Multiplayer Limit Break] Disabled by incompatible clients ({0}/{1})",
                    players.Count,
                    Const.VanillaPlayerLimit),
                _ => Format(
                    "lobbyStatus.available",
                    "[Multiplayer Limit Break] Active ({0}/{1})",
                    players.Count,
                    Const.PlayerLimit),
            });

            TooltipText = status.Availability == MlbExpansionAvailability.Blocked
                ? Format(
                    "lobbyStatus.blockedTooltip",
                    "Some clients in this room did not provide a supported Multiplayer Limit Break protocol, so the room limit remains 4. Affected players: {0}",
                    string.Join(", ", blockerNames))
                : status.Availability == MlbExpansionAvailability.Active
                    ? Text("lobbyStatus.activeTooltip",
                        "Expansion has been activated for this room. Additional players are carried by message-tail data, and later joins must provide a supported protocol; the original message body remains unchanged.")
                    : Text("lobbyStatus.availableTooltip",
                        "The original message body remains unchanged. Multiplayer Limit Break will begin carrying additional player data in message tails when player 5 joins with a supported version.");
            _label.TooltipText = TooltipText;

            var accent = status.Availability switch
            {
                MlbExpansionAvailability.Active => new Color(0.26f, 0.66f, 0.88f, 1f),
                MlbExpansionAvailability.Blocked => new Color(0.92f, 0.57f, 0.18f, 1f),
                _ => new Color(0.25f, 0.72f, 0.42f, 1f),
            };
            AddThemeStyleboxOverride("panel", CreatePanelStyle(accent));
            UpdatePlayerMarkers(blockerIds);
        }

        private void ApplyResolvedGameFont()
        {
            var sourceLabel = GetParent()?.GetNodeOrNull<MegaLabel>("%SoloLabel")
                              ?? throw new InvalidOperationException(
                                  "The original lobby SoloLabel was not found for game-font resolution.");
            _label.AddThemeFontOverride("font", sourceLabel.GetThemeFont("font", "Label"));
            _label.AddThemeColorOverride(
                "font_shadow_color",
                sourceLabel.GetThemeColor("font_shadow_color", "Label"));
            _label.AddThemeConstantOverride(
                "shadow_offset_x",
                sourceLabel.GetThemeConstant("shadow_offset_x", "Label"));
            _label.AddThemeConstantOverride(
                "shadow_offset_y",
                sourceLabel.GetThemeConstant("shadow_offset_y", "Label"));
        }

        private void UpdatePlayerMarkers(IReadOnlySet<ulong> blockerIds)
        {
            if (_playerContainer == null)
                return;

            foreach (var playerNode in FindDescendants<NRemoteLobbyPlayer>(_playerContainer))
            {
                var nameplate = playerNode.GetNodeOrNull<MegaLabel>("%NameplateLabel");
                if (nameplate == null)
                    continue;

                if (!PlayerMarkers.TryGetValue(playerNode, out var marker))
                {
                    marker = new()
                    {
                        Name = "MlbExpansionBlockerMarker",
                        Visible = false,
                        MouseFilter = MouseFilterEnum.Stop,
                        ZIndex = 30,
                        AnchorLeft = 0f,
                        AnchorTop = 0.5f,
                        AnchorRight = 0f,
                        AnchorBottom = 0.5f,
                        OffsetLeft = -27f,
                        OffsetTop = -10f,
                        OffsetRight = -7f,
                        OffsetBottom = 10f,
                    };
                    nameplate.AddChildSafely(marker);
                    PlayerMarkers.Add(playerNode, marker);
                }

                marker.Visible = blockerIds.Contains(playerNode.PlayerId);
                if (!marker.Visible)
                    continue;

                marker.TooltipText = _state?.GetCapability(playerNode.PlayerId) == null
                    ? Text("lobbyStatus.playerUnsupportedTooltip",
                        "This player did not provide Multiplayer Limit Break expansion support, so the room limit remains 4.")
                    : Text("lobbyStatus.playerMismatchTooltip",
                        "This player's Multiplayer Limit Break protocol is not supported by this room, so the room limit remains 4.");
            }
        }

        private static IEnumerable<T> FindDescendants<T>(Node root) where T : Node
        {
            foreach (var child in root.GetChildren())
            {
                if (child is T match)
                    yield return match;
                foreach (var descendant in FindDescendants<T>(child))
                    yield return descendant;
            }
        }

        private static StyleBoxFlat CreatePanelStyle(Color accent)
        {
            return new()
            {
                BgColor = new(0.055f, 0.065f, 0.08f, 0.92f),
                BorderColor = accent,
                BorderWidthLeft = 2,
                BorderWidthTop = 2,
                BorderWidthRight = 2,
                BorderWidthBottom = 2,
                CornerRadiusTopLeft = 7,
                CornerRadiusTopRight = 7,
                CornerRadiusBottomRight = 7,
                CornerRadiusBottomLeft = 7,
                ContentMarginLeft = 6f,
                ContentMarginTop = 4f,
                ContentMarginRight = 6f,
                ContentMarginBottom = 4f,
            };
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

    internal sealed partial class MlbLobbyBlockerMarker : Control
    {
        public override void _Draw()
        {
            var points = new[]
            {
                new Vector2(10f, 1.5f),
                new Vector2(19f, 18.5f),
                new Vector2(1f, 18.5f),
            };
            DrawColoredPolygon(points, new(0.96f, 0.58f, 0.12f, 1f));
            DrawPolyline([points[0], points[1], points[2], points[0]],
                new(0.3f, 0.08f, 0.025f, 1f), 1.2f, true);
            DrawLine(new(10f, 6f), new(10f, 12.5f), new(0.19f, 0.055f, 0.02f, 1f), 1.35f, true);
            DrawCircle(new(10f, 15.5f), 0.9f, new(0.19f, 0.055f, 0.02f, 1f), true);
        }
    }
}
