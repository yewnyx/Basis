namespace Basis.ModelPickup
{
    /// <summary>
    /// How a two-button model prompt ended. <see cref="Dismissed"/> covers every way it can close without an
    /// answer (menu closed, prompt released, cancelled), so a caller maps it to its safe default explicitly.
    /// </summary>
    public enum BasisModelDialogChoice : byte
    {
        Accept = 0,
        Deny = 1,
        Dismissed = 2,
    }
}
