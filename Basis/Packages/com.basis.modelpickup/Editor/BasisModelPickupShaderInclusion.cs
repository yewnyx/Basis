using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.ModelPickup.Editor
{
    /// <summary>
    /// The model back panel's labels switch to TextMeshPro's depth-tested "Distance Field" shader through
    /// <c>Shader.Find</c> (BasisModelBackPanel), which in a player only resolves shaders the build kept. Nothing in
    /// the package references that shader from a material, so it goes in Always Included Shaders before every
    /// build, as the framework does for its far-LOD shader. Without it the labels keep the default font's overlay
    /// material and draw through geometry.
    /// </summary>
    public sealed class BasisModelPickupShaderInclusion : IPreprocessBuildWithReport
    {
        public const string DistanceFieldShaderName = "TextMeshPro/Distance Field";

        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Editor;

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            EnsureIncluded();
        }

        public static void EnsureIncluded()
        {
            Shader shader = Shader.Find(DistanceFieldShaderName);
            if (shader == null)
            {
                BasisDebug.LogError(
                    $"Model pickup: shader '{DistanceFieldShaderName}' not found; back-panel labels will draw through geometry in builds.",
                    LogTag
                );
                return;
            }

            var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty included = graphics.FindProperty("m_AlwaysIncludedShaders");
            if (included == null)
            {
                BasisDebug.LogError(
                    $"Model pickup: could not reach Always Included Shaders; add '{DistanceFieldShaderName}' under Project Settings > Graphics.",
                    LogTag
                );
                return;
            }

            int count = included.arraySize;
            for (int index = 0; index < count; index++)
            {
                if (included.GetArrayElementAtIndex(index).objectReferenceValue == shader)
                    return;
            }

            included.InsertArrayElementAtIndex(count);
            included.GetArrayElementAtIndex(count).objectReferenceValue = shader;
            graphics.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            BasisDebug.Log($"Model pickup: added '{DistanceFieldShaderName}' to Always Included Shaders.", LogTag);
        }
    }
}
