using NUnit.Framework;
using UnityEngine.Rendering;

namespace Basis.Tests.Rendering
{
    public class BasisUpscalerOptionTests
    {
        [Test]
        public void EveryTemporalOption_HasALabelAndAnUpscalerId()
        {
            Assert.That(SMModuleAntialiasingURP.TemporalUpscalerLabelKeys.Length, Is.EqualTo(SMModuleAntialiasingURP.TemporalUpscalerOptions.Length));
            foreach (string option in SMModuleAntialiasingURP.TemporalUpscalerOptions)
            {
                Assert.That(SMModuleAntialiasingURP.TemporalUpscalerId(option), Is.Not.Null, option);
                Assert.That(SMModuleAntialiasingURP.IsTemporalUpscalerOption(option.ToLowerInvariant()), Is.True, $"{option} has to survive the settings system lowercasing it.");
            }
        }

        [Test]
        public void MsaaAndSpatialOptions_AreNotTemporal()
        {
            foreach (string option in new[] { "Off", "MSAA 2X", "MSAA 4X", "MSAA 8X", "Linear", "Point", "FSR", "STP" })
            {
                Assert.That(SMModuleAntialiasingURP.IsTemporalUpscalerOption(option), Is.False, option);
            }
        }

        [Test]
        public void EveryQualityOption_HasALabel()
        {
            Assert.That(SMModuleAntialiasingURP.UpscalerQualityLabelKeys.Length, Is.EqualTo(SMModuleAntialiasingURP.UpscalerQualityOptions.Length));
            Assert.That(SMModuleAntialiasingURP.UpscalerQualityOptions, Does.Contain("Quality"));
        }

#if ENABLE_UPSCALER_FRAMEWORK && UNITY_EDITOR_WIN
        [Test]
        public void EveryTemporalOption_IsRegisteredWithUnity()
        {
            foreach (string option in SMModuleAntialiasingURP.TemporalUpscalerOptions)
            {
                string upscalerId = SMModuleAntialiasingURP.TemporalUpscalerId(option);
                Assert.That(UpscalerRegistry.s_RegisteredUpscalers.ContainsKey(upscalerId), Is.True, $"{option} maps to {upscalerId}, which Unity has not registered. com.unity.modules.amd and com.unity.modules.nvidia have to stay in Packages/manifest.json.");
            }
        }
#endif
    }
}
