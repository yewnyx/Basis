using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime material migrations for content serialized by older Unity versions. AssetBundle
/// materials do not pass through URP's editor-only MaterialPostprocessor when loaded.
/// </summary>
public static class BasisLegacyMaterialCompatibility
{
    private const int DirectEmissionIntroducedUnityMajor = 6000;
    private const int DirectEmissionIntroducedUnityMinor = 7;
    private const string EmissionKeyword = "_EMISSION";
    private const MaterialGlobalIlluminationFlags AllEmissionFlags =
        MaterialGlobalIlluminationFlags.RealtimeDirectEmission |
        MaterialGlobalIlluminationFlags.RealtimeIndirectEmission |
        MaterialGlobalIlluminationFlags.BakedEmission;

    public static int UpgradeEmission(IList<Renderer> renderers, string builtWithUnityVersion)
    {
        if (renderers == null)
            return 0;

        HashSet<Material> visited = new HashSet<Material>();
        bool forceLegacyMaterials = RequiresLegacyEmissionUpgrade(builtWithUnityVersion);
        int upgraded = 0;
        int legacyCandidates = 0;
        string firstUpgrade = null;
        for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null)
                continue;

            Material[] materials = renderer.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null || !visited.Add(material))
                    continue;

                MaterialGlobalIlluminationFlags flags = material.globalIlluminationFlags;
                if ((flags & AllEmissionFlags) == AllEmissionFlags &&
                    (flags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0)
                    continue;

                bool hadLegacyEmissionIntent = material.IsKeywordEnabled(EmissionKeyword) ||
                    (flags & (MaterialGlobalIlluminationFlags.BakedEmission |
                              MaterialGlobalIlluminationFlags.RealtimeIndirectEmission)) != 0;
                // AssetBundleStripUnityVersion writes "5.x.x" into the UnityFS header and old
                // bundles can deserialize with both the keyword and the old emission bits lost.
                // For an unquestionably legacy/unknown bundle, normalize every material. A black
                // or absent emission input remains visually black; the flags merely make the
                // direct-emission shader path available again.
                if (!forceLegacyMaterials && !hadLegacyEmissionIntent)
                    continue;

                legacyCandidates++;
                material.globalIlluminationFlags = (flags & ~MaterialGlobalIlluminationFlags.EmissiveIsBlack) |
                                                   AllEmissionFlags;
                material.EnableKeyword(EmissionKeyword);
                MaterialGlobalIlluminationFlags readback = material.globalIlluminationFlags;
                if (firstUpgrade == null)
                {
                    firstUpgrade = $"{material.name} ({material.shader?.name ?? "missing shader"}) {flags} -> {readback}, keyword={material.IsKeywordEnabled(EmissionKeyword)}";
                }
                upgraded++;
            }
        }

        if (visited.Count > 0)
        {
            BasisDebug.Log(
                $"AssetBundle emission compatibility scan (Unity {builtWithUnityVersion ?? "unknown"}): " +
                $"renderers={renderers.Count}, materials={visited.Count}, legacyCandidates={legacyCandidates}, upgraded={upgraded}" +
                (firstUpgrade == null ? "." : $". First: {firstUpgrade}"),
                BasisDebug.LogTag.Event);
        }
        return upgraded;
    }

    public static bool RequiresLegacyEmissionUpgrade(string unityVersion)
    {
        // Bundles built before Basis started retaining the parsed header version are also legacy.
        if (string.IsNullOrWhiteSpace(unityVersion))
            return true;

        string[] parts = unityVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            return true;

        return major < DirectEmissionIntroducedUnityMajor ||
               (major == DirectEmissionIntroducedUnityMajor && minor < DirectEmissionIntroducedUnityMinor);
    }
}
