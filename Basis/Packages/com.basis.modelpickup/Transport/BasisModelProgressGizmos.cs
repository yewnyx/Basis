using System;
using System.Collections.Generic;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Billboarded transfer readouts under model pickups: how much has moved and how fast. The manager reports
    /// every transfer still in flight each tick; a label goes the moment its transfer stops being reported, and
    /// the manager clears them all whenever it has no work.
    ///
    /// Labels are created outside the debug-gizmo toggles (<c>BasisGizmoManager.Render</c> runs regardless), so
    /// a player sees progress without turning anything on. The master toggle going off still destroys every
    /// gizmo, hence the hook.
    /// </summary>
    public sealed class BasisModelProgressGizmos
    {
        private const float LabelBaseScale = 0.02f;
        private static readonly Color InboundColor = new Color(0.55f, 0.80f, 1f, 1f);
        private static readonly Color OutboundColor = new Color(0.35f, 0.70f, 1f, 1f);

        public struct ProgressLabel
        {
            public int Label;
            public int TextKey;
            public string Text;
        }

        private readonly string _namePrefix;
        /// <summary>The live label per transfer id.</summary>
        public readonly Dictionary<Guid, ProgressLabel> Labels = new Dictionary<Guid, ProgressLabel>();
        private readonly HashSet<Guid> _seen = new HashSet<Guid>();
        private readonly List<Guid> _stale = new List<Guid>();
        private Vector3 _cameraPosition;
        private float _scale = 1f;
        private bool _hooked;

        /// <param name="gizmoNamePrefix">Label object names are this plus the id in "N" form.</param>
        public BasisModelProgressGizmos(string gizmoNamePrefix)
        {
            _namePrefix = gizmoNamePrefix ?? string.Empty;
        }

        public int ActiveCount => Labels.Count;

        public void BeginFrame()
        {
            EnsureMasterHook();
            _seen.Clear();
            _cameraPosition = BasisLocalCameraDriver.Position;
            float scale = BasisHeightDriver.ScaledToMatchValue;
            _scale = scale > 0f ? scale : 1f;
        }

        /// <summary>
        /// Raises or refreshes the label for one in-flight transfer. <paramref name="progress"/> is a fraction of
        /// the whole and <paramref name="bytesPerSecond"/> the smoothed throughput. The text is rebuilt only when
        /// it would read differently.
        /// </summary>
        public void Report(Guid id, Vector3 anchor, float progress, float bytesPerSecond, bool outbound)
        {
            _seen.Add(id);
            Labels.TryGetValue(id, out ProgressLabel entry);

            int percent = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f), 0, 100);
            Color color = outbound ? OutboundColor : InboundColor;

            if (entry.Label <= 0 || BasisGizmoManager.IsTextVisible(entry.Label))
            {
                int key = BasisModelProgressText.TextKey(percent, bytesPerSecond, outbound);
                if (entry.Label <= 0 || key != entry.TextKey || entry.Text == null)
                {
                    entry.TextKey = key;
                    entry.Text = BasisModelProgressText.BuildText(percent, bytesPerSecond, outbound);
                }
            }

            Quaternion rotation = BasisGizmoManager.BillboardRotation(anchor, _cameraPosition);
            if (entry.Label <= 0)
            {
                BasisGizmoManager.CreateTextGizmo(
                    string.Concat(_namePrefix, id.ToString("N")),
                    out entry.Label,
                    anchor,
                    entry.Text,
                    color
                );
            }
            BasisGizmoManager.UpdateTextGizmo(entry.Label, anchor, rotation, LabelBaseScale * _scale, entry.Text, color);

            Labels[id] = entry;
        }

        /// <summary>Removes every label that was not reported since <see cref="BeginFrame"/>.</summary>
        public void EndFrame()
        {
            if (Labels.Count == _seen.Count)
                return;

            _stale.Clear();
            foreach (KeyValuePair<Guid, ProgressLabel> entry in Labels)
            {
                if (!_seen.Contains(entry.Key))
                    _stale.Add(entry.Key);
            }
            int staleCount = _stale.Count;
            for (int i = 0; i < staleCount; i++)
                Remove(_stale[i]);
            _stale.Clear();
        }

        public void Remove(Guid id)
        {
            if (!Labels.TryGetValue(id, out ProgressLabel entry))
                return;
            if (entry.Label > 0)
                BasisGizmoManager.DestroyGizmo(entry.Label);
            Labels.Remove(id);
        }

        /// <summary>Drops this instance's labels only. Cheap when there are none, so managers call it whenever idle.</summary>
        public void Shutdown()
        {
            if (Labels.Count == 0)
                return;
            foreach (KeyValuePair<Guid, ProgressLabel> entry in Labels)
            {
                if (entry.Value.Label > 0)
                    BasisGizmoManager.DestroyGizmo(entry.Value.Label);
            }
            Labels.Clear();
            _seen.Clear();
        }

        private void EnsureMasterHook()
        {
            if (_hooked)
                return;
            BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
            _hooked = true;
        }

        // The gizmo manager has already destroyed every label; only the ids are left to forget.
        private void OnMasterToggleChanged(bool state)
        {
            if (!state)
                Labels.Clear();
        }
    }
}
