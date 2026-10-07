using Basis.Scripts.BasisSdk.Interactions;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The grab interactable on a model pickup. The model's size lives on a child (the base scale), so the root's
    /// scale is the user's gesture scale alone: pinning the reference to 1 makes the 10–1000 % gesture range the
    /// absolute [0.1, 10] that remote transforms are clamped to, however many times the pickup was regrabbed.
    /// </summary>
    public class BasisModelPickupInteractable : BasisPickupInteractable
    {
        protected override float GestureScaleReference => 1f;

        /// <summary>
        /// Recollects the highlight once the placeholder has been swapped for the imported model. Only MeshRenderers
        /// are highlighted; a model with skinned meshes alone falls back to the trigger box's outline.
        /// </summary>
        public void RebuildHighlight()
        {
            HighlightObject(false);
            CalculateHighlightRenderers();
        }
    }
}
