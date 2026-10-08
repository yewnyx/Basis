using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelDialogChoiceTests
    {
        [Test]
        public void ValuesArePinned()
        {
            // The dialog maps the panel's bool onto Accept/Deny and every unanswered close onto Dismissed,
            // and the size dialog maps those onto Fit or Original, so the numbering is part of the contract.
            Assert.That((byte)BasisModelDialogChoice.Accept, Is.EqualTo(0));
            Assert.That((byte)BasisModelDialogChoice.Deny, Is.EqualTo(1));
            Assert.That((byte)BasisModelDialogChoice.Dismissed, Is.EqualTo(2));
        }
    }
}
