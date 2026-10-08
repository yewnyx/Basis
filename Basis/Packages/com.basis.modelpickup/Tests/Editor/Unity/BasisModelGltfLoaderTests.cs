using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Basis.ModelPickup.Tests.Validation;
using Basis.ModelPickup.Validation;
using GLTFast;
using GLTFast.Loading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using static Basis.ModelPickup.Tests.BasisModelUnityTestSupport;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelGltfLoaderTests
    {
        private readonly List<BasisModelImportResult> _results = new List<BasisModelImportResult>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _results.Count; i++)
                _results[i]?.Dispose();
            _results.Clear();
        }

        private Task<BasisModelImportResult> Import(
            in BasisGlbValidationResult validated,
            BasisModelImportTicket ticket,
            IDeferAgent agent = null,
            BasisModelImportOptions? options = null
        )
        {
            Task<BasisModelImportResult> task = BasisModelGltfLoader.ImportAsync(
                validated.CleanGlb,
                validated.Stats,
                options ?? Options,
                agent ?? new UninterruptedDeferAgent(),
                ticket
            );
            return task;
        }

        private BasisModelImportResult Keep(Task<BasisModelImportResult> task)
        {
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.IsFaulted, Is.False, "ImportAsync must never throw");
            BasisModelImportResult result = task.Result;
            _results.Add(result);
            return result;
        }

        // With nothing logged, CollectingLogger.Items is null; the finally block must still clean up and forward
        // nothing. A local ticket forwards every item, so no log line at all means the logger stayed empty.
        [UnityTest]
        public IEnumerator CleanImportWithNoLogItemsSucceeds()
        {
            BasisGlbValidationResult validated = Canonical(BasisGlbTestBuilder.IndexedQuad16());
            Task<BasisModelImportResult> task = Import(validated, new BasisModelImportTicket("clean quad", false));
            yield return WaitFor(task);
            BasisModelImportResult result = Keep(task);

            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.Cancelled, Is.False);
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Holder, Is.Not.Null);
            Assert.That(result.Import, Is.Not.Null);
            Assert.That(result.Holder.activeSelf, Is.False, "the holder stays inactive until a pickup adopts it");
            Assert.That(result.Holder.transform.parent, Is.Null, "the holder is a standalone root during import");
            Renderer[] renderers = result.Holder.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers.Length, Is.EqualTo(1));
            Assert.That(renderers[0].shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
            Assert.That(renderers[0].receiveShadows, Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        // glTFast releases its image list when loading ends, so a GetImage check would check nothing.
        [UnityTest]
        public IEnumerator VerifyUsesTexturesNotNulledImages()
        {
            BasisGlbValidationResult validated = Canonical(BasisGlbTestBuilder.TexturedQuad(8, 4));
            Task<BasisModelImportResult> task = Import(validated, new BasisModelImportTicket("textured quad", true));
            yield return WaitFor(task);
            BasisModelImportResult result = Keep(task);
            Assert.That(result.Ok, Is.True, result.Error);

            Assert.That(result.Import.ImageCount, Is.EqualTo(0), "glTFast has released its images");
            Assert.That(result.Import.GetImage(0), Is.Null);
            Assert.That(result.Import.TextureCount, Is.EqualTo(1));
            Texture2D texture = result.Import.GetTexture(0);
            Assert.That(texture.width, Is.EqualTo(8));
            Assert.That(texture.height, Is.EqualTo(4));

            BasisGlbStats stats = validated.Stats;
            Assert.That(BasisModelGltfLoader.VerifyAgainstStats(result.Import, result.Holder, stats, out string reason), Is.True, reason);

            BasisGlbStats narrow = stats;
            narrow.MaxTextureDimension = 7;
            Assert.That(BasisModelGltfLoader.VerifyAgainstStats(result.Import, result.Holder, narrow, out reason), Is.False);
            StringAssert.Contains("texture", reason);

            BasisGlbStats fewerPixels = stats;
            fewerPixels.TexturePixels = 31;
            Assert.That(BasisModelGltfLoader.VerifyAgainstStats(result.Import, result.Holder, fewerPixels, out reason), Is.False);
            StringAssert.Contains("texture pixels", reason);
        }

        // Every corpus entry, through the sender and receiver validators and then glTFast with the deny-all provider.
        [UnityTest]
        public IEnumerator ValidatorCorpusImportsWithinItsStatsAndLeavesNothingBehind()
        {
            yield return WarmUp();
            BasisModelImportOptions noShadows = Options;
            noShadows.CastShadows = false;
            ObjectCounts before = CountObjects();
            List<BasisGlbTestCorpus.Entry> corpus = BasisGlbTestCorpus.All();
            Assert.That(corpus.Count, Is.GreaterThan(0));
            for (int e = 0; e < corpus.Count; e++)
            {
                BasisGlbTestCorpus.Entry entry = corpus[e];
                BasisGlbValidationResult validated = Canonical(entry.Bytes, entry.Format);
                BasisGlbStats stats = validated.Stats;
                Task<BasisModelImportResult> task = Import(validated, new BasisModelImportTicket(entry.Name, true), null, noShadows);
                yield return WaitFor(task);
                BasisModelImportResult result = Keep(task);
                Assert.That(result.Ok, Is.True, entry.Name + ": " + result.Error);

                GameObject holder = result.Holder;
                Assert.That(holder.activeSelf, Is.False, entry.Name);
                Assert.That(holder.transform.childCount, Is.EqualTo(1), entry.Name + ": one scene object under the holder");
                foreach (Type excluded in new[] { typeof(Camera), typeof(Light), typeof(Animation), typeof(Animator), typeof(Collider), typeof(MonoBehaviour) })
                    Assert.That(holder.GetComponentsInChildren(excluded, true).Length, Is.EqualTo(0), entry.Name + ": " + excluded.Name);

                Renderer[] renderers = holder.GetComponentsInChildren<Renderer>(true);
                Assert.That(renderers.Length, Is.GreaterThan(0), entry.Name);
                Assert.That(renderers.Length, Is.LessThanOrEqualTo(stats.DrawCalls), entry.Name + ": renderers");
                Transform[] transforms = holder.GetComponentsInChildren<Transform>(true);
                Assert.That(transforms.Length, Is.LessThanOrEqualTo(2 + stats.Nodes + stats.DrawCalls), entry.Name + ": transforms");
                for (int r = 0; r < renderers.Length; r++)
                {
                    Assert.That(renderers[r].shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off), entry.Name);
                    Assert.That(renderers[r].receiveShadows, Is.False, entry.Name);
                }

                // Static meshes give up their CPU copy; skinned ones keep it for CPU skinning.
                foreach (MeshFilter filter in holder.GetComponentsInChildren<MeshFilter>(true))
                    Assert.That(filter.sharedMesh.isReadable, Is.False, entry.Name + ": static mesh " + filter.name);
                foreach (SkinnedMeshRenderer skinned in holder.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    Assert.That(skinned.sharedMesh.isReadable, Is.True, entry.Name + ": skinned mesh " + skinned.name);
                for (int t = 0; t < result.Import.TextureCount; t++)
                {
                    Texture2D texture = result.Import.GetTexture(t);
                    Assert.That(texture, Is.Not.Null, entry.Name);
                    Assert.That(texture.isReadable, Is.False, entry.Name + ": texture " + t);
                }

                GltfImport import = result.Import;
                result.Dispose();
                Assert.That(holder == null, Is.True, entry.Name + ": holder destroyed");
                Assert.That(import.Meshes, Is.Null, entry.Name + ": meshes released");
                Assert.That(import.TextureCount, Is.EqualTo(0), entry.Name);
                Assert.That(import.MaterialCount, Is.EqualTo(0), entry.Name);
                AssertSameCounts(before, CountObjects(), entry.Name);
            }
        }

        [UnityTest]
        public IEnumerator UnderstatedStatsFailAndLeakNothing()
        {
            yield return WarmUp();
            ObjectCounts before = CountObjects();
            BasisGlbValidationResult validated = Canonical(BasisGlbTestBuilder.Triangle());
            BasisGlbStats lying = validated.Stats;
            lying.Vertices = 2;
            Task<BasisModelImportResult> task = BasisModelGltfLoader.ImportAsync(
                validated.CleanGlb,
                lying,
                Options,
                new UninterruptedDeferAgent(),
                new BasisModelImportTicket("understated", true)
            );
            yield return WaitFor(task);
            BasisModelImportResult result = Keep(task);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Cancelled, Is.False);
            StringAssert.Contains("does not match its validated description", result.Error);
            StringAssert.Contains("vertices", result.Error);
            Assert.That(result.Holder, Is.Null);
            Assert.That(result.Import, Is.Null);
            AssertSameCounts(before, CountObjects(), "understated");
        }

        // glTFast ignores cancellation; an abandoned ticket must still end with everything destroyed.
        [UnityTest]
        public IEnumerator AbandonedTicketDisposesEverything()
        {
            yield return WarmUp();
            ObjectCounts before = CountObjects();
            BasisGlbValidationResult validated = Canonical(BasisGlbTestBuilder.TexturedQuad(4, 4));
            var ticket = new BasisModelImportTicket("abandoned", true);
            // Never armed, so every break point yields and the import cannot finish synchronously.
            var agent = new BasisModelDeferAgent(3f);
            Task<BasisModelImportResult> task = Import(validated, ticket, agent);
            Assert.That(task.IsCompleted, Is.False);
            ticket.Abandoned = true;
            yield return WaitFor(task);
            BasisModelImportResult result = Keep(task);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Cancelled, Is.True);
            Assert.That(result.Holder, Is.Null);
            Assert.That(result.Import, Is.Null);
            AssertSameCounts(before, CountObjects(), "abandoned");
        }

        [UnityTest]
        public IEnumerator ArmedDeferAgentCompletesAnImport()
        {
            BasisGlbValidationResult validated = Canonical(BasisGlbTestBuilder.SkinnedStrip(3));
            var agent = new BasisModelDeferAgent(3f);
            agent.BeginFrame();
            Task<BasisModelImportResult> task = Import(validated, new BasisModelImportTicket("sliced", true), agent);
            // Bounded by wall time, not steps: an EditMode step is not a timed frame, and glTFast also waits on jobs.
            Stopwatch clock = Stopwatch.StartNew();
            int frames = 0;
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < TaskTimeoutSeconds)
            {
                yield return null;
                agent.BeginFrame();
                frames++;
            }
            BasisModelImportResult result = Keep(task);
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(frames, Is.GreaterThan(0), "the import spanned several frames");
        }

        // These fail before glTFast is touched, so the task completes synchronously.
        [Test]
        public void MissingInputsFailWithoutThrowingOrCreatingAGlobalAgent()
        {
            BasisGlbValidationResult validated = Canonical(BasisGlbTestBuilder.Triangle());
            ObjectCounts before = CountObjects();
            BasisModelImportResult noAgent = Keep(
                BasisModelGltfLoader.ImportAsync(validated.CleanGlb, validated.Stats, Options, null, new BasisModelImportTicket("no agent", true))
            );
            Assert.That(noAgent.Ok, Is.False);
            StringAssert.Contains("defer agent", noAgent.Error);
            // A null agent passed through would have made glTFast create its global "glTF-StableFramerate" object.
            AssertSameCounts(before, CountObjects(), "no agent");

            BasisModelImportResult noBytes = Keep(
                BasisModelGltfLoader.ImportAsync(null, validated.Stats, Options, new UninterruptedDeferAgent(), new BasisModelImportTicket("no bytes", true))
            );
            Assert.That(noBytes.Ok, Is.False);

            BasisModelImportResult noTicket = Keep(
                BasisModelGltfLoader.ImportAsync(validated.CleanGlb, validated.Stats, Options, new UninterruptedDeferAgent(), null)
            );
            Assert.That(noTicket.Ok, Is.False);
        }

        [Test]
        public void DenyAllProviderRefusesEveryRequestWithoutReadingIt()
        {
            BasisModelDenyAllDownloadProvider provider = BasisModelDenyAllDownloadProvider.Instance;
            var url = new Uri("http://example.invalid/model.bin");

            Task<IDownload> download = provider.Request(url);
            Assert.That(download.IsCompleted, Is.True);
            Assert.That(download.Result.Success, Is.False);
            Assert.That(download.Result.Error, Is.EqualTo(BasisModelDenyAllDownloadProvider.RefusedError));
            Assert.That(download.Result.Data, Is.Null);
            Assert.That(download.Result.Text, Is.Null);
            Assert.That(download.Result.IsBinary, Is.Null);

            Task<ITextureDownload> texture = provider.RequestTexture(new Uri("file:///C:/secret.png"), true);
            Assert.That(texture.IsCompleted, Is.True);
            Assert.That(texture.Result.Success, Is.False);
            Assert.That(texture.Result.Texture, Is.Null);
            Assert.DoesNotThrow(() => texture.Result.Dispose());
            Assert.DoesNotThrow(() => provider.Request(null).Result.Dispose());
        }

        [Test]
        public void RemoteDiagnosticsAreCappedAndCarryNoTags()
        {
            string capped = BasisModelGltfLoader.SanitizeRemoteText(new string('a', 300) + "<b>");
            Assert.That(capped.Length, Is.EqualTo(BasisModelGltfLoader.MaxRemoteLogCharacters));

            string defused = BasisModelGltfLoader.SanitizeRemoteText("bad <color=red>name</color>");
            Assert.That(defused.IndexOf('<'), Is.EqualTo(-1));
            Assert.That(defused, Is.EqualTo("bad \u2039color=red>name\u2039/color>"));
            Assert.That(BasisModelGltfLoader.SanitizeRemoteText(null), Is.EqualTo(string.Empty));
        }
    }
}
