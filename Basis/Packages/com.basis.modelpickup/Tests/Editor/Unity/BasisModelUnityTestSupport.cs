using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Basis.ModelPickup.Tests.Validation;
using Basis.ModelPickup.Validation;
using GLTFast;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Basis.ModelPickup.Tests
{
    /// <summary>Shared helpers for the model package's Unity (EditMode) tests.</summary>
    internal static class BasisModelUnityTestSupport
    {
        internal const double TaskTimeoutSeconds = 60d;

        internal static readonly BasisModelImportOptions Options = new BasisModelImportOptions
        {
            Layer = 0,
            CastShadows = true,
            GenerateMipMaps = true,
        };

        /// <summary>The sender path, then the receiver path on the sender's output, exactly as two clients run them.</summary>
        internal static BasisGlbValidationResult Canonical(byte[] source, BasisModelSourceFormat format = BasisModelSourceFormat.Glb)
        {
            BasisGlbValidationResult sent = BasisGlbTestRun.Send(source, format);
            BasisGlbTestRun.AssertOk(sent);
            BasisGlbValidationResult received = BasisGlbTestRun.Receive(sent.CleanGlb);
            BasisGlbTestRun.AssertOk(received);
            return received;
        }

        internal static BasisGlbValidationResult Canonical(BasisGlbTestBuilder builder)
        {
            return Canonical(builder.BuildGlb());
        }

        /// <summary>Lets the editor loop run continuations until <paramref name="task"/> finishes.</summary>
        internal static IEnumerator WaitFor(Task task)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (clock.Elapsed.TotalSeconds > TaskTimeoutSeconds)
                    Assert.Fail("The task did not finish within " + TaskTimeoutSeconds + " s.");
                yield return null;
            }
        }

        /// <summary>Counts of the engine objects an import creates, to show that one leaves none behind.</summary>
        internal struct ObjectCounts
        {
            public int Meshes;
            public int Textures;
            public int Materials;
            public int GameObjects;

            public override string ToString()
            {
                return Meshes + " meshes, " + Textures + " textures, " + Materials + " materials, " + GameObjects + " game objects";
            }
        }

        internal static ObjectCounts CountObjects()
        {
            return new ObjectCounts
            {
                Meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length,
                Textures = Resources.FindObjectsOfTypeAll<Texture2D>().Length,
                Materials = Resources.FindObjectsOfTypeAll<Material>().Length,
                GameObjects = Resources.FindObjectsOfTypeAll<GameObject>().Length,
            };
        }

        internal static void AssertSameCounts(in ObjectCounts before, in ObjectCounts after, string context)
        {
            Assert.That(after.Meshes, Is.EqualTo(before.Meshes), context + ": meshes leaked (" + after + " vs " + before + ")");
            Assert.That(after.Textures, Is.EqualTo(before.Textures), context + ": textures leaked (" + after + " vs " + before + ")");
            Assert.That(after.Materials, Is.EqualTo(before.Materials), context + ": materials leaked (" + after + " vs " + before + ")");
            Assert.That(after.GameObjects, Is.EqualTo(before.GameObjects), context + ": game objects leaked (" + after + " vs " + before + ")");
        }

        private static bool s_WarmedUp;

        /// <summary>
        /// One untextured and one textured import, disposed. glTFast creates a process-wide default material and
        /// shader-graph state on first use that no import owns; leak counts are taken after this.
        /// </summary>
        internal static IEnumerator WarmUp()
        {
            if (s_WarmedUp)
                yield break;
            BasisGlbTestBuilder[] builders = { BasisGlbTestBuilder.Triangle(), BasisGlbTestBuilder.TexturedQuad(4, 4) };
            for (int i = 0; i < builders.Length; i++)
            {
                BasisGlbValidationResult validated = Canonical(builders[i]);
                Task<BasisModelImportResult> task = BasisModelGltfLoader.ImportAsync(
                    validated.CleanGlb,
                    validated.Stats,
                    Options,
                    new UninterruptedDeferAgent(),
                    new BasisModelImportTicket("warm-up", true)
                );
                yield return WaitFor(task);
                task.Result.Dispose();
            }
            s_WarmedUp = true;
        }

        /// <summary>Stands in for the manager.</summary>
        internal sealed class FakeHost : IBasisModelPickupHost
        {
            public readonly List<BasisModelPickupObject> Destroyed = new List<BasisModelPickupObject>();
            public readonly List<Guid> Despawns = new List<Guid>();
            public readonly List<Guid> Claims = new List<Guid>();

            /// <summary>Runs inside <see cref="OnPickupDestroyed"/>, to observe what the pickup still holds at that point.</summary>
            public Action<BasisModelPickupObject> OnDestroyedProbe;

            public void OnPickupDestroyed(BasisModelPickupObject pickup)
            {
                Destroyed.Add(pickup);
                OnDestroyedProbe?.Invoke(pickup);
            }

            public void RequestDespawn(Guid id)
            {
                Despawns.Add(id);
            }

            public void ClaimControl(Guid id)
            {
                Claims.Add(id);
            }
        }

        /// <summary>
        /// A stand-in for an import's holder: inactive, with one cube renderer far larger than any bounds the tests use,
        /// so a shape taken from renderer bounds would show.
        /// </summary>
        internal static BasisModelImportResult FakeImport(float rendererSize = 10f)
        {
            var holder = new GameObject("Model");
            holder.SetActive(false);
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (part.TryGetComponent(out Collider collider))
                Object.DestroyImmediate(collider);
            part.transform.SetParent(holder.transform, false);
            part.transform.localScale = new Vector3(rendererSize, rendererSize, rendererSize);
            return new BasisModelImportResult { Ok = true, Holder = holder };
        }

        internal static BasisGlbAabb Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            return new BasisGlbAabb { MinX = minX, MinY = minY, MinZ = minZ, MaxX = maxX, MaxY = maxY, MaxZ = maxZ };
        }
    }
}
