using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using STS2MultiplayerLimitBreak.Settings;

namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal sealed class MlbLobbyProtocolState
    {
        private readonly Dictionary<ulong, MlbPeerCapability?> _capabilities = [];

        public bool ExtendedProtocolActive { get; private set; }

        public byte SelectedProtocol { get; private set; }

        public void RecordCapability(ulong peerId, MlbPeerCapability? capability)
        {
            _capabilities[peerId] = capability;
        }

        public void RemovePeer(ulong peerId)
        {
            _capabilities.Remove(peerId);
        }

        public MlbPeerCapability? GetCapability(ulong peerId)
        {
            return _capabilities.GetValueOrDefault(peerId);
        }

        public void ApplySnapshot(MlbLobbySnapshot snapshot)
        {
            _capabilities.Clear();
            foreach (var entry in snapshot.Capabilities)
                _capabilities[entry.PeerId] = entry.Capability;
            ExtendedProtocolActive = snapshot.ExtendedProtocolActive;
            SelectedProtocol = snapshot.SelectedProtocol;
        }

        public MlbLobbySnapshot CreateSnapshot(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer> players,
            bool includePlayers)
        {
            var entries = players
                .Select(player => new MlbPeerCapabilityEntry(player.id, GetCapability(player.id)))
                .ToList();
            return new(
                ExtendedProtocolActive,
                SelectedProtocol,
                RuntimeMultiplayerSettings.ExtraPlayerScalingMultiplier,
                entries,
                includePlayers ? players.ToList() : null);
        }

        public bool TryActivate(
            IEnumerable<ulong> peerIds,
            out byte selectedProtocol,
            out ulong[] incompatiblePeers)
        {
            var min = MlbPeerCapability.Local.MinProtocol;
            var max = MlbPeerCapability.Local.MaxProtocol;
            var incompatible = new List<ulong>();

            foreach (var peerId in peerIds.Distinct())
            {
                if (!_capabilities.TryGetValue(peerId, out var capability) || capability == null)
                {
                    incompatible.Add(peerId);
                    continue;
                }

                min = Math.Max(min, capability.Value.MinProtocol);
                max = Math.Min(max, capability.Value.MaxProtocol);
            }

            if (incompatible.Count > 0 || min > max)
            {
                if (incompatible.Count == 0)
                    incompatible.AddRange(peerIds.Where(peerId =>
                        GetCapability(peerId) is not { } capability ||
                        !capability.Supports(MlbPeerCapability.Local.MaxProtocol)));
                selectedProtocol = 0;
                incompatiblePeers = [.. incompatible.Distinct()];
                return false;
            }

            selectedProtocol = (byte)max;
            incompatiblePeers = [];
            ExtendedProtocolActive = true;
            SelectedProtocol = selectedProtocol;
            return true;
        }
    }

    internal static class MlbLobbyProtocolRegistry
    {
        private static readonly ConditionalWeakTable<StartRunLobby, MlbLobbyProtocolState> States = new();
        private static readonly ConditionalWeakTable<object, SnapshotHolder> PendingClientSnapshots = new();
        private static readonly ConditionalWeakTable<object, CapabilityHolder> RemoteHostCapabilities = new();
        private static readonly Lock Gate = new();
        private static WeakReference<MlbLobbyProtocolState>? _currentHostState;

        public static MlbLobbyProtocolState GetOrCreate(StartRunLobby lobby)
        {
            var state = States.GetValue(lobby, static currentLobby =>
            {
                var created = new MlbLobbyProtocolState();
                created.RecordCapability(currentLobby.NetService.NetId, MlbPeerCapability.Local);
                return created;
            });

            if (lobby.NetService.Type == NetGameType.Host)
                lock (Gate)
                    _currentHostState = new(state);

            if (lobby.NetService.Type == NetGameType.Client &&
                PendingClientSnapshots.TryGetValue(lobby.NetService, out var holder))
            {
                state.ApplySnapshot(holder.Snapshot);
                RuntimeMultiplayerSettings.ApplyRemoteHostSettings(holder.Snapshot.ExtraPlayerScalingMultiplier);
                PendingClientSnapshots.Remove(lobby.NetService);
            }

            return state;
        }

        public static bool TryGet(StartRunLobby lobby, out MlbLobbyProtocolState state)
        {
            return States.TryGetValue(lobby, out state!);
        }

        public static MlbLobbyProtocolState? TryGetCurrentHostState()
        {
            lock (Gate)
                return _currentHostState != null && _currentHostState.TryGetTarget(out var state) ? state : null;
        }

        public static void StageClientSnapshot(INetGameService netService, MlbLobbySnapshot snapshot)
        {
            PendingClientSnapshots.Remove(netService);
            PendingClientSnapshots.Add(netService, new(snapshot));
            RuntimeMultiplayerSettings.ApplyRemoteHostSettings(snapshot.ExtraPlayerScalingMultiplier);
        }

        public static void SetRemoteHostCapability(INetGameService netService, MlbPeerCapability? capability)
        {
            RemoteHostCapabilities.Remove(netService);
            RemoteHostCapabilities.Add(netService, new(capability));
        }

        public static MlbPeerCapability? GetRemoteHostCapability(INetGameService netService)
        {
            return RemoteHostCapabilities.TryGetValue(netService, out var holder) ? holder.Capability : null;
        }

        public static void CleanUp(StartRunLobby lobby)
        {
            if (States.TryGetValue(lobby, out var state))
            {
                lock (Gate)
                    if (_currentHostState != null &&
                        _currentHostState.TryGetTarget(out var current) &&
                        ReferenceEquals(current, state))
                        _currentHostState = null;
            }

            States.Remove(lobby);
            PendingClientSnapshots.Remove(lobby.NetService);
            RemoteHostCapabilities.Remove(lobby.NetService);
            MlbLobbyToasts.ClearSession();
        }

        private sealed record SnapshotHolder(MlbLobbySnapshot Snapshot);

        private sealed record CapabilityHolder(MlbPeerCapability? Capability);
    }
}
