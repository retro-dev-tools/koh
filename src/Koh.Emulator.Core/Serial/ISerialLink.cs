namespace Koh.Emulator.Core.Serial;

/// <summary>
/// Optional peer for link-cable transfers. When a <see cref="Serial"/> has a
/// link attached, every internal-clock transfer exchanges a byte with the peer
/// instead of shifting in $FF from the open bus. Implementations are expected
/// to be synchronous from the guest's point of view — the guest only sees the
/// exchanged byte after the shift countdown expires, so async transports
/// (WebRTC, TCP) should buffer internally and block one side if needed.
/// </summary>
public interface ISerialLink
{
    /// <summary>
    /// Send <paramref name="sent"/> to the peer and return the peer's byte.
    /// Called once per 8-bit transfer, at the moment the guest starts
    /// shifting (i.e. when SC bit 7 is set with internal clock).
    /// </summary>
    byte ExchangeByte(byte sent);
}
