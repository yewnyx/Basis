using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Turns one embedded source image into a sanitised PNG. Awaited sequentially on the caller's context (the Unity
    /// main thread). Must return 8-bit PNG data within MaxTextureDimension and MaxImageBytes; Finish checks it anyway.
    /// </summary>
    public interface IBasisModelImageSanitizer
    {
        Task<BasisModelImageSanitizeResult> SanitizeAsync(byte[] sourceImage, BasisModelImageFormat format, CancellationToken cancellationToken);
    }

    public struct BasisModelImageSanitizeResult
    {
        public bool Ok;
        public string Error;
        public byte[] Png;
    }

    /// <summary>
    /// Async glue over the validator for tests and simple callers. The model runtime drives the same stages itself
    /// (polled, with liveness checks between them).
    /// </summary>
    public static class BasisGlbPipeline
    {
        /// <summary>
        /// Prepare on a worker, sanitise each image in canonical order on the caller's context, Finish on a worker. Call it
        /// on the Unity main thread; it never uses ConfigureAwait(false). Never throws.
        /// </summary>
        public static async Task<BasisGlbValidationResult> ValidateLocalAsync(byte[] source, BasisModelSourceFormat format,
            BasisModelLimits limits, IBasisModelImageSanitizer sanitizer, CancellationToken cancellationToken)
        {
            BasisGlbPrepareResult prepared = await Task.Run(() => BasisGlbValidator.Prepare(source, format, limits, cancellationToken));
            if (!prepared.Ok) return BasisGlbValidationResult.Fail(prepared.ErrorKind, prepared.Error);
            BasisGlbPreparedModel model = prepared.Model;
            for (int i = 0; i < model.ImageCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Cancelled, "Validation was cancelled.");
                }
                string path = BasisGlbErrors.Index("images", model.GetSourceImageIndex(i));
                BasisModelImageSanitizeResult sanitized;
                try
                {
                    if (sanitizer == null) return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "No image sanitiser was provided.");
                    sanitized = await sanitizer.SanitizeAsync(model.CopyImageSource(i), model.GetImageFormat(i), cancellationToken);
                }
                catch (Exception e)
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.ImageRejected, path + ": the image could not be processed (" + e.GetType().Name + ").");
                }
                if (!sanitized.Ok || sanitized.Png == null)
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.ImageRejected, path + ": " + (sanitized.Error ?? "the image was rejected."));
                }
                model.SetSanitizedImage(i, sanitized.Png);
            }
            return await Task.Run(() => BasisGlbValidator.Finish(model, cancellationToken));
        }

        public static Task<BasisGlbValidationResult> ValidateReceivedAsync(byte[] wireBytes, BasisModelLimits limits, CancellationToken cancellationToken)
        {
            return Task.Run(() => BasisGlbValidator.ValidateReceived(wireBytes, limits, cancellationToken));
        }
    }
}
