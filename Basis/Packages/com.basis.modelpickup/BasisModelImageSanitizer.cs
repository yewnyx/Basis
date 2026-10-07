using System;
using System.Threading;
using System.Threading.Tasks;
using Basis.ModelPickup.Validation;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The validator's image hook on the sender: each embedded PNG or JPEG is decoded and re-encoded into an
    /// 8-bit RGBA PNG of at most <see cref="BasisModelLimits.MaxTextureDimension"/> a side and
    /// <see cref="BasisModelLimits.MaxImageBytes"/>, so nothing the source file carried beyond its pixels
    /// survives. The signature, byte count and header dimensions are checked before anything decodes
    /// (<see cref="BasisModelImageHeader"/>); a GIF or anything else is refused whatever its mime type says.
    ///
    /// Start it on the main thread: <c>Texture2D.LoadImage</c> runs there, synchronously, before the first
    /// await, and the texture is destroyed before the method yields. Its pixels are copied into a native buffer
    /// (no managed pixel array for RGBA32 or RGB24 decodes); resizing, and widening RGB to RGBA, run as a Burst job
    /// polled once a frame; encoding runs on a worker. Every native buffer is freed before the method returns, on
    /// every path. Never throws; every failure comes back as an English reason.
    /// </summary>
    public sealed class BasisModelImageSanitizer : IBasisModelImageSanitizer
    {
        public static BasisModelImageSanitizer Instance = new BasisModelImageSanitizer();

        private const int ResampleRowsPerBatch = 16;
        private const string CancelledReason = "Image preparation was cancelled.";

        public async Task<BasisModelImageSanitizeResult> SanitizeAsync(
            byte[] sourceImage,
            BasisModelImageFormat format,
            CancellationToken cancellationToken
        )
        {
            NativeArray<byte> source = default;
            NativeArray<byte> resampled = default;
            NativeArray<byte> encoded = default;
            JobHandle resample = default;
            bool resampleRunning = false;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return Fail(CancelledReason);
                if (sourceImage == null || sourceImage.Length == 0)
                    return Fail("The image is empty.");
                if (format != BasisModelImageFormat.Png && format != BasisModelImageFormat.Jpeg)
                    return Fail(BasisModelImageHeader.PngOrJpegOnly);

                BasisModelLimits limits = BasisModelLimits.Desktop;
                if (!BasisModelImageHeader.TryCheckSource(sourceImage, limits, out _, out int width, out int height,
                        out string error))
                    return Fail(error);
                if (!TryDecode(sourceImage, width, height, out source, out int bytesPerPixel, out error))
                    return Fail(error);

                BasisModelImageHeader.FitWithin(width, height, limits.MaxTextureDimension, out int targetWidth, out int targetHeight);
                NativeArray<byte> rgba = source;
                if (targetWidth != width || targetHeight != height || bytesPerPixel != 4)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return Fail(CancelledReason);
                    resampled = new NativeArray<byte>(
                        targetWidth * targetHeight * 4,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    resample = new BasisModelImageResampleJob
                    {
                        Source = source,
                        SourceWidth = width,
                        SourceHeight = height,
                        SourceBytesPerPixel = bytesPerPixel,
                        Width = targetWidth,
                        ScaleX = (float)width / targetWidth,
                        ScaleY = (float)height / targetHeight,
                        Target = resampled,
                    }.Schedule(targetHeight, ResampleRowsPerBatch);
                    resampleRunning = true;
                    JobHandle.ScheduleBatchedJobs();
                    // Polled, never waited on: the frame goes on while the workers resample.
                    while (!resample.IsCompleted)
                        await Task.Yield();
                    resample.Complete();
                    resampleRunning = false;
                    rgba = resampled;
                }

                long maxImageBytes = limits.MaxImageBytes;
                NativeArray<byte> pixels = rgba;
                (byte[] png, NativeArray<byte> encodedOnWorker, string encodeError) = await Task.Run(
                    () => Encode(pixels, targetWidth, targetHeight, maxImageBytes, cancellationToken)
                );
                // Freed below, on the main thread, with the other buffers.
                encoded = encodedOnWorker;
                if (png == null)
                    return Fail(encodeError);
                return new BasisModelImageSanitizeResult { Ok = true, Png = png };
            }
            catch (Exception e)
            {
                return Fail("The image could not be processed (" + e.GetType().Name + ").");
            }
            finally
            {
                // The job reads source and writes resampled until it completes; nothing is freed before that.
                if (resampleRunning)
                    resample.Complete();
                if (encoded.IsCreated)
                    encoded.Dispose();
                if (resampled.IsCreated)
                    resampled.Dispose();
                if (source.IsCreated)
                    source.Dispose();
            }
        }

        /// <summary>
        /// Main thread. The header was already checked, so the decode is bounded; a decoder that disagrees with
        /// the header about the size is refused. The texture is destroyed on every path. RGBA32 and RGB24 decodes
        /// (8-bit PNG and JPEG) are copied out as they are; any other format goes through Unity's own conversion to
        /// RGBA32. <paramref name="pixels"/> is the caller's to free whenever it was created, success or not.
        /// </summary>
        private static bool TryDecode(byte[] source, int width, int height, out NativeArray<byte> pixels,
            out int bytesPerPixel, out string error)
        {
            pixels = default;
            bytesPerPixel = 4;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                bool loaded;
                try
                {
                    loaded = texture.LoadImage(source, false);
                }
                catch (Exception e)
                {
                    error = "The image could not be decoded (" + e.GetType().Name + ").";
                    return false;
                }
                if (!loaded)
                {
                    error = "The image could not be decoded.";
                    return false;
                }
                if (texture.width != width || texture.height != height)
                {
                    error = "The image's header and pixels disagree about its size.";
                    return false;
                }

                TextureFormat decoded = texture.format;
                int pixelCount = width * height;
                if (decoded == TextureFormat.RGBA32 || decoded == TextureFormat.RGB24)
                {
                    int decodedBytesPerPixel = decoded == TextureFormat.RGBA32 ? 4 : 3;
                    NativeArray<byte> data = texture.GetPixelData<byte>(0);
                    if (data.Length == pixelCount * decodedBytesPerPixel)
                    {
                        bytesPerPixel = decodedBytesPerPixel;
                        pixels = new NativeArray<byte>(data, Allocator.Persistent);
                        error = null;
                        return true;
                    }
                }

                bytesPerPixel = 4;
                pixels = new NativeArray<byte>(pixelCount * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                pixels.Reinterpret<Color32>(1).CopyFrom(texture.GetPixels32());
                error = null;
                return true;
            }
            finally
            {
                BasisModelGltfLoader.DestroyUnityObject(texture);
            }
        }

        /// <summary>
        /// Worker thread: Unity's thread-safe native encoder only. The encoder's own buffer comes back to be freed
        /// on the main thread whatever the outcome.
        /// </summary>
        private static (byte[] Png, NativeArray<byte> Encoded, string Error) Encode(NativeArray<byte> rgba, int width,
            int height, long maxImageBytes, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return (null, default, CancelledReason);

            NativeArray<byte> encoded = ImageConversion.EncodeNativeArrayToPNG(
                rgba,
                GraphicsFormat.R8G8B8A8_SRGB,
                (uint)width,
                (uint)height,
                0
            );
            try
            {
                if (!encoded.IsCreated || encoded.Length == 0)
                    return (null, encoded, "The image re-encode produced no data.");
                if (encoded.Length > maxImageBytes)
                    return (null, encoded, BasisGlbErrors.Bytes("Re-encoded image", encoded.Length, maxImageBytes));
                return (encoded.ToArray(), encoded, null);
            }
            catch
            {
                // The buffer never reaches the main thread on this path, so it is freed here.
                if (encoded.IsCreated)
                    encoded.Dispose();
                throw;
            }
        }

        private static BasisModelImageSanitizeResult Fail(string error)
        {
            return new BasisModelImageSanitizeResult { Ok = false, Error = error };
        }
    }
}
