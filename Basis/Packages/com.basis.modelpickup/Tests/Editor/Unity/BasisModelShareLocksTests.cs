using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.Networking;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelShareLocksTests
    {
        private static readonly MethodInfo SetPropsLocked = typeof(BasisNetworkModeration)
            .GetProperty(nameof(BasisNetworkModeration.GlobalPropsLocked))
            ?.GetSetMethod(true);

        private HashSet<string> _savedPermissions;
        private bool _savedConnected;
        private bool _savedPropsLocked;

        [SetUp]
        public void SetUp()
        {
            Assert.That(SetPropsLocked, Is.Not.Null, "GlobalPropsLocked lost its setter");
            _savedPermissions = BasisNetworkManagement.LocalPermissions;
            _savedConnected = BasisNetworkConnection.LocalPlayerIsConnected;
            _savedPropsLocked = BasisNetworkModeration.GlobalPropsLocked;
        }

        [TearDown]
        public void TearDown()
        {
            BasisNetworkManagement.LocalPermissions = _savedPermissions;
            BasisNetworkConnection.LocalPlayerIsConnected = _savedConnected;
            SetPropsLocked.Invoke(null, new object[] { _savedPropsLocked });
        }

        private static void Grant(params string[] nodes)
        {
            BasisNetworkManagement.LocalPermissions = new HashSet<string>(nodes, StringComparer.OrdinalIgnoreCase);
        }

        [Test]
        public void PropsBypassIsTheServersPropNodeNotTheGlobalLockNode()
        {
            Grant(BasisPermissions.PermNodes.ModerationGlobalLock);
            Assert.That(BasisModelShareLocks.LocalPlayerCanBypass(), Is.False);

            Grant(BasisPermissions.PermNodes.ResourceLockBypassProp);
            Assert.That(BasisModelShareLocks.LocalPlayerCanBypass(), Is.True);

            Grant(BasisPermissions.PermNodes.All);
            Assert.That(BasisModelShareLocks.LocalPlayerCanBypass(), Is.True);
        }

        [Test]
        public void PropsBypassHonoursParentWildcards()
        {
            Grant("basis.resource.*");
            Assert.That(BasisModelShareLocks.LocalPlayerCanBypass(), Is.True);

            Grant("basis.resource.lockbypass.*");
            Assert.That(BasisModelShareLocks.LocalPlayerCanBypass(), Is.True);

            Grant("basis.resource.load.*");
            Assert.That(BasisModelShareLocks.LocalPlayerCanBypass(), Is.False);
        }

        [Test]
        public void MayShareRequiresPropLoadOnlyWhenPermissionsAreKnown()
        {
            BasisNetworkConnection.LocalPlayerIsConnected = false;
            Grant();
            Assert.That(BasisModelShareLocks.LocalPlayerMayShare(), Is.True, "offline");

            BasisNetworkConnection.LocalPlayerIsConnected = true;
            BasisNetworkManagement.LocalPermissions = null;
            Assert.That(BasisModelShareLocks.LocalPlayerMayShare(), Is.True, "not told yet");

            Grant();
            Assert.That(BasisModelShareLocks.LocalPlayerMayShare(), Is.False);

            Grant(BasisPermissions.PermNodes.ResourceLoadProp);
            Assert.That(BasisModelShareLocks.LocalPlayerMayShare(), Is.True);
        }

        [Test]
        public void ThePropsLockBlocksUnlessBypassed()
        {
            Grant();
            SetPropsLocked.Invoke(null, new object[] { true });
            Assert.That(BasisModelShareLocks.IsBlockedLocally(), Is.True);

            Grant(BasisPermissions.PermNodes.ResourceLockBypassProp);
            Assert.That(BasisModelShareLocks.IsLocked(), Is.True);
            Assert.That(BasisModelShareLocks.IsBlockedLocally(), Is.False);

            SetPropsLocked.Invoke(null, new object[] { false });
            Grant();
            Assert.That(BasisModelShareLocks.IsBlockedLocally(), Is.False);
        }
    }
}
