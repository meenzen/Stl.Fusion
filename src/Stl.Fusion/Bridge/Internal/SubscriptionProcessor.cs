using Stl.Fusion.Bridge.Messages;

namespace Stl.Fusion.Bridge.Internal;

public abstract class SubscriptionProcessor : WorkerBase
{
    private ILogger? _log;

    protected readonly IServiceProvider Services;
    protected readonly MomentClockSet Clocks;
    protected readonly TimeSpan ExpirationTime;
    protected long MessageIndex;
    protected (LTag Version, bool IsConsistent) LastSentVersion;
    protected ILogger Log => _log ??= Services.LogFor(GetType());

    public IPublisher Publisher => Publication.Publisher;
    public readonly IPublication Publication;
    public readonly Channel<BridgeMessage> OutgoingChannel;
    public readonly Channel<ReplicaRequest> IncomingChannel;

    protected SubscriptionProcessor(
        IPublication publication,
        Channel<BridgeMessage> outgoingChannel,
        TimeSpan expirationTime,
        MomentClockSet clocks,
        IServiceProvider services)
    {
        Services = services;
        Clocks = clocks;
        Publication = publication;
        OutgoingChannel = outgoingChannel;
        IncomingChannel = Channel.CreateBounded<ReplicaRequest>(new BoundedChannelOptions(16));
        ExpirationTime = expirationTime;
    }
}

public class SubscriptionProcessor<T> : SubscriptionProcessor
{
    public new readonly IPublication<T> Publication;

    public SubscriptionProcessor(
        IPublication<T> publication,
        Channel<BridgeMessage> outgoingChannel,
        TimeSpan expirationTime,
        MomentClockSet clocks,
        IServiceProvider services)
        : base(publication, outgoingChannel, expirationTime, clocks, services)
        => Publication = publication;

    protected override async Task RunInternal(CancellationToken cancellationToken)
    {
        var publicationUseScope = Publication.Use();
        var state = Publication.State;
        var incomingChannelReader = IncomingChannel.Reader;

        try {
            var incomingMessageTask = incomingChannelReader.ReadAsync(cancellationToken).AsTask();
            while (true) {
                // Awaiting for new SubscribeMessage
                var messageOpt = await incomingMessageTask
                    .WithTimeout(Clocks.CoarseCpuClock, ExpirationTime, cancellationToken)
                    .ConfigureAwait(false);
                if (!messageOpt.IsSome(out var incomingMessage))
                    break; // Timeout

                // Maybe sending an update
                if (incomingMessage is UnsubscribeRequest)
                    return;
                if (incomingMessage is not SubscribeRequest sm)
                    continue;

                if (MessageIndex == 0)
                    LastSentVersion = (sm.Version, sm.IsConsistent);

                if (sm.IsUpdateRequested) {
                    // We do only explicit state updates
                    await Publication.Update(cancellationToken).ConfigureAwait(false);
                    state = Publication.State;
                }

                var computed = state.Computed;
                var isUpdateNeeded = sm.IsUpdateRequested
                    || sm.Version != computed.Version
                    || sm.IsConsistent != computed.IsConsistent();
                await TrySendUpdate(state, isUpdateNeeded, cancellationToken).ConfigureAwait(false);

                incomingMessageTask = incomingChannelReader.ReadAsync(cancellationToken).AsTask();
                // If we know for sure the last sent version is inconsistent,
                // we don't need to wait till the moment it gets invalidated -
                // it's client's time to act & request the update.
                if (!LastSentVersion.IsConsistent)
                    continue;

                // Awaiting for invalidation or new message - whatever happens first;
                // CreateLinkedTokenSource is needed to make sure we truly cancel
                // WhenInvalidated(...) & remove the OnInvalidated handler. 
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try {
                    var whenInvalidatedTask = computed.WhenInvalidated(cts.Token);
                    var completedTask = await Task
                        .WhenAny(whenInvalidatedTask, incomingMessageTask)
                        .ConfigureAwait(false);
                    // WhenAny doesn't throw, and we need to make sure
                    // we exit right here in this task is cancelled. 
                    cancellationToken.ThrowIfCancellationRequested();
                    if (completedTask == incomingMessageTask)
                        continue;
                }
                finally {
                    cts.CancelAndDisposeSilently();
                }

                // And finally, sending the invalidation message
                await TrySendUpdate(state, false, cancellationToken).ConfigureAwait(false);
            }
        }
        finally {
            publicationUseScope.Dispose();
            IncomingChannel.Writer.TryComplete();
            // Awaiting for disposal here = cyclic task dependency;
            // we should just ensure it starts right when this method
            // completes.
            _ = DisposeAsync();
        }
    }

    protected virtual async ValueTask TrySendUpdate(
        PublicationState<T>? state, bool mustUpdate, CancellationToken cancellationToken)
    {
        if (state == null || state.IsDisposed) {
            var absentsMessage = new PublicationAbsentsReply();
            await Send(absentsMessage, cancellationToken).ConfigureAwait(false);
            LastSentVersion = default;
            return;
        }

        var computed = state.Computed;
        var isConsistent = computed.IsConsistent(); // It may change, so we want to make a snapshot here
        var version = (computed.Version, isConsistent);
        if (!mustUpdate && LastSentVersion == version)
            return;

        var reply = isConsistent || LastSentVersion.Version != computed.Version
            ? PublicationStateReply<T>.New(computed.Output)
            : new PublicationStateReply<T>();
        reply.Version = computed.Version;
        reply.IsConsistent = isConsistent;

        await Send(reply, cancellationToken).ConfigureAwait(false);
        LastSentVersion = version;
    }

    protected virtual async ValueTask Send(PublicationReply reply, CancellationToken cancellationToken)
    {
        reply.MessageIndex = ++MessageIndex;
        reply.PublisherId = Publisher.Id;
        reply.PublicationId = Publication.Id;

        await OutgoingChannel.Writer.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
    }
}
