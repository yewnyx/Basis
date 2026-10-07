using System;
using System.Threading;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Sender state between <see cref="BasisGlbValidator.Prepare"/> and <see cref="BasisGlbValidator.Finish"/>: the parsed
    /// and checked model plus its retained images in canonical order, each waiting for a sanitised PNG.
    /// </summary>
    public sealed class BasisGlbPreparedModel
    {
        public readonly BasisGltfWork Work;
        private readonly byte[][] _sanitized;
        private int _finished;

        public BasisGlbPreparedModel(BasisGltfWork work)
        {
            Work = work;
            _sanitized = new byte[work.Plan.Images.Count][];
        }

        /// <summary>Retained images, in canonical order.</summary>
        public int ImageCount => _sanitized.Length;

        public BasisGlbStripped Stripped => Work.Document.Stripped;

        /// <summary>Final bounds (glTF space, before base scale). Known once the bounds pass has run; images never change them.</summary>
        public BasisGlbAabb Bounds => Work.Plan.Bounds;

        public BasisModelImageFormat GetImageFormat(int index)
        {
            return Image(index).Format;
        }

        /// <summary>The original images[] index, for messages.</summary>
        public int GetSourceImageIndex(int index)
        {
            return Image(index).Source;
        }

        /// <summary>A fresh copy of the source image bytes (at most MaxSourceImageBytes).</summary>
        public byte[] CopyImageSource(int index)
        {
            BasisGltfCanonImage image = Image(index);
            var copy = new byte[image.SourceLength];
            Buffer.BlockCopy(image.SourceData, image.SourceOffset, copy, 0, image.SourceLength);
            return copy;
        }

        /// <summary>Stores the sanitiser's PNG; it is checked (and stripped) in Finish.</summary>
        public void SetSanitizedImage(int index, byte[] png)
        {
            Image(index);
            _sanitized[index] = png;
        }

        public byte[] GetSanitizedImage(int index)
        {
            return _sanitized[index];
        }

        public bool TryBeginFinish()
        {
            return Interlocked.Exchange(ref _finished, 1) == 0;
        }

        private BasisGltfCanonImage Image(int index)
        {
            if ((uint)index >= (uint)_sanitized.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return Work.Plan.Images[index];
        }
    }
}
