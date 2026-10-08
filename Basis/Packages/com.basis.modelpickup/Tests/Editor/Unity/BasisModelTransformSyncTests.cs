using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    /// <summary>The follower and controller halves of a pickup.</summary>
    public sealed class BasisModelTransformSyncTests
    {
        // Exactly representable, so "one interval later" is never a hair short of the interval.
        private const float Interval = 0.5f;

        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("TransformSyncTestHost");
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
                Object.DestroyImmediate(_host);
        }

        [Test]
        public void TheFirstRemoteTargetSnaps()
        {
            var sync = new BasisModelTransformSync();
            Transform root = _host.transform;

            sync.SetRemoteTarget(root, new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 90f, 0f), 2f);
            Assert.That(sync.HasRemoteTarget, Is.True);
            Assert.That(root.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(root.localScale, Is.EqualTo(new Vector3(2f, 2f, 2f)));

            sync.SetRemoteTarget(root, new Vector3(9f, 9f, 9f), Quaternion.identity, 3f);
            Assert.That(root.position, Is.EqualTo(new Vector3(1f, 2f, 3f)), "later targets are eased toward, not snapped");
            Assert.That(sync.TargetPosition, Is.EqualTo(new Vector3(9f, 9f, 9f)));
            Assert.That(sync.TargetScale, Is.EqualTo(3f));
        }

        [Test]
        public void SimulateMovesTowardTheTargetByTheLerpFactor()
        {
            var sync = new BasisModelTransformSync();
            Transform root = _host.transform;
            sync.SetRemoteTarget(root, Vector3.zero, Quaternion.identity, 1f);
            sync.SetRemoteTarget(root, new Vector3(10f, 0f, 0f), Quaternion.identity, 3f);

            const float deltaTime = 0.05f;
            float t = BasisModelMath.RemoteTransformLerpFactor(deltaTime);
            sync.Simulate(root, deltaTime);

            Assert.That(root.position.x, Is.EqualTo(10f * t).Within(1e-5f));
            Assert.That(root.localScale.x, Is.EqualTo(1f + 2f * t).Within(1e-5f));
            Assert.That(root.localScale.y, Is.EqualTo(root.localScale.x));

            sync.Promote();
            Vector3 before = root.position;
            sync.Simulate(root, deltaTime);
            Assert.That(root.position, Is.EqualTo(before), "the controller is never moved by remote targets");
        }

        [Test]
        public void AFollowerThatArrivesSnapsOntoItsTargetAndIsLeftAlone()
        {
            var sync = new BasisModelTransformSync();
            Transform root = _host.transform;
            sync.SetRemoteTarget(root, Vector3.zero, Quaternion.identity, 1f);
            var target = new Vector3(2f, 0f, 0f);
            sync.SetRemoteTarget(root, target, Quaternion.identity, 1f);
            root.position = target + new Vector3(0.0005f, 0f, 0f);

            sync.Simulate(root, 0.05f);
            Assert.That(sync.Settled, Is.True, "within a millimetre it counts as arrived");
            Assert.That(root.position, Is.EqualTo(target), "and lands exactly on the target");
            Assert.That(sync.NeedsFollow, Is.False);

            root.position = new Vector3(5f, 0f, 0f);
            sync.Simulate(root, 0.05f);
            Assert.That(root.position, Is.EqualTo(new Vector3(5f, 0f, 0f)), "a settled follower is not written");

            sync.SetRemoteTarget(root, new Vector3(9f, 0f, 0f), Quaternion.identity, 1f);
            Assert.That(sync.Settled, Is.False, "a new target wakes it");
            Assert.That(sync.NeedsFollow, Is.True);
        }

        [Test]
        public void TheFollowPassEasesFollowersAndSamplesControllersInOneJob()
        {
            var controllerHost = new GameObject("TransformSyncTestController");
            var pass = new BasisModelFollowPass();
            try
            {
                Transform follower = _host.transform;
                Transform controller = controllerHost.transform;
                follower.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                controller.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 30f, 0f));
                controller.localScale = Vector3.one * 2f;
                pass.Add(follower);
                pass.Add(controller);

                var slots = pass.Slots;
                slots[0] = new BasisModelFollowSlot
                {
                    Mode = BasisModelFollowMode.Follow,
                    TargetPosition = new Vector3(10f, 0f, 0f),
                    TargetRotation = Quaternion.identity,
                    TargetScale = 3f,
                };
                slots[1] = new BasisModelFollowSlot { Mode = BasisModelFollowMode.Sample };

                float t = BasisModelMath.RemoteTransformLerpFactor(0.05f);
                pass.Run(t);

                Assert.That(follower.position.x, Is.EqualTo(10f * t).Within(1e-5f));
                Assert.That(follower.localScale.x, Is.EqualTo(1f + 2f * t).Within(1e-5f));
                Assert.That(follower.localScale.z, Is.EqualTo(follower.localScale.x));
                Assert.That(slots[0].Settled, Is.EqualTo((byte)0));

                BasisModelFollowSlot sampled = slots[1];
                Assert.That(sampled.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(Quaternion.Angle(sampled.Rotation, Quaternion.Euler(0f, 30f, 0f)), Is.LessThan(1e-3f));
                Assert.That(sampled.Scale, Is.EqualTo(2f));
                Assert.That(controller.position, Is.EqualTo(new Vector3(1f, 2f, 3f)), "sampling never writes");

                // Swap-back keeps root i and slot i together: the controller now sits at 0.
                pass.RemoveAtSwapBack(0);
                Assert.That(pass.Roots.length, Is.EqualTo(1));
                Assert.That(pass.Roots[0], Is.SameAs(controller));
            }
            finally
            {
                pass.Dispose();
                Object.DestroyImmediate(controllerHost);
            }
        }

        [Test]
        public void TryTakeSendHonoursTheIntervalAndEachEpsilon()
        {
            var sync = new BasisModelTransformSync();
            Transform root = _host.transform;
            float now = 10f;

            Assert.That(sync.TryTakeSend(root, now, Interval, out _, out _, out _), Is.False, "a follower never sends");

            sync.Promote();
            root.position = new Vector3(1f, 0f, 0f);
            Assert.That(sync.TryTakeSend(root, now, Interval, out Vector3 p, out Quaternion r, out float s), Is.True);
            Assert.That(p, Is.EqualTo(new Vector3(1f, 0f, 0f)));
            Assert.That(sync.LastSentPosition, Is.EqualTo(p));
            Assert.That(sync.LastSendTime, Is.EqualTo(now));

            root.position = new Vector3(2f, 0f, 0f);
            Assert.That(sync.TryTakeSend(root, now + Interval * 0.5f, Interval, out _, out _, out _), Is.False, "inside the interval");

            now += Interval;
            root.position = new Vector3(1f + BasisModelShareSettings.MovedPositionEpsilon * 0.5f, 0f, 0f);
            Assert.That(sync.TryTakeSend(root, now, Interval, out _, out _, out _), Is.False, "below the position epsilon");

            root.position = new Vector3(1f, 0f, 0f);
            root.rotation = Quaternion.Euler(0f, BasisModelShareSettings.MovedRotationEpsilonDegrees * 2f, 0f);
            Assert.That(sync.TryTakeSend(root, now, Interval, out _, out _, out _), Is.True, "past the rotation epsilon");

            now += Interval;
            root.localScale = Vector3.one * (1f + BasisModelShareSettings.MovedScaleEpsilon * 2f);
            Assert.That(sync.TryTakeSend(root, now, Interval, out _, out _, out s), Is.True, "past the scale epsilon");
            Assert.That(s, Is.EqualTo(root.localScale.x));
        }

        [Test]
        public void DemoteZeroesVelocityAndMakesTheBodyKinematic()
        {
            var sync = new BasisModelTransformSync();
            Transform root = _host.transform;
            var body = _host.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = false;
            body.linearVelocity = new Vector3(1f, 2f, 3f);
            body.angularVelocity = new Vector3(0.5f, 0f, 0f);
            root.SetPositionAndRotation(new Vector3(4f, 5f, 6f), Quaternion.Euler(0f, 30f, 0f));
            root.localScale = Vector3.one * 1.5f;
            sync.Promote();

            sync.Demote(root, body, null);

            Assert.That(sync.IsController, Is.False);
            Assert.That(body.isKinematic, Is.True);
            Assert.That(body.linearVelocity, Is.EqualTo(Vector3.zero));
            Assert.That(body.angularVelocity, Is.EqualTo(Vector3.zero));
            Assert.That(sync.TargetPosition, Is.EqualTo(new Vector3(4f, 5f, 6f)), "it holds still until the new controller speaks");
            Assert.That(sync.TargetScale, Is.EqualTo(1.5f));
        }

        [Test]
        public void DemoteWithoutAHolderDoesNotTouchTheInteractable()
        {
            var sync = new BasisModelTransformSync();
            var body = _host.AddComponent<Rigidbody>();
            var interactable = _host.AddComponent<BasisPickupInteractable>();
            interactable.RigidRef = body;
            interactable._previousKinematicValue = false;

            sync.Demote(_host.transform, body, interactable);

            Assert.That(interactable._previousKinematicValue, Is.False, "nobody was holding it, so there is no release to steer");
            Assert.That(body.isKinematic, Is.True);
        }

        [Test]
        public void DemotionWhileHeldDropsTheHandAndReleasesKinematic()
        {
            var handHost = new GameObject("TransformSyncTestHand");
            try
            {
                var sync = new BasisModelTransformSync();
                Rigidbody body = _host.AddComponent<Rigidbody>();
                var interactable = _host.AddComponent<RecordingPickupInteractable>();
                interactable.RigidRef = body;
                sync.Promote();

                // What OnInteractStart and OnLocalGrabbed leave behind on the local holder.
                var hand = handHost.AddComponent<StubInput>();
                hand.UniqueDeviceIdentifier = "left";
                var inputs = new BasisInputSources(0);
                inputs.leftHand = HeldBy(hand);
                interactable.Inputs = inputs;
                body.isKinematic = true;
                interactable._previousKinematicValue = false;

                sync.Demote(_host.transform, body, interactable);

                Assert.That(sync.IsController, Is.False);
                Assert.That(interactable.EndedCount, Is.EqualTo(1), "the local hand kept holding a pickup another player now controls");
                Assert.That(interactable._previousKinematicValue, Is.True, "the release would hand a follower back to physics");
                Assert.That(body.isKinematic, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(handHost);
            }
        }

        private sealed class StubInput : BasisInput
        {
            public override void LateDoPollData() { }
            public override void ShowTrackedVisual() { }
            public override void PlayHaptic(float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f) { }
            public override void PlaySoundEffect(string SoundEffectName, float Volume) { }
        }

        /// <summary>
        /// The stub hand has no tracked role, so the base release returns early; counting the call is what
        /// shows the hold was ended.
        /// </summary>
        private sealed class RecordingPickupInteractable : BasisPickupInteractable
        {
            public int EndedCount;

            public override void OnInteractEnd(BasisInput input)
            {
                EndedCount++;
                base.OnInteractEnd(input);
            }
        }

        private static BasisInputWrapper HeldBy(BasisInput input)
        {
            FieldInfo stateField = typeof(BasisInputWrapper).GetField(
                "State",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(stateField, Is.Not.Null);
            object boxed = default(BasisInputWrapper);
            stateField.SetValue(boxed, BasisInteractInputState.Interacting);
            var wrapper = (BasisInputWrapper)boxed;
            wrapper.Source = input;
            return wrapper;
        }
    }
}
