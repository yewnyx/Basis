using System;

namespace Basis.ModelPickup
{
    /// <summary>Grid spacing for a drag-and-drop batch.</summary>
    public readonly struct BasisModelBatchSpacing
    {
        /// <summary>Preferred columns before height pressure widens the grid.</summary>
        public readonly int Columns;

        public readonly int MaximumColumns;
        public readonly float HorizontalMeters;
        public readonly float VerticalMeters;

        public BasisModelBatchSpacing(int columns, int maximumColumns, float horizontalMeters, float verticalMeters)
        {
            Columns = columns;
            MaximumColumns = maximumColumns;
            HorizontalMeters = horizontalMeters;
            VerticalMeters = verticalMeters;
        }
    }

    /// <summary>
    /// Lays a multi-file drop out as a centred grid in the spawn pose's local plane, widening it rather than
    /// letting rows sink through the floor when the batch would otherwise be too tall.
    /// </summary>
    public static class BasisModelBatchLayout
    {
        /// <summary>Offset of item <paramref name="index"/> with the preferred column count and no floor.</summary>
        public static void LocalOffset(int index, int count, in BasisModelBatchSpacing spacing, out float x, out float y)
        {
            int columns = BasisModelMath.Min(spacing.Columns, count);
            LocalOffset(index, count, columns, float.NegativeInfinity, spacing, out x, out y);
        }

        /// <summary>
        /// Offset of item <paramref name="index"/> in a grid of <paramref name="columns"/>, rows centred on the
        /// origin and the whole grid shifted up so its lowest row sits at or above
        /// <paramref name="minimumLocalY"/> (negative infinity for no floor). The last row is centred on its own.
        /// </summary>
        public static void LocalOffset(
            int index,
            int count,
            int columns,
            float minimumLocalY,
            in BasisModelBatchSpacing spacing,
            out float x,
            out float y
        )
        {
            if (count <= 1)
            {
                if (index != 0)
                    throw new ArgumentOutOfRangeException(nameof(index));
                x = 0f;
                y = BasisModelMath.Max(0f, minimumLocalY);
                return;
            }
            if (index < 0 || index >= count)
                throw new ArgumentOutOfRangeException(nameof(index));

            columns = BasisModelMath.Clamp(columns, 1, count);
            int rows = (count + columns - 1) / columns;
            int row = index / columns;
            int column = index % columns;
            int itemsInRow = BasisModelMath.Min(columns, count - row * columns);

            x = (column - (itemsInRow - 1) * 0.5f) * spacing.HorizontalMeters;
            float centeredY = ((rows - 1) * 0.5f - row) * spacing.VerticalMeters;
            float lowestCenteredY = -(rows - 1) * 0.5f * spacing.VerticalMeters;
            float upwardShift = float.IsNegativeInfinity(minimumLocalY)
                ? 0f
                : BasisModelMath.Max(0f, minimumLocalY - lowestCenteredY);
            y = centeredY + upwardShift;
        }

        /// <summary>
        /// Columns needed so <paramref name="count"/> items centred at <paramref name="batchCenterY"/> fit above
        /// <paramref name="minimumCenterY"/>: never fewer than the preferred count, never more than the maximum
        /// or the item count.
        /// </summary>
        public static int Columns(int count, float batchCenterY, float minimumCenterY, in BasisModelBatchSpacing spacing)
        {
            if (count <= 1)
                return 1;

            float verticalSpacing = BasisModelMath.Max(0.01f, spacing.VerticalMeters);
            float availableDownward = BasisModelMath.Max(0f, batchCenterY - minimumCenterY);
            int rowsWithoutCrossingMinimum = BasisModelMath.Max(
                1,
                BasisModelMath.FloorToInt(availableDownward * 2f / verticalSpacing) + 1
            );
            int requiredColumns = BasisModelMath.CeilToInt(count / (float)rowsWithoutCrossingMinimum);
            int defaultColumns = BasisModelMath.Min(spacing.Columns, count);
            int maximumColumns = BasisModelMath.Min(spacing.MaximumColumns, count);
            return BasisModelMath.Clamp(BasisModelMath.Max(defaultColumns, requiredColumns), 1, maximumColumns);
        }
    }
}
