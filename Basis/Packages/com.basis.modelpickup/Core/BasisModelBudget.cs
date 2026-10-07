using System;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Running totals over a set of models (one sender's, or everything resident). Sums saturate instead of wrapping
    /// and negative inputs add nothing, so no claim can make a total smaller or roll it over.
    /// </summary>
    public struct BasisModelAggregate
    {
        public int Count;

        /// <summary>Wire (canonical GLB) bytes.</summary>
        public long Bytes;

        public long Vertices;
        public long TexturePixels;

        /// <summary>Estimated decoded memory plus the canonical GLB a pickup keeps for Save.</summary>
        public long ResidentBytes;

        public long DrawCalls;
        public long RenderedTriangles;
        public long SkinnedVertexInstances;
        public long Nodes;

        /// <summary>
        /// Adds one model. <paramref name="retainsGlb"/> is false only for a received model whose GLB was dropped after
        /// import (Mobile receivers); a model still downloading, validating or importing always holds its bytes.
        /// </summary>
        public void Add(in BasisGlbClaims claims, int totalBytes, bool retainsGlb = true)
        {
            long wireBytes = Math.Max(0, totalBytes);
            Count = Count == int.MaxValue ? int.MaxValue : Count + 1;
            Bytes = SaturatingAdd(Bytes, wireBytes);
            Vertices = SaturatingAdd(Vertices, claims.Vertices);
            TexturePixels = SaturatingAdd(TexturePixels, claims.TexturePixels);
            ResidentBytes = SaturatingAdd(ResidentBytes, claims.EstimatedDecodedBytes);
            if (retainsGlb)
                ResidentBytes = SaturatingAdd(ResidentBytes, wireBytes);
            DrawCalls = SaturatingAdd(DrawCalls, claims.DrawCalls);
            RenderedTriangles = SaturatingAdd(RenderedTriangles, claims.RenderedTriangles);
            SkinnedVertexInstances = SaturatingAdd(SkinnedVertexInstances, claims.SkinnedVertexInstances);
            Nodes = SaturatingAdd(Nodes, claims.Nodes);
        }

        private static long SaturatingAdd(long total, long value)
        {
            if (value <= 0)
                return total;
            return total > long.MaxValue - value ? long.MaxValue : total + value;
        }
    }

    /// <summary>
    /// Aggregate budget predicates. Each takes totals that already include the candidate and names the first budget
    /// it would exceed, e.g. "per-sender vertex limit of 2,000,000". A negative total fails as invalid.
    /// </summary>
    public static class BasisModelBudget
    {
        private const string SenderScope = "per-sender";
        private const string ResidentScope = "resident";

        public static bool IsWithinSenderLimits(in BasisModelAggregate total, in BasisModelSenderLimits limits, out string reason)
        {
            return Within(total.Count, limits.MaxModels, SenderScope, "model count", false, out reason)
                && Within(total.Bytes, limits.MaxBytes, SenderScope, "download", true, out reason)
                && Within(total.Vertices, limits.MaxVertices, SenderScope, "vertex", false, out reason)
                && Within(total.TexturePixels, limits.MaxTexturePixels, SenderScope, "texture pixel", false, out reason)
                && Within(total.ResidentBytes, limits.MaxResidentBytes, SenderScope, "memory", true, out reason)
                && Within(total.DrawCalls, limits.MaxDrawCalls, SenderScope, "draw call", false, out reason)
                && Within(total.RenderedTriangles, limits.MaxRenderedTriangles, SenderScope, "rendered triangle", false, out reason)
                && Within(total.SkinnedVertexInstances, limits.MaxSkinnedVertexInstances, SenderScope, "skinned vertex", false, out reason)
                && Within(total.Nodes, limits.MaxNodes, SenderScope, "node", false, out reason);
        }

        public static bool IsWithinResidentLimits(in BasisModelAggregate total, in BasisModelResidentLimits limits, out string reason)
        {
            return Within(total.Count, limits.MaxModels, ResidentScope, "model count", false, out reason)
                && Within(total.ResidentBytes, limits.MaxResidentBytes, ResidentScope, "memory", true, out reason)
                && Within(total.DrawCalls, limits.MaxDrawCalls, ResidentScope, "draw call", false, out reason)
                && Within(total.RenderedTriangles, limits.MaxRenderedTriangles, ResidentScope, "rendered triangle", false, out reason)
                && Within(total.SkinnedVertexInstances, limits.MaxSkinnedVertexInstances, ResidentScope, "skinned vertex", false, out reason)
                && Within(total.Nodes, limits.MaxNodes, ResidentScope, "node", false, out reason);
        }

        /// <summary>
        /// How many more local drops may start. Every local job counts from the moment it is queued, not only finished
        /// models, so a burst of drops cannot overshoot the cap while earlier ones are still loading.
        /// </summary>
        public static int AvailableLocalSlots(int ownedCount, int inFlightLocalCount, int maxModels)
        {
            long free = (long)maxModels - Math.Max(0, ownedCount) - Math.Max(0, inFlightLocalCount);
            return free <= 0 ? 0 : (int)free;
        }

        private static bool Within(long total, long limit, string scope, string budget, bool isBytes, out string reason)
        {
            if (total >= 0 && total <= limit)
            {
                reason = null;
                return true;
            }
            reason = total < 0
                ? scope + " " + budget + " total is invalid"
                : scope + " " + budget + " limit of " + (isBytes ? BasisGlbErrors.FormatBytes(limit) : BasisGlbErrors.N(limit));
            return false;
        }
    }
}
