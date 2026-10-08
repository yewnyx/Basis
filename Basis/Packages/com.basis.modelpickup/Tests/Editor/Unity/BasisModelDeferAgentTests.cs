using System.Collections;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelDeferAgentTests
    {
        // Without a driver tick, an import must not run unpaced on the main thread.
        [Test]
        public void UnarmedAgentYieldsAtEveryBreakPoint()
        {
            var agent = new BasisModelDeferAgent(3f);
            Assert.That(agent.ShouldDefer(), Is.True);
            Assert.That(agent.BreakPoint().IsCompleted, Is.False);
            Assert.That(agent.BreakPoint(0.0001f).IsCompleted, Is.False);
        }

        [Test]
        public void TheFirstStepOfAFrameAlwaysRuns()
        {
            var agent = new BasisModelDeferAgent(3f);
            agent.BeginFrame();
            Assert.That(agent.BreakPoint(10f).IsCompleted, Is.True, "a step predicted to take longer than the slice still runs first");
        }

        [Test]
        public void ASpentSliceYieldsUntilTheNextFrame()
        {
            var agent = new BasisModelDeferAgent(0.5f);
            agent.BeginFrame();
            Assert.That(agent.BreakPoint().IsCompleted, Is.True);
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalMilliseconds < 2d)
            {
            }
            Assert.That(agent.ShouldDefer(), Is.True);
            Assert.That(agent.BreakPoint().IsCompleted, Is.False);

            agent.BeginFrame();
            Assert.That(agent.BreakPoint().IsCompleted, Is.True, "a new frame starts a fresh slice");
        }

        // glTFast asks ShouldDefer(duration) only to choose a worker over the main thread (JSON parse, base64 decode).
        [Test]
        public void WorkLongerThanTheWholeSliceGoesToAWorker()
        {
            var agent = new BasisModelDeferAgent(3f);
            agent.BeginFrame();
            Assert.That(agent.ShouldDefer(0.05f), Is.True);
            Assert.That(agent.ShouldDefer(0.0001f), Is.False, "the slice itself was not started by the worker decision");
        }

        [UnityTest]
        public IEnumerator AYieldResumesOnALaterFrame()
        {
            var agent = new BasisModelDeferAgent(3f);
            bool resumed = false;
            async Task Step()
            {
                await agent.BreakPoint();
                resumed = true;
            }

            Task step = Step();
            Assert.That(resumed, Is.False, "the yield does not continue synchronously");
            int frames = 0;
            Stopwatch clock = Stopwatch.StartNew();
            while (!step.IsCompleted && clock.Elapsed.TotalSeconds < 10d)
            {
                yield return null;
                frames++;
            }
            Assert.That(resumed, Is.True);
            Assert.That(frames, Is.GreaterThanOrEqualTo(1), "it resumed only after the editor loop ran");
        }
    }
}
