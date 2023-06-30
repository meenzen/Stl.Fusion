using Stl.Rpc.Infrastructure;

namespace Stl.Rpc;

public class RpcServerPeer : RpcPeer
{
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromMinutes(1);

    public RpcServerPeer(RpcHub hub, RpcPeerRef @ref)
        : base(hub, @ref)
        => LocalServiceFilter = static serviceDef => !serviceDef.IsBackend;

    public async Task Connect(RpcConnection connection, CancellationToken cancellationToken = default)
    {
        var connectionState = ConnectionState.LatestOrThrow();
        if (connectionState.Value.IsConnected()) {
            Disconnect();
            using var cts = cancellationToken.LinkWith(StopToken);
            await connectionState.WhenDisconnected(cts.Token).ConfigureAwait(false);
        }
        SetConnectionState(connection, null);
    }

    // Protected methods

    protected override async Task<RpcConnection> GetConnection(CancellationToken cancellationToken)
    {
        while (true) {
            var cts = cancellationToken.CreateLinkedTokenSource();
            try {
                await ConnectionState
                    .WhenConnected(cts.Token)
                    .WaitAsync(CloseTimeout, cancellationToken)
                    .ConfigureAwait(false);

                var connectionState = ConnectionState.LatestOrThrow().Value;
                if (connectionState.Connection != null)
                    return connectionState.Connection;
            }
            finally {
                cts.CancelAndDisposeSilently();
            }
        }
    }
}
