using System;
using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The replication range in force, said out loud once per change. The range is advertised by the server in
    /// the join handshake, so every way it fails silently (an older server, the setting at 0, a handshake that
    /// never landed) looks the same in the world: everything loads at every distance. Printing the figure
    /// separates "the range is not applied" from "nobody set one".
    /// </summary>
    public sealed class BasisModelRangeReporter
    {
        /// <summary>
        /// Null reads <c>ImagePickupRangeMeters</c> from the server metadata: models replicate over the same
        /// advertised range as images.
        /// </summary>
        public Func<float> AdvertisedRange;

        private readonly string _logPrefix;
        private readonly string _itemNoun;
        private readonly BasisDebug.LogTag _logTag;

        /// <summary>NaN means nothing reported yet, so the first real read always prints.</summary>
        private float _lastReportedRangeMeters = float.NaN;

        public BasisModelRangeReporter(string logPrefix, string itemNoun, BasisDebug.LogTag logTag)
        {
            _logPrefix = logPrefix;
            _itemNoun = itemNoun;
            _logTag = logTag;
        }

        /// <summary>The advertised range in metres, never negative. Zero is unlimited.</summary>
        public float ServerRangeMeters()
        {
            float advertised = AdvertisedRange != null
                ? AdvertisedRange()
                : BasisNetworkManagement.ServerMetaDataMessage.ImagePickupRangeMeters;
            float rangeMeters = Mathf.Max(0f, advertised);
            Report(rangeMeters);
            return rangeMeters;
        }

        /// <summary>Forgets the last value, so the next read prints again (a new server may say something else).</summary>
        public void Reset()
        {
            _lastReportedRangeMeters = float.NaN;
        }

        private void Report(float rangeMeters)
        {
            if (rangeMeters == _lastReportedRangeMeters)
                return;
            _lastReportedRangeMeters = rangeMeters;

            if (rangeMeters <= 0f)
            {
                BasisDebug.LogWarning(
                    $"{_logPrefix} replication range is UNLIMITED - every {_itemNoun} replicates and loads at any "
                        + "distance. The server advertised ImagePickupRangeMeters=0, or it is old enough not to "
                        + "send the field at all. Set ImagePickupRangeMeters in the server config to enable "
                        + "distance-based loading.",
                    _logTag
                );
                return;
            }

            BasisDebug.Log($"{_logPrefix} replication range is {rangeMeters:0.##}m (advertised by the server).", _logTag);
        }
    }
}
