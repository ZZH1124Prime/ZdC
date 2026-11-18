using System.Net;
using System.Net.Sockets;
namespace TrafikRuler.Core;

internal interface INatPortLease : IDisposable { ushort Port { get; } }
internal interface INatPortAllocator { INatPortLease Reserve(byte protocol, UInt128 address); }

// Used only when an explicitly started capture session creates a selected NAT flow.
// An exclusive bind reserves the translated port against normal Windows socket allocation.
internal sealed class SocketNatPortAllocator : INatPortAllocator
{
    public INatPortLease Reserve(byte protocol, UInt128 address)
    {
        var ip = Addr.ToIPAddress(address);
        var udp = protocol == Packet.ProtoUdp;
        var socket = new Socket(ip.AddressFamily, udp ? SocketType.Dgram : SocketType.Stream,
            udp ? ProtocolType.Udp : ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            socket.Bind(new IPEndPoint(ip, 0));
            return new Lease(socket, checked((ushort)((IPEndPoint)socket.LocalEndPoint!).Port));
        }
        catch { socket.Dispose(); throw; }
    }
    private sealed class Lease(Socket socket, ushort port) : INatPortLease
    {
        public ushort Port { get; } = port;
        public void Dispose() => socket.Dispose();
    }
}
