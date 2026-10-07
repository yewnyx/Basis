using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Resamples a decoded image into 8-bit RGBA, one target row per index: bilinear, sampling pixel centres, as
    /// the image pickup resizes its own oversized images; each channel is lerped exactly as <c>Color32.Lerp</c>
    /// does. Also widens RGB24 to RGBA (alpha 255) when the size is unchanged, where every sample lands on a
    /// pixel. Top level and non-generic so Burst and IL2CPP compile it without a <c>RegisterGenericJobType</c>.
    /// </summary>
    [BurstCompile]
    public struct BasisModelImageResampleJob : IJobParallelFor
    {
        /// <summary>Tightly packed rows of <see cref="SourceBytesPerPixel"/> bytes per pixel, R, G, B and optionally A.</summary>
        [ReadOnly]
        public NativeArray<byte> Source;

        public int SourceWidth;
        public int SourceHeight;

        /// <summary>3 (RGB, alpha read as 255) or 4 (RGBA).</summary>
        public int SourceBytesPerPixel;

        /// <summary>Target width; the job runs once per target row.</summary>
        public int Width;

        /// <summary>Source pixels per target pixel on each axis.</summary>
        public float ScaleX;
        public float ScaleY;

        /// <summary>RGBA rows, four bytes a pixel. Each index writes only its own row.</summary>
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<byte> Target;

        public void Execute(int y)
        {
            float v = (y + 0.5f) * ScaleY - 0.5f;
            int y0 = math.clamp((int)math.floor(v), 0, SourceHeight - 1);
            int y1 = math.min(y0 + 1, SourceHeight - 1);
            float fy = math.saturate(v - y0);
            int row0 = y0 * SourceWidth;
            int row1 = y1 * SourceWidth;
            int targetRow = y * Width * 4;
            for (int x = 0; x < Width; x++)
            {
                float u = (x + 0.5f) * ScaleX - 0.5f;
                int x0 = math.clamp((int)math.floor(u), 0, SourceWidth - 1);
                int x1 = math.min(x0 + 1, SourceWidth - 1);
                float fx = math.saturate(u - x0);
                int target = targetRow + x * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    byte top = Lerp(Read(row0 + x0, channel), Read(row0 + x1, channel), fx);
                    byte bottom = Lerp(Read(row1 + x0, channel), Read(row1 + x1, channel), fx);
                    Target[target + channel] = Lerp(top, bottom, fy);
                }
            }
        }

        private byte Read(int pixel, int channel)
        {
            return channel < SourceBytesPerPixel ? Source[pixel * SourceBytesPerPixel + channel] : (byte)255;
        }

        /// <summary><c>Color32.Lerp</c> for one channel, with <paramref name="t"/> already in [0, 1].</summary>
        private static byte Lerp(byte a, byte b, float t)
        {
            return (byte)(a + (b - a) * t);
        }
    }
}
