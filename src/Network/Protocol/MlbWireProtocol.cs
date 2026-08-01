namespace STS2MultiplayerLimitBreak.Network.Protocol
{
    internal enum MlbJoinRejectionReason : byte
    {
        ExistingIncompatiblePeers = 1,
        JoiningPeerUnsupported = 2,
        ProtocolMismatch = 3,
        ExtendedSessionRequiresProtocol = 4,
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
    }
}
