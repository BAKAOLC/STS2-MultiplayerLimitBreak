using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using STS2MultiplayerLimitBreak.Settings;

namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal sealed class MlbLobbyProtocolState
    {
        private readonly Dictionary<ulong, MlbPeerCapability?> _capabilities = [];

        public event Action? Changed;

        public bool ExtendedProtocolActive { get; private set; }

        public byte SelectedProtocol { get; private set; }

        public void RecordCapability(ulong peerId, MlbPeerCapability? capability)
        {
            if (_capabilities.TryGetValue(peerId, out var existing) && existing == capability)
                return;

            _capabilities[peerId] = capability;
            Changed?.Invoke();
        }

        public void RemovePeer(ulong peerId)
        {
            if (_capabilities.Remove(peerId))
                Changed?.Invoke();
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
            Changed?.Invoke();
        }

        public MlbExpansionStatus GetExpansionStatus(IEnumerable<ulong> peerIds)
        {
            var distinctPeerIds = peerIds.Distinct().ToArray();
            if (ExtendedProtocolActive && distinctPeerIds.Length > Const.VanillaPlayerLimit)
                return new(MlbExpansionAvailability.Active, SelectedProtocol, []);

            var bestProtocol = MlbPeerCapability.Local.MaxProtocol;
            ulong[]? bestBlockers = null;

            for (var protocol = MlbPeerCapability.Local.MinProtocol;
                 protocol <= MlbPeerCapability.Local.MaxProtocol;
                 protocol++)
            {
                var blockers = distinctPeerIds
                    .Where(peerId => GetCapability(peerId) is not { } capability ||
                                     !capability.Supports(protocol))
                    .ToArray();
                if (bestBlockers == null || blockers.Length < bestBlockers.Length ||
                    blockers.Length == bestBlockers.Length && protocol > bestProtocol)
                {
                    bestProtocol = protocol;
                    bestBlockers = blockers;
                }

                if (protocol == byte.MaxValue)
                    break;
            }

            bestBlockers ??= distinctPeerIds;
            return bestBlockers.Length == 0
                ? new(MlbExpansionAvailability.Available, bestProtocol, [])
                : new(MlbExpansionAvailability.Blocked, 0, bestBlockers);
        }

        public MlbLobbySnapshot CreateSnapshot(
            IReadOnlyList<MlbLobbyPlayerData> players,
            bool includePlayers)
        {
            var entries = players
                .Select(player => new MlbPeerCapabilityEntry(player.Id, GetCapability(player.Id)))
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
            out ulong[] incompatiblePeers)
        {
            var status = GetExpansionStatus(peerIds);
            if (status.Availability == MlbExpansionAvailability.Blocked)
            {
                incompatiblePeers = [.. status.BlockingPeerIds];
                return false;
            }

            incompatiblePeers = [];
            var selectedProtocol = status.SelectedProtocol;
            if (ExtendedProtocolActive && SelectedProtocol == selectedProtocol)
                return true;

            ExtendedProtocolActive = true;
            SelectedProtocol = selectedProtocol;
            Changed?.Invoke();
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
        private static WeakReference<StartRunLobby>? _currentHostLobby;

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
                {
                    _currentHostState = new(state);
                    _currentHostLobby = new(lobby);
                }

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

        public static StartRunLobby? TryGetCurrentHostLobby()
        {
            lock (Gate)
                return _currentHostLobby != null && _currentHostLobby.TryGetTarget(out var lobby) ? lobby : null;
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
                    {
                        _currentHostState = null;
                        _currentHostLobby = null;
                    }
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
