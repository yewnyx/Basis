using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelRichTextTests
    {
        [Test]
        public void EscapesAmpersandFirst()
        {
            Assert.That(BasisModelRichText.Escape("<b>&lt;</b>"), Is.EqualTo("&lt;b&gt;&amp;lt;&lt;/b&gt;"));
            Assert.That(BasisModelRichText.Escape("plain name"), Is.EqualTo("plain name"));
        }

        [Test]
        public void NullBecomesEmpty()
        {
            Assert.That(BasisModelRichText.Escape(null), Is.EqualTo(string.Empty));
            Assert.That(BasisModelRichText.Escape(string.Empty), Is.EqualTo(string.Empty));
        }

        [Test]
        public void FileNameIsTheLastPathSegment()
        {
            Assert.That(BasisModelRichText.FileNameForDisplay("C:/Users/someone/Pictures/cat.png"), Is.EqualTo("cat.png"));
            Assert.That(BasisModelRichText.FileNameForDisplay("model.glb"), Is.EqualTo("model.glb"));
            Assert.That(BasisModelRichText.FileNameForDisplay(null), Is.Null);
        }

        [Test]
        public void FileNameFallsBackToThePathOnInvalidCharacters()
        {
            // Modern .NET never throws here and returns the last segment; Unity's Mono may reject the
            // character and throw, in which case the whole path is shown. Either way no exception escapes.
            const string path = "dir/a|b\"c.png";
            Assert.That(
                BasisModelRichText.FileNameForDisplay(path),
                Is.EqualTo("a|b\"c.png").Or.EqualTo(path)
            );
        }
    }
}
