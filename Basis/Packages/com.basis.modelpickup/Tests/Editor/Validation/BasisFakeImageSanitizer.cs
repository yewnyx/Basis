using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup.Tests.Validation
{
    /// <summary>Stands in for the runtime's main-thread sanitiser. Completes synchronously and records every call.</summary>
    internal sealed class BasisFakeImageSanitizer : IBasisModelImageSanitizer
    {
        internal enum Mode
        {
            /// <summary>PNG sources come back unchanged; JPEG sources become a generated PNG.</summary>
            PassThroughPng,
            Generate,
            Fail,
            Throw,
            NotPng,
            SixteenBit,
            HugeDimensions,
            OverEightMegabytes,
        }

        public Mode Behaviour = Mode.PassThroughPng;
        public int Width = 4, Height = 4;
        public readonly List<BasisModelImageFormat> Formats = new List<BasisModelImageFormat>();
        public readonly List<int> Lengths = new List<int>();
        public CancellationTokenSource CancelAfterCall;

        public int Calls => Formats.Count;

        public Task<BasisModelImageSanitizeResult> SanitizeAsync(byte[] sourceImage, BasisModelImageFormat format, CancellationToken cancellationToken)
        {
            Formats.Add(format);
            Lengths.Add(sourceImage.Length);
            CancelAfterCall?.Cancel();
            switch (Behaviour)
            {
                case Mode.PassThroughPng:
                    return Ok(format == BasisModelImageFormat.Png ? sourceImage : BasisGlbTestPng.Create(Width, Height));
                case Mode.Generate:
                    return Ok(BasisGlbTestPng.Create(Width, Height));
                case Mode.Fail:
                    return Task.FromResult(new BasisModelImageSanitizeResult { Ok = false, Error = "sanitiser refused it" });
                case Mode.Throw:
                    throw new InvalidOperationException("sanitiser exploded");
                case Mode.NotPng:
                    return Ok(BasisGlbTestPng.CreateJpegHeader(4, 4));
                case Mode.SixteenBit:
                    return Ok(BasisGlbTestPng.Create(2, 2, 6, 16));
                case Mode.HugeDimensions:
                    return Ok(WithDimensions(BasisGlbTestPng.Create(1, 1), 4096, 4096));
                default:
                    return Ok(OversizePng());
            }
        }

        private static Task<BasisModelImageSanitizeResult> Ok(byte[] png)
        {
            return Task.FromResult(new BasisModelImageSanitizeResult { Ok = true, Png = png });
        }

        /// <summary>Rewrites IHDR's dimensions (and its CRC) without touching IDAT: the validator rejects on IHDR alone.</summary>
        internal static byte[] WithDimensions(byte[] png, int width, int height)
        {
            List<BasisGlbTestPng.Chunk> chunks = BasisGlbTestPng.Split(png);
            byte[] ihdr = (byte[])chunks[0].Data.Clone();
            BasisGlbTestPng.WriteBigEndian(ihdr, 0, (uint)width);
            BasisGlbTestPng.WriteBigEndian(ihdr, 4, (uint)height);
            chunks[0] = new BasisGlbTestPng.Chunk("IHDR", ihdr);
            return BasisGlbTestPng.Join(chunks);
        }

        /// <summary>Structurally valid, 1×1, with an IDAT just over 8 MiB (never decoded by the validator).</summary>
        internal static byte[] OversizePng()
        {
            List<BasisGlbTestPng.Chunk> chunks = BasisGlbTestPng.Split(BasisGlbTestPng.Create(1, 1));
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i].Type == "IDAT") chunks[i] = new BasisGlbTestPng.Chunk("IDAT", new byte[8 * 1024 * 1024 + 16]);
            }
            return BasisGlbTestPng.Join(chunks);
        }
    }
}
