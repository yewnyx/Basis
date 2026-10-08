using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// The resolve-register-release cycle. A known id resolves synchronously (the resolver answers from its map
    /// without awaiting), so these run without a server.
    /// </summary>
    public sealed class BasisModelNetworkIdentityTests
    {
        private const string Identifier = "Basis.ModelPickup.Tests.Identity";
        private const ushort IssuedId = 61001;
        private const ushort ReissuedId = 61002;

        private static readonly FieldInfo DirectHandlersField = typeof(BasisNetworkGenericMessages).GetField(
            "_directHandlers",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        private bool _wasConnected;
        private int _armed;
        private int _released;

        [SetUp]
        public void SetUp()
        {
            _wasConnected = BasisNetworkConnection.LocalPlayerIsConnected;
            _armed = 0;
            _released = 0;
        }

        [TearDown]
        public void TearDown()
        {
            BasisNetworkConnection.LocalPlayerIsConnected = _wasConnected;
            BasisNetworkIdResolver.KnownIdMap.TryRemove(Identifier, out _);
            BasisNetworkGenericMessages.UnregisterDirectHandler(IssuedId);
            BasisNetworkGenericMessages.UnregisterDirectHandler(ReissuedId);
        }

        private BasisModelNetworkIdentity Make()
        {
            return new BasisModelNetworkIdentity(Identifier, "Test identity", BasisDebug.LogTag.Pickups, (sender, buffer, method) => { })
            {
                Enabled = true,
                Armed = () => _armed++,
                Released = () => _released++,
            };
        }

        private static bool HandlerRegistered(ushort id)
        {
            Assert.That(DirectHandlersField, Is.Not.Null, "BasisNetworkGenericMessages._directHandlers moved");
            var handlers = (Dictionary<ushort, Action<ushort, byte[], DeliveryMethod>>)DirectHandlersField.GetValue(null);
            return handlers.ContainsKey(id);
        }

        private static void Connect(ushort issued)
        {
            BasisNetworkConnection.LocalPlayerIsConnected = true;
            BasisNetworkIdResolver.KnownIdMap[Identifier] = issued;
        }

        [Test]
        public void ResolveArmsOnceWithTheIssuedId()
        {
            Connect(IssuedId);
            BasisModelNetworkIdentity identity = Make();

            identity.Resolve();
            identity.Resolve();

            Assert.That(identity.HasNetworkID, Is.True);
            Assert.That(identity.NetworkID, Is.EqualTo(IssuedId));
            Assert.That(HandlerRegistered(IssuedId), Is.True);
            Assert.That(_armed, Is.EqualTo(1));
        }

        [Test]
        public void ReleaseOpensANewGenerationAndDisarms()
        {
            Connect(IssuedId);
            BasisModelNetworkIdentity identity = Make();
            identity.Resolve();
            int generation = identity.ConnectionGeneration;

            identity.Release();

            Assert.That(identity.ConnectionGeneration, Is.EqualTo(generation + 1));
            Assert.That(identity.HasNetworkID, Is.False);
            Assert.That(identity.NetworkID, Is.EqualTo(0));
            Assert.That(HandlerRegistered(IssuedId), Is.False);
            Assert.That(_released, Is.EqualTo(1));

            identity.Resolve();
            Assert.That(identity.HasNetworkID, Is.True, "a released identity can arm again");
        }

        [Test]
        public void AnAnswerAfterShutdownDoesNotArm()
        {
            Connect(IssuedId);
            BasisModelNetworkIdentity identity = Make();
            identity.Enabled = false;

            identity.Resolve();

            Assert.That(identity.HasNetworkID, Is.False);
            Assert.That(HandlerRegistered(IssuedId), Is.False);
            Assert.That(_armed, Is.EqualTo(0));
        }

        [Test]
        public void EnsureDoesNothingWhileDisconnected()
        {
            BasisNetworkConnection.LocalPlayerIsConnected = false;
            BasisModelNetworkIdentity identity = Make();
            int generation = identity.ConnectionGeneration;

            identity.Ensure();

            Assert.That(identity.HasNetworkID, Is.False);
            Assert.That(identity.ConnectionGeneration, Is.EqualTo(generation));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void EnsureReResolvesAnIdTheServerNoLongerConfirms()
        {
            Connect(IssuedId);
            BasisModelNetworkIdentity identity = Make();
            identity.Resolve();

            identity.Ensure();
            Assert.That(identity.NetworkID, Is.EqualTo(IssuedId), "a confirmed id is kept");
            Assert.That(_released, Is.EqualTo(0));

            BasisNetworkIdResolver.KnownIdMap[Identifier] = ReissuedId;
            identity.Ensure();

            Assert.That(identity.NetworkID, Is.EqualTo(ReissuedId));
            Assert.That(HandlerRegistered(IssuedId), Is.False);
            Assert.That(HandlerRegistered(ReissuedId), Is.True);
            Assert.That(_released, Is.EqualTo(1));
            Assert.That(_armed, Is.EqualTo(2));
        }

        [Test]
        public void ResolveWhileDisconnectedLogsAndStaysUnarmed()
        {
            BasisNetworkConnection.LocalPlayerIsConnected = false;
            BasisModelNetworkIdentity identity = Make();
            LogAssert.Expect(LogType.Error, new Regex("Test identity cannot start; the local player is not connected"));

            identity.Resolve();

            Assert.That(identity.HasNetworkID, Is.False);
        }

        [Test]
        public void AResolveFaultIsLoggedAndTheNextResolveRetries()
        {
            BasisNetworkConnection.LocalPlayerIsConnected = true;
            ConcurrentDictionary<string, ushort> knownIds = BasisNetworkIdResolver.KnownIdMap;
            var pendingResolutions = BasisNetworkIdResolver.PendingResolutions;
            try
            {
                BasisNetworkIdResolver.KnownIdMap = new ConcurrentDictionary<string, ushort>();
                // A missing pending map makes ResolveAsync fault instead of returning a failed result, standing in
                // for a teardown that clears the map between its TryAdd and its lookup.
                BasisNetworkIdResolver.PendingResolutions = null;
                BasisModelNetworkIdentity identity = Make();

                // The colon matches only the faulted path; a plain failed result says no more than the identifier.
                var resolveFaulted = new Regex("Test identity could not resolve the network identifier '.+': ");
                LogAssert.Expect(LogType.Error, resolveFaulted);
                identity.Resolve();

                // The next join has to try again rather than find the faulted attempt still marked in flight.
                LogAssert.Expect(LogType.Error, resolveFaulted);
                identity.Resolve();
                Assert.That(identity.HasNetworkID, Is.False);
            }
            finally
            {
                BasisNetworkIdResolver.PendingResolutions = pendingResolutions;
                BasisNetworkIdResolver.KnownIdMap = knownIds;
            }
        }

        [Test]
        public void SendWithoutAnIdLogsAndDrops()
        {
            BasisModelNetworkIdentity identity = Make();
            LogAssert.Expect(LogType.Error, new Regex("Test identity has no network id assigned yet"));

            identity.Send(new byte[] { 1 }, DeliveryMethod.ReliableOrdered, null);
        }
    }
}
