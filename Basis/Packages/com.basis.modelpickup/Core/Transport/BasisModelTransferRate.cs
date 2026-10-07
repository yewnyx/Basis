namespace Basis.ModelPickup
{
    /// <summary>
    /// Smoothed throughput for one transfer, sampled on an interval rather than per chunk so the readout
    /// does not swing with whichever frame a chunk happens to land on. Callers add to
    /// <see cref="MovedBytes"/> as bytes move and call <see cref="Sample"/> once per tick.
    /// </summary>
    public struct BasisModelTransferRate
    {
        public long MovedBytes;
        public long SampleBytes;
        public float SampleTime;
        public float BytesPerSecond;

        /// <summary>
        /// The first call only opens the window. Afterwards the rate is resampled every
        /// <see cref="BasisModelShareSettings.TransferRateSampleSeconds"/>, easing toward the instant value.
        /// </summary>
        public void Sample(float now)
        {
            if (SampleTime <= 0f)
            {
                SampleTime = now;
                SampleBytes = MovedBytes;
                return;
            }

            float elapsed = now - SampleTime;
            if (elapsed < BasisModelShareSettings.TransferRateSampleSeconds)
                return;

            float instant = (MovedBytes - SampleBytes) / elapsed;
            BytesPerSecond =
                BytesPerSecond <= 0f
                    ? instant
                    : BasisModelMath.Lerp(
                        BytesPerSecond,
                        instant,
                        BasisModelShareSettings.TransferRateSmoothing
                    );
            SampleTime = now;
            SampleBytes = MovedBytes;
        }

        public float Fraction(long totalBytes)
        {
            return totalBytes > 0 ? BasisModelMath.Clamp01((float)MovedBytes / totalBytes) : 0f;
        }
    }
}
