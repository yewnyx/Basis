using System;
using System.Globalization;
using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Basis.ModelPickup
{
    /// <summary>What a pickup's back panel shows and drives.</summary>
    public interface IBasisModelBackPanelTarget
    {
        bool IsOwner { get; }
        string OwnerName { get; }
        bool IsHidden { get; }
        void OnHidePressed();
        void OnSavePressed();
        void OnDeletePressed();

        /// <summary>Hands the target the two labels it rewrites itself (Hide/Show, Delete/Confirm).</summary>
        void BindBackPanelLabels(TextMeshProUGUI hideLabel, TextMeshProUGUI deleteLabel);
    }

    /// <summary>A pickup whose back panel follows the main menu (see <see cref="BasisModelBackPanelSync"/>).</summary>
    public interface IBasisModelBackPanelHost
    {
        bool BackPanelVisible { get; }
        void SetBackPanelVisible(bool visible);
    }

    /// <summary>Where the panel sits on its pickup, and how big it is in the world.</summary>
    public struct BasisModelBackPanelLayout
    {
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public float WorldHeight;
        public bool ShowSave;
    }

    /// <summary>The panel's text, localised by the caller.</summary>
    public struct BasisModelBackPanelLabels
    {
        public string SpawnedLocally;

        /// <summary>"{0}" is the owner's name, shown as plain text.</summary>
        public string SpawnedByFormat;

        public string Hide;
        public string Show;
        public string Save;
        public string Delete;
    }

    /// <summary>
    /// Builds the world-space control panel on a pickup: a spawner label plus Hide, Save and Delete. Uses the
    /// Basis world-UI stack so the VR laser and the desktop cursor can click it. Built on demand the first time
    /// the menu opens and then only toggled; the returned object is the canvas root.
    /// </summary>
    public static class BasisModelBackPanel
    {
        public const float PanelPixels = 400f;

        private const int FallbackUiLayer = 5;
        private const float SpawnerFontSize = 30f;
        private const float ButtonFontSize = 28f;
        private static readonly Vector2 ButtonSize = new Vector2(120f, 80f);
        private static readonly Color ButtonColor = new Color(0.80f, 0.82f, 0.88f, 0.95f);

        private static Shader _distanceFieldShader;

        /// <summary>One depth-tested copy of the font's material, shared by every label (see <see cref="GetLabelMaterial"/>).</summary>
        public static Material LabelMaterial;

        private static Material _labelMaterialSource;

        public static GameObject Build(
            Transform parent,
            IBasisModelBackPanelTarget target,
            in BasisModelBackPanelLayout layout,
            in BasisModelBackPanelLabels labels
        )
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0)
                uiLayer = FallbackUiLayer;

            var canvasObject = new GameObject("BackPanel", typeof(RectTransform));
            canvasObject.layer = uiLayer;
            canvasObject.SetActive(false);

            var canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.SetParent(parent, false);
            canvasRect.sizeDelta = new Vector2(PanelPixels, PanelPixels);
            Place(canvasObject, layout);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var scaler = canvasObject.AddComponent<CanvasScaler>();

            var rayCaster = canvasObject.AddComponent<BasisGraphicUIRayCaster>();
            rayCaster.Canvas = canvas;

            var component = canvasObject.AddComponent<BasisUIComponent>();
            component.Canvas = canvas;
            component.CanvasScaler = scaler;
            component.GraphicUIRayCaster = rayCaster;

            string ownerName = target.OwnerName;
            bool localUnknown = target.IsOwner && (string.IsNullOrEmpty(ownerName) || ownerName == "Unknown");
            string spawnerText = localUnknown ? labels.SpawnedLocally : FormatSpawnedBy(labels.SpawnedByFormat, ownerName);
            TextMeshProUGUI spawnerLabel = CreateLabel(
                canvasRect,
                uiLayer,
                spawnerText,
                new Vector2(0f, 150f),
                new Vector2(PanelPixels - 20f, 80f),
                SpawnerFontSize
            );
            // A display name is untrusted text; shown literally it cannot inject tags into the label.
            spawnerLabel.richText = false;

            TextMeshProUGUI hideLabel = CreateButton(
                canvasRect,
                uiLayer,
                "HideButton",
                target.IsHidden ? labels.Show : labels.Hide,
                new Vector2(-130f, -120f),
                target.OnHidePressed
            );
            if (layout.ShowSave)
                CreateButton(canvasRect, uiLayer, "SaveButton", labels.Save, new Vector2(0f, -120f), target.OnSavePressed);
            TextMeshProUGUI deleteLabel = CreateButton(
                canvasRect,
                uiLayer,
                "DeleteButton",
                labels.Delete,
                new Vector2(130f, -120f),
                target.OnDeletePressed
            );
            target.BindBackPanelLabels(hideLabel, deleteLabel);

            canvasObject.SetActive(true);
            return canvasObject;
        }

        /// <summary>Moves and resizes a built panel, e.g. when its pickup changes shape.</summary>
        public static void Place(GameObject panel, in BasisModelBackPanelLayout layout)
        {
            if (panel == null)
                return;
            Transform panelTransform = panel.transform;
            panelTransform.SetLocalPositionAndRotation(layout.LocalPosition, layout.LocalRotation);
            panelTransform.localScale = Vector3.one * (layout.WorldHeight / PanelPixels);
        }

        private static string FormatSpawnedBy(string format, string ownerName)
        {
            if (string.IsNullOrEmpty(format))
                return ownerName ?? string.Empty;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, format, ownerName);
            }
            catch (FormatException)
            {
                // A broken translation must not cost the whole panel.
                return format + " " + ownerName;
            }
        }

        private static TextMeshProUGUI CreateButton(
            RectTransform parent,
            int layer,
            string objectName,
            string label,
            Vector2 anchoredPosition,
            UnityAction onClick
        )
        {
            var buttonObject = new GameObject(objectName, typeof(RectTransform));
            buttonObject.layer = layer;

            var rect = (RectTransform)buttonObject.transform;
            rect.SetParent(parent, false);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = ButtonSize;

            var image = buttonObject.AddComponent<Image>();
            image.color = ButtonColor;
            image.raycastTarget = true;

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            return CreateLabel(rect, layer, label, Vector2.zero, ButtonSize, ButtonFontSize);
        }

        private static TextMeshProUGUI CreateLabel(
            RectTransform parent,
            int layer,
            string text,
            Vector2 anchoredPosition,
            Vector2 size,
            float fontSize
        )
        {
            var labelObject = new GameObject("Label", typeof(RectTransform));
            labelObject.layer = layer;

            var rect = (RectTransform)labelObject.transform;
            rect.SetParent(parent, false);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var tmp = labelObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.black;
            tmp.raycastTarget = false;
            Material labelMaterial = GetLabelMaterial(tmp.fontSharedMaterial);
            if (labelMaterial != null)
                tmp.fontSharedMaterial = labelMaterial;
            return tmp;
        }

        /// <summary>
        /// The font's material with TextMeshPro's depth-tested shader, built once and shared. Reading
        /// <c>TextMeshProUGUI.fontMaterial</c> instead would create a material per label that TextMeshPro never
        /// destroys. Rebuilt if the font's material changes; null (keep the font's own material) without the shader.
        /// </summary>
        private static Material GetLabelMaterial(Material fontMaterial)
        {
            if (fontMaterial == null)
                return null;
            if (LabelMaterial != null && _labelMaterialSource == fontMaterial)
                return LabelMaterial;
            Shader distanceField = GetDistanceFieldShader();
            if (distanceField == null)
                return null;
            LabelMaterial = new Material(fontMaterial) { name = "Model Back Panel Label", shader = distanceField };
            _labelMaterialSource = fontMaterial;
            return LabelMaterial;
        }

        /// <summary>
        /// TextMeshPro's depth-tested SDF shader, looked up once and cached. Players resolve it because the package's
        /// build step (Basis.ModelPickup.Editor's BasisModelPickupShaderInclusion) puts it in Always Included
        /// Shaders; if it is still missing, labels keep the default font's overlay material.
        /// </summary>
        private static Shader GetDistanceFieldShader()
        {
            if (_distanceFieldShader == null)
                _distanceFieldShader = Shader.Find("TextMeshPro/Distance Field");
            return _distanceFieldShader;
        }
    }
}
