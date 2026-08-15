namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal enum MlbExpansionAvailability
    {
        Available,
        Blocked,
        Active,
    }

    internal readonly record struct MlbExpansionStatus(
        MlbExpansionAvailability Availability,
        byte SelectedProtocol,
        IReadOnlyList<ulong> BlockingPeerIds);

    internal enum MlbJoinRejectionReason : byte
    {
        ExistingIncompatiblePeers = 1,
        JoiningPeerUnsupported = 2,
        ProtocolMismatch = 3,
        ExtendedSessionRequiresProtocol = 4,
        UnsafeVanillaRoster = 5,
    }

    internal readonly record struct MlbPeerCapability(
        byte MinProtocol,
        byte MaxProtocol,
        string ModVersion)
    {
        public static MlbPeerCapability Local => new(
            Const.WireProtocolVersion,
            Const.WireProtocolVersion,
            Const.Version);

        public bool Supports(byte protocol)
        {
            return protocol >= MinProtocol && protocol <= MaxProtocol;
        }

        public bool IsCompatibleWith(MlbPeerCapability other)
        {
            return MinProtocol <= other.MaxProtocol && other.MinProtocol <= MaxProtocol;
        }
    }
}
