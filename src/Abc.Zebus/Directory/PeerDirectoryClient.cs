﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Abc.Zebus.Routing;
using Abc.Zebus.Util;
using Abc.Zebus.Util.Extensions;
using log4net;

namespace Abc.Zebus.Directory
{
    public partial class PeerDirectoryClient : IPeerDirectory,
                                               IMessageHandler<PeerStarted>,
                                               IMessageHandler<PeerStopped>,
                                               IMessageHandler<PeerDecommissioned>,
                                               IMessageHandler<PingPeerCommand>,
                                               IMessageHandler<PeerSubscriptionsUpdated>,
                                               IMessageHandler<PeerSubscriptionsForTypesUpdated>,
                                               IMessageHandler<PeerNotResponding>,
                                               IMessageHandler<PeerResponding>
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(PeerDirectoryClient));

        private readonly ConcurrentDictionary<MessageTypeId, PeerSubscriptionTree> _globalSubscriptionsIndex = new ConcurrentDictionary<MessageTypeId, PeerSubscriptionTree>();
        private readonly ConcurrentDictionary<PeerId, PeerEntry> _peers = new ConcurrentDictionary<PeerId, PeerEntry>();
        private readonly UniqueTimestampProvider _timestampProvider = new UniqueTimestampProvider(10);
        private readonly IBusConfiguration _configuration;
        private readonly Stopwatch _pingStopwatch = new Stopwatch();
        private BlockingCollection<IEvent> _messagesReceivedDuringRegister;
        private IEnumerable<Peer> _directoryPeers = Enumerable.Empty<Peer>();
        private Peer _self = default!;
        private volatile HashSet<Type> _observedSubscriptionMessageTypes = new HashSet<Type>();

        public PeerDirectoryClient(IBusConfiguration configuration)
        {
            _configuration = configuration;

            _messagesReceivedDuringRegister = new BlockingCollection<IEvent>();
            _messagesReceivedDuringRegister.CompleteAdding();
        }

        public event Action<PeerId, PeerUpdateAction>? PeerUpdated;
        public event Action<PeerId, IReadOnlyList<Subscription>>? PeerSubscriptionsUpdated;

        public TimeSpan TimeSinceLastPing => _pingStopwatch.IsRunning ? _pingStopwatch.Elapsed : TimeSpan.MaxValue;

        public async Task RegisterAsync(IBus bus, Peer self, IEnumerable<Subscription> subscriptions)
        {
            _self = self;

            _globalSubscriptionsIndex.Clear();
            _peers.Clear();

            var selfDescriptor = CreateSelfDescriptor(subscriptions);
            AddOrUpdatePeerEntry(selfDescriptor, shouldRaisePeerUpdated: false);

            _messagesReceivedDuringRegister = new BlockingCollection<IEvent>();

            try
            {
                await TryRegisterOnDirectoryAsync(bus, selfDescriptor).ConfigureAwait(false);
            }
            finally
            {
                _messagesReceivedDuringRegister.CompleteAdding();
            }

            _pingStopwatch.Restart();
            ProcessMessagesReceivedDuringRegister();
        }

        private void ProcessMessagesReceivedDuringRegister()
        {
            foreach (var message in _messagesReceivedDuringRegister.GetConsumingEnumerable())
            {
                try
                {
                    switch (message)
                    {
                        case PeerStarted msg:
                            Handle(msg);
                            break;

                        case PeerStopped msg:
                            Handle(msg);
                            break;

                        case PeerDecommissioned msg:
                            Handle(msg);
                            break;

                        case PeerSubscriptionsUpdated msg:
                            Handle(msg);
                            break;

                        case PeerSubscriptionsForTypesUpdated msg:
                            Handle(msg);
                            break;

                        case PeerNotResponding msg:
                            Handle(msg);
                            break;

                        case PeerResponding msg:
                            Handle(msg);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.WarnFormat("Unable to process message {0} {{{1}}}, Exception: {2}", message.GetType(), message, ex);
                }
            }
        }

        private PeerDescriptor CreateSelfDescriptor(IEnumerable<Subscription> subscriptions)
        {
            return new PeerDescriptor(_self.Id, _self.EndPoint, _configuration.IsPersistent, true, true, _timestampProvider.NextUtcTimestamp(), subscriptions.ToArray())
            {
                HasDebuggerAttached = Debugger.IsAttached
            };
        }

        private async Task TryRegisterOnDirectoryAsync(IBus bus, PeerDescriptor selfDescriptor)
        {
            var directoryPeers = GetDirectoryPeers().ToList();

            foreach (var directoryPeer in directoryPeers)
            {
                try
                {
                    if (await TryRegisterOnDirectoryAsync(bus, selfDescriptor, directoryPeer).ConfigureAwait(false))
                        return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Error(ex);
                }
            }

            var directoryPeersText = string.Join(", ", directoryPeers.Select(peer => "{" + peer + "}"));
            var message = $"Unable to register peer on directory (tried: {directoryPeersText}) after {_configuration.RegistrationTimeout}";
            _logger.Error(message);
            throw new TimeoutException(message);
        }

        private async Task<bool> TryRegisterOnDirectoryAsync(IBus bus, PeerDescriptor self, Peer directoryPeer)
        {
            try
            {
                var registration = await bus.Send(new RegisterPeerCommand(self), directoryPeer).WithTimeoutAsync(_configuration.RegistrationTimeout).ConfigureAwait(false);

                var response = (RegisterPeerResponse?)registration.Response;
                if (response?.PeerDescriptors == null)
                    return false;

                if (registration.ErrorCode == DirectoryErrorCodes.PeerAlreadyExists)
                {
                    _logger.InfoFormat("Register rejected for {0}, the peer already exists in the directory", new RegisterPeerCommand(self).Peer.PeerId);
                    return false;
                }

                response.PeerDescriptors?.ForEach(peer => AddOrUpdatePeerEntry(peer, shouldRaisePeerUpdated: false));

                return true;
            }
            catch (TimeoutException ex)
            {
                _logger.Error(ex);
                return false;
            }
        }

        public async Task UpdateSubscriptionsAsync(IBus bus, IEnumerable<SubscriptionsForType> subscriptionsForTypes)
        {
            var subscriptions = subscriptionsForTypes as SubscriptionsForType[] ?? subscriptionsForTypes.ToArray();
            if (subscriptions.Length == 0)
                return;

            var command = new UpdatePeerSubscriptionsForTypesCommand(_self.Id, _timestampProvider.NextUtcTimestamp(), subscriptions);

            foreach (var directoryPeer in GetDirectoryPeers())
            {
                try
                {
                    await bus.Send(command, directoryPeer).WithTimeoutAsync(_configuration.RegistrationTimeout).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Error(ex);
                }
            }

            throw new TimeoutException("Unable to update peer subscriptions on directory");
        }

        public async Task UnregisterAsync(IBus bus)
        {
            var command = new UnregisterPeerCommand(_self, _timestampProvider.NextUtcTimestamp());

            // Using a cache of the directory peers in case of the underlying configuration proxy values changed before stopping
            foreach (var directoryPeer in _directoryPeers)
            {
                try
                {
                    await bus.Send(command, directoryPeer).WithTimeoutAsync(_configuration.RegistrationTimeout).ConfigureAwait(false);
                    _pingStopwatch.Stop();
                    return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Error(ex);
                }
            }

            throw new TimeoutException("Unable to unregister peer on directory");
        }

        public IList<Peer> GetPeersHandlingMessage(IMessage message)
            => GetPeersHandlingMessage(MessageBinding.FromMessage(message));

        public IList<Peer> GetPeersHandlingMessage(MessageBinding messageBinding)
        {
            var subscriptionList = _globalSubscriptionsIndex.GetValueOrDefault(messageBinding.MessageTypeId);
            if (subscriptionList == null)
                return Array.Empty<Peer>();

            return subscriptionList.GetPeers(messageBinding.RoutingKey);
        }

        public bool IsPersistent(PeerId peerId)
        {
            var entry = _peers.GetValueOrDefault(peerId);
            return entry != null && entry.IsPersistent;
        }

        public Peer? GetPeer(PeerId peerId)
        {
            return _peers.TryGetValue(peerId, out var entry) ? entry.Peer : null;
        }

        public void EnableSubscriptionsUpdatedFor(IEnumerable<Type> types)
        {
            _observedSubscriptionMessageTypes = types.ToHashSet();
        }

        public PeerDescriptor? GetPeerDescriptor(PeerId peerId)
            => _peers.GetValueOrDefault(peerId)?.ToPeerDescriptor();

        public IEnumerable<PeerDescriptor> GetPeerDescriptors()
            => _peers.Values.Select(x => x.ToPeerDescriptor()).ToList();

        // Only internal for testing purposes
        internal IEnumerable<Peer> GetDirectoryPeers()
        {
            _directoryPeers = _configuration.DirectoryServiceEndPoints.Select(CreateDirectoryPeer);
            return _configuration.IsDirectoryPickedRandomly ? _directoryPeers.Shuffle() : _directoryPeers;
        }

        private static Peer CreateDirectoryPeer(string endPoint, int index)
        {
            var peerId = new PeerId("Abc.Zebus.DirectoryService." + index);
            return new Peer(peerId, endPoint);
        }

        private void AddOrUpdatePeerEntry(PeerDescriptor peerDescriptor, bool shouldRaisePeerUpdated)
        {
            var subscriptions = peerDescriptor.Subscriptions ?? Array.Empty<Subscription>();

            var peerEntry = _peers.AddOrUpdate(peerDescriptor.PeerId, key => CreatePeerEntry(), (key, entry) => UpdatePeerEntry(entry));
            peerEntry.SetSubscriptions(subscriptions, peerDescriptor.TimestampUtc);

            if (shouldRaisePeerUpdated)
                PeerUpdated?.Invoke(peerDescriptor.Peer.Id, PeerUpdateAction.Started);

            var observedSubscriptions = GetObservedSubscriptions(subscriptions);
            if (observedSubscriptions.Count > 0)
                PeerSubscriptionsUpdated?.Invoke(peerDescriptor.PeerId, observedSubscriptions);

            PeerEntry CreatePeerEntry() => new PeerEntry(peerDescriptor, _globalSubscriptionsIndex);

            PeerEntry UpdatePeerEntry(PeerEntry entry)
            {
                entry.Peer.EndPoint = peerDescriptor.Peer.EndPoint;
                entry.Peer.IsUp = peerDescriptor.Peer.IsUp;
                entry.Peer.IsResponding = peerDescriptor.Peer.IsResponding;
                entry.IsPersistent = peerDescriptor.IsPersistent;
                entry.TimestampUtc = peerDescriptor.TimestampUtc ?? DateTime.UtcNow;
                entry.HasDebuggerAttached = peerDescriptor.HasDebuggerAttached;

                return entry;
            }
        }

        public void Handle(PeerStarted message)
        {
            if (EnqueueIfRegistering(message))
                return;

            AddOrUpdatePeerEntry(message.PeerDescriptor, true);
        }

        private bool EnqueueIfRegistering(IEvent message)
        {
            if (_messagesReceivedDuringRegister.IsAddingCompleted)
                return false;

            try
            {
                _messagesReceivedDuringRegister.Add(message);
                return true;
            }
            catch (InvalidOperationException)
            {
                // if adding is complete; should only happen in a race
                return false;
            }
        }

        public void Handle(PingPeerCommand message)
        {
            if (_pingStopwatch.IsRunning)
                _pingStopwatch.Restart();
        }

        public void Handle(PeerStopped message)
        {
            if (EnqueueIfRegistering(message))
                return;

            var peer = GetPeerCheckTimestamp(message.PeerId, message.TimestampUtc);
            if (peer.Value == null)
                return;

            peer.Value.Peer.IsUp = false;
            peer.Value.Peer.IsResponding = false;
            peer.Value.TimestampUtc = message.TimestampUtc ?? DateTime.UtcNow;

            PeerUpdated?.Invoke(message.PeerId, PeerUpdateAction.Stopped);
        }

        public void Handle(PeerDecommissioned message)
        {
            if (EnqueueIfRegistering(message))
                return;

            if (!_peers.TryRemove(message.PeerId, out var removedPeer))
                return;

            removedPeer.RemoveSubscriptions();

            PeerUpdated?.Invoke(message.PeerId, PeerUpdateAction.Decommissioned);
        }

        public void Handle(PeerSubscriptionsUpdated message)
        {
            if (EnqueueIfRegistering(message))
                return;

            var peer = GetPeerCheckTimestamp(message.PeerDescriptor.Peer.Id, message.PeerDescriptor.TimestampUtc);
            if (peer.Value == null)
            {
                WarnWhenPeerDoesNotExist(peer, message.PeerDescriptor.PeerId);
                return;
            }

            var subscriptions = message.PeerDescriptor.Subscriptions ?? Array.Empty<Subscription>();

            peer.Value.SetSubscriptions(subscriptions, message.PeerDescriptor.TimestampUtc);
            peer.Value.TimestampUtc = message.PeerDescriptor.TimestampUtc ?? DateTime.UtcNow;

            PeerUpdated?.Invoke(message.PeerDescriptor.PeerId, PeerUpdateAction.Updated);

            var observedSubscriptions = GetObservedSubscriptions(subscriptions);
            if (observedSubscriptions.Count > 0)
                PeerSubscriptionsUpdated?.Invoke(message.PeerDescriptor.PeerId, observedSubscriptions);
        }

        private IReadOnlyList<Subscription> GetObservedSubscriptions(Subscription[] subscriptions)
        {
            if (subscriptions.Length == 0)
                return Array.Empty<Subscription>();

            var observedSubscriptionMessageTypes = _observedSubscriptionMessageTypes;
            if (observedSubscriptionMessageTypes.Count == 0)
                return Array.Empty<Subscription>();

            return subscriptions.Where(x =>
                                {
                                    var messageType = x.MessageTypeId.GetMessageType();
                                    return messageType != null && observedSubscriptionMessageTypes.Contains(messageType);
                                })
                                .ToList();
        }

        public void Handle(PeerSubscriptionsForTypesUpdated message)
        {
            if (EnqueueIfRegistering(message))
                return;

            var peer = GetPeerCheckTimestamp(message.PeerId, message.TimestampUtc);
            if (peer.Value == null)
            {
                WarnWhenPeerDoesNotExist(peer, message.PeerId);
                return;
            }

            var subscriptionsForTypes = message.SubscriptionsForType ?? Array.Empty<SubscriptionsForType>();
            peer.Value.SetSubscriptionsForType(subscriptionsForTypes, message.TimestampUtc);

            PeerUpdated?.Invoke(message.PeerId, PeerUpdateAction.Updated);

            var observedSubscriptions = GetObservedSubscriptions(subscriptionsForTypes);
            if (observedSubscriptions.Count > 0)
                PeerSubscriptionsUpdated?.Invoke(message.PeerId, observedSubscriptions);
        }

        private IReadOnlyList<Subscription> GetObservedSubscriptions(SubscriptionsForType[] subscriptionsForTypes)
        {
            if (subscriptionsForTypes.Length == 0)
                return Array.Empty<Subscription>();

            var observedSubscriptionMessageTypes = _observedSubscriptionMessageTypes;
            if (observedSubscriptionMessageTypes.Count == 0)
                return Array.Empty<Subscription>();

            return subscriptionsForTypes.Where(x =>
                                        {
                                            var messageType = x.MessageTypeId.GetMessageType();
                                            return messageType != null && observedSubscriptionMessageTypes.Contains(messageType);
                                        })
                                        .SelectMany(x => x.ToSubscriptions())
                                        .ToList();
        }

        private static void WarnWhenPeerDoesNotExist(PeerEntryResult peer, PeerId peerId)
        {
            if (peer.FailureReason == PeerEntryResult.FailureReasonType.PeerNotPresent)
                _logger.WarnFormat("Received message but no peer existed: {0}", peerId);
        }

        public void Handle(PeerNotResponding message)
        {
            HandlePeerRespondingChange(message.PeerId, false);
        }

        public void Handle(PeerResponding message)
        {
            HandlePeerRespondingChange(message.PeerId, true);
        }

        private void HandlePeerRespondingChange(PeerId peerId, bool isResponding)
        {
            var peer = _peers.GetValueOrDefault(peerId);
            if (peer == null)
                return;

            peer.Peer.IsResponding = isResponding;

            PeerUpdated?.Invoke(peerId, PeerUpdateAction.Updated);
        }

        private PeerEntryResult GetPeerCheckTimestamp(PeerId peerId, DateTime? timestampUtc)
        {
            var peer = _peers.GetValueOrDefault(peerId);
            if (peer == null)
                return new PeerEntryResult(PeerEntryResult.FailureReasonType.PeerNotPresent);

            if (peer.TimestampUtc > timestampUtc)
            {
                _logger.InfoFormat("Outdated message ignored");
                return new PeerEntryResult(PeerEntryResult.FailureReasonType.OutdatedMessage);
            }

            return new PeerEntryResult(peer);
        }

        private readonly struct PeerEntryResult
        {
            internal enum FailureReasonType
            {
                PeerNotPresent,
                OutdatedMessage,
            }

            public PeerEntryResult(PeerEntry value)
                : this()
            {
                Value = value;
            }

            public PeerEntryResult(FailureReasonType failureReason)
                : this()
            {
                FailureReason = failureReason;
            }

            public PeerEntry Value { get; }
            public FailureReasonType? FailureReason { get; }
        }
    }
}
