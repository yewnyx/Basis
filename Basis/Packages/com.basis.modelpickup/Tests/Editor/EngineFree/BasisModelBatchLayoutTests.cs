using System;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// Layout tests run with a wide spacing and with the model's own, so the arithmetic is pinned for any
    /// spacing rather than one set of numbers.
    /// </summary>
    public sealed class BasisModelBatchLayoutTests
    {
        private static BasisModelBatchSpacing Spacing(int spacingCase)
        {
            return spacingCase == 0
                ? new BasisModelBatchSpacing(4, 16, 1.0f, 0.65f)
                : new BasisModelBatchSpacing(4, 8, 0.8f, 0.8f);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void LargeBatchSpreadsHorizontallyAndStaysAboveMinimumHeight(int spacingCase)
        {
            BasisModelBatchSpacing spacing = Spacing(spacingCase);
            const int count = 64;
            const float batchCenterY = 1.6f;
            const float minimumCenterY = 0.3f;
            int columns = BasisModelBatchLayout.Columns(count, batchCenterY, minimumCenterY, spacing);

            Assert.That(columns, Is.GreaterThan(spacing.Columns));
            Assert.That(columns, Is.LessThanOrEqualTo(spacing.MaximumColumns));
            for (int index = 0; index < count; index++)
            {
                BasisModelBatchLayout.LocalOffset(index, count, columns, minimumCenterY - batchCenterY, spacing, out _, out float y);
                Assert.That(batchCenterY + y, Is.GreaterThanOrEqualTo(minimumCenterY - 0.0001f));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void LowBatchCenterUsesMaximumWidthAndShiftsAboveGround(int spacingCase)
        {
            BasisModelBatchSpacing spacing = Spacing(spacingCase);
            const int count = 64;
            const float batchCenterY = 0.7f;
            const float minimumCenterY = 0.3f;
            int columns = BasisModelBatchLayout.Columns(count, batchCenterY, minimumCenterY, spacing);

            Assert.That(columns, Is.EqualTo(spacing.MaximumColumns));
            for (int index = 0; index < count; index++)
            {
                BasisModelBatchLayout.LocalOffset(index, count, columns, minimumCenterY - batchCenterY, spacing, out _, out float y);
                Assert.That(batchCenterY + y, Is.GreaterThanOrEqualTo(minimumCenterY - 0.0001f));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void BatchOffsetsPlaceTwoItemsSideBySide(int spacingCase)
        {
            BasisModelBatchSpacing spacing = Spacing(spacingCase);
            BasisModelBatchLayout.LocalOffset(0, 2, spacing, out float leftX, out float leftY);
            BasisModelBatchLayout.LocalOffset(1, 2, spacing, out float rightX, out float rightY);

            Assert.That(leftX, Is.EqualTo(-rightX).Within(0.0001f));
            Assert.That(leftY, Is.EqualTo(rightY).Within(0.0001f));
            Assert.That(leftX, Is.LessThan(0f));
            Assert.That(rightX, Is.GreaterThan(0f));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void BatchOffsetsUseStableRowsForFiveItems(int spacingCase)
        {
            BasisModelBatchSpacing spacing = Spacing(spacingCase);
            BasisModelBatchLayout.LocalOffset(0, 5, spacing, out float firstX, out float firstY);
            BasisModelBatchLayout.LocalOffset(3, 5, spacing, out float fourthX, out _);
            BasisModelBatchLayout.LocalOffset(4, 5, spacing, out float fifthX, out float fifthY);

            Assert.That(firstY, Is.GreaterThan(fifthY));
            Assert.That(firstX, Is.LessThan(fourthX));
            Assert.That(fifthX, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void WiderSpacingScalesOffsetsLinearly()
        {
            var narrow = new BasisModelBatchSpacing(4, 16, 1.0f, 0.65f);
            var wide = new BasisModelBatchSpacing(4, 16, 2.0f, 1.3f);
            for (int index = 0; index < 11; index++)
            {
                BasisModelBatchLayout.LocalOffset(index, 11, narrow, out float narrowX, out float narrowY);
                BasisModelBatchLayout.LocalOffset(index, 11, wide, out float wideX, out float wideY);
                Assert.That(wideX, Is.EqualTo(narrowX * 2f).Within(0.00001f));
                Assert.That(wideY, Is.EqualTo(narrowY * 2f).Within(0.00001f));
            }
        }

        [Test]
        public void ASingleItemRefusesANonZeroIndex()
        {
            BasisModelBatchSpacing spacing = Spacing(0);

            BasisModelBatchLayout.LocalOffset(0, 1, spacing, out float x, out float y);
            Assert.That(x, Is.EqualTo(0f));
            Assert.That(y, Is.EqualTo(0f));
            BasisModelBatchLayout.LocalOffset(0, 1, 1, 0.4f, spacing, out _, out y);
            Assert.That(y, Is.EqualTo(0.4f), "a single item still respects the floor");

            Assert.Throws<ArgumentOutOfRangeException>(() => BasisModelBatchLayout.LocalOffset(1, 1, spacing, out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => BasisModelBatchLayout.LocalOffset(-1, 3, spacing, out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => BasisModelBatchLayout.LocalOffset(3, 3, spacing, out _, out _));
        }

        [Test]
        public void ColumnsNeverExceedTheMaximumOrTheCount()
        {
            for (int spacingCase = 0; spacingCase < 2; spacingCase++)
            {
                BasisModelBatchSpacing spacing = Spacing(spacingCase);
                for (int count = 0; count <= 100; count++)
                {
                    for (float centerY = -1f; centerY <= 3f; centerY += 0.25f)
                    {
                        int columns = BasisModelBatchLayout.Columns(count, centerY, 0.3f, spacing);
                        Assert.That(columns, Is.GreaterThanOrEqualTo(1));
                        Assert.That(columns, Is.LessThanOrEqualTo(Math.Max(1, Math.Min(spacing.MaximumColumns, count))));
                    }
                }
            }
        }
    }
}
