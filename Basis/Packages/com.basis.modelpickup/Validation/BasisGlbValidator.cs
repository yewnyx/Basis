using System;
using System.IO;
using System.Threading;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// The only code that reads untrusted model bytes before glTFast does. Outputs a canonical GLB, its stats and its
    /// bounds. The sender calls <see cref="Prepare"/>, sanitises each image on the main thread, then calls
    /// <see cref="Finish"/>; a receiver calls <see cref="ValidateReceived"/> and imports only <c>CleanGlb</c>.
    /// Every entry point is thread-agnostic, stateless and never throws: any exception is reported as
    /// <see cref="BasisGlbErrorKind.Internal"/>, and the fuzz tests assert that input never gets there.
    /// </summary>
    public static class BasisGlbValidator
    {
        public const byte CanonicalFormatVersion = BasisGlbClaims.CurrentFormatVersion;
        public static readonly string[] SupportedExtensions = { ".glb", ".gltf" };

        /// <summary>
        /// The sender self-check always compares stats and stripped flags. Byte equality of a re-canonicalisation is
        /// also required in the editor, development players and dotnet tests; release players skip it so a float
        /// round-trip quirk in a player BCL cannot disable all sharing (receivers re-validate regardless).
        /// </summary>
        public static bool RequireByteIdenticalSelfCheck =
#if UNITY_EDITOR || DEVELOPMENT_BUILD || !UNITY_5_3_OR_NEWER
            true;
#else
            false;
#endif

        public static bool HasSupportedModelExtension(string path)
        {
            return TryGetExtension(path, out string extension)
                && (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryGetExtension(string path, out string extension)
        {
            extension = null;
            if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) return false;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
            return !string.IsNullOrEmpty(extension);
        }

        /// <summary>
        /// With a path, the extension must be .glb or .gltf (case-insensitive) and the head must agree with it. Without
        /// one, the head is sniffed: GLB magic, or '{' as the first non-whitespace byte after an optional BOM.
        /// </summary>
        public static bool TryDetectFormat(string pathOrNull, ReadOnlySpan<byte> head, out BasisModelSourceFormat format, out string error)
        {
            format = BasisModelSourceFormat.Unknown;
            error = null;
            bool glb = BasisGlbContainer.HasGlbMagic(head);
            bool json = !glb && BasisGlbContainer.LooksLikeGltfJson(head);
            if (pathOrNull == null)
            {
                if (glb) format = BasisModelSourceFormat.Glb;
                else if (json) format = BasisModelSourceFormat.GltfJson;
                else error = "Unsupported model data; only GLB and glTF JSON are supported.";
                return format != BasisModelSourceFormat.Unknown;
            }
            if (!TryGetExtension(pathOrNull, out string extension))
            {
                error = "Unsupported model type; use .glb or .gltf";
                return false;
            }
            if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
            {
                if (!glb)
                {
                    error = "The file extension says .glb but the data is not a GLB.";
                    return false;
                }
                format = BasisModelSourceFormat.Glb;
                return true;
            }
            if (string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase))
            {
                if (!json)
                {
                    error = "The file extension says .gltf but the data is not glTF JSON.";
                    return false;
                }
                format = BasisModelSourceFormat.GltfJson;
                return true;
            }
            error = "Unsupported model type; use .glb or .gltf";
            return false;
        }

        /// <summary>
        /// Sender phase A (worker): every check up to the images (<see cref="TryAnalyze"/>). Holds references to <paramref name="source"/>; do not mutate it until
        /// <see cref="Finish"/> returns. Never throws.
        /// </summary>
        public static BasisGlbPrepareResult Prepare(byte[] source, BasisModelSourceFormat format, in BasisModelLimits limits,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!limits.TryValidate(out string limitError))
                {
                    return PrepareFail(BasisGlbErrorKind.Internal, limitError);
                }
                if (source == null || source.Length == 0) return PrepareFail(BasisGlbErrorKind.Malformed, "No data.");
                if (source.Length > limits.MaxSourceBytes)
                {
                    return PrepareFail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes("Model file", source.Length, limits.MaxSourceBytes));
                }
                if (format == BasisModelSourceFormat.Unknown && !TryDetectFormat(null, source, out format, out string detectError))
                {
                    return PrepareFail(BasisGlbErrorKind.Unsupported, detectError);
                }
                var work = new BasisGltfWork(source, format, limits, true, cancellationToken);
                if (!TryAnalyze(work)) return PrepareFail(work.ErrorKind, work.Error);
                return new BasisGlbPrepareResult { Ok = true, Model = new BasisGlbPreparedModel(work) };
            }
            catch (Exception e)
            {
                return PrepareFail(BasisGlbErrorKind.Internal, "Internal validation error (" + e.GetType().Name + ").");
            }
        }

        /// <summary>
        /// Sender phase C (worker), after every image has a sanitised PNG: layout, canonical JSON, stats and GLB, then a self-check that runs
        /// the receiver path on the output. Single use per prepared model. Never throws.
        /// </summary>
        public static BasisGlbValidationResult Finish(BasisGlbPreparedModel prepared, CancellationToken cancellationToken = default)
        {
            try
            {
                if (prepared == null) return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "No prepared model.");
                if (!prepared.TryBeginFinish())
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "This prepared model was already finished.");
                }
                BasisGltfWork work = prepared.Work;
                work.Cancellation = cancellationToken;
                if (cancellationToken.IsCancellationRequested) return Cancelled();
                BasisGltfPlan plan = work.Plan;
                for (int i = 0; i < plan.Images.Count; i++)
                {
                    BasisGltfCanonImage image = plan.Images[i];
                    byte[] png = prepared.GetSanitizedImage(i);
                    string path = BasisGlbErrors.Index("images", image.Source);
                    if (png == null)
                    {
                        return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, path + " has no sanitised PNG.");
                    }
                    if (!BasisGlbPng.TryCanonicalize(png, work.Limits, out image.Final, out image.Width, out image.Height, out bool overLimit, out string error))
                    {
                        return BasisGlbValidationResult.Fail(overLimit ? BasisGlbErrorKind.OverLimit : BasisGlbErrorKind.ImageRejected,
                            path + " after sanitising: " + error);
                    }
                }
                BasisGlbValidationResult result = Complete(work, null);
                if (!result.Ok) return result;

                BasisGlbValidationResult check = ValidateReceived(result.CleanGlb, work.Limits, cancellationToken);
                if (!check.Ok)
                {
                    return check.ErrorKind == BasisGlbErrorKind.Cancelled
                        ? check
                        : BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "The canonical model failed its own validation: " + check.Error);
                }
                if (check.Stripped != BasisGlbStripped.None)
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "The canonical model still contains content that should have been stripped.");
                }
                if (!BasisGlbStats.AreEqual(check.Stats, result.Stats))
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "The canonical model's stats changed when it was validated again.");
                }
                if (RequireByteIdenticalSelfCheck && !check.InputWasCanonical)
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "The canonical model changed when it was canonicalised again.");
                }
                return result;
            }
            catch (Exception e)
            {
                return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "Internal validation error (" + e.GetType().Name + ").");
            }
        }

        /// <summary>
        /// Receiver (worker): GLB only, no uri anywhere, PNG images only; every check. When the input is already canonical,
        /// <c>CleanGlb</c> is <paramref name="wireBytes"/> itself, so callers must not mutate it afterwards. Never throws.
        /// </summary>
        public static BasisGlbValidationResult ValidateReceived(byte[] wireBytes, in BasisModelLimits limits,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!limits.TryValidate(out string limitError))
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, limitError);
                }
                if (wireBytes == null || wireBytes.Length == 0) return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Malformed, "No data.");
                if (wireBytes.Length > limits.MaxModelBytes)
                {
                    return BasisGlbValidationResult.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes("Model", wireBytes.Length, limits.MaxModelBytes));
                }
                var work = new BasisGltfWork(wireBytes, BasisModelSourceFormat.Glb, limits, false, cancellationToken);
                if (!TryAnalyze(work)) return BasisGlbValidationResult.Fail(work.ErrorKind, work.Error);
                BasisGltfPlan plan = work.Plan;
                for (int i = 0; i < plan.Images.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) return Cancelled();
                    BasisGltfCanonImage image = plan.Images[i];
                    var png = new ReadOnlySpan<byte>(image.SourceData, image.SourceOffset, image.SourceLength);
                    if (!BasisGlbPng.TryCanonicalize(png, limits, out image.Final, out image.Width, out image.Height, out bool overLimit, out string error))
                    {
                        return BasisGlbValidationResult.Fail(overLimit ? BasisGlbErrorKind.OverLimit : BasisGlbErrorKind.ImageRejected,
                            BasisGlbErrors.Index("images", image.Source) + ": " + error);
                    }
                }
                return Complete(work, wireBytes);
            }
            catch (Exception e)
            {
                return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Internal, "Internal validation error (" + e.GetType().Name + ").");
            }
        }

        /// <summary>Container, UTF-8, parse, header, graph, references, buffers, totals, accessor scans, bounds.</summary>
        public static bool TryAnalyze(BasisGltfWork work)
        {
            BasisModelLimits limits = work.Limits;
            byte[] source = work.Source;
            int jsonStart, jsonLength;
            bool glb = work.Format == BasisModelSourceFormat.Glb;
            if (glb)
            {
                if (!BasisGlbContainer.TryParse(source, limits.MaxJsonBytes, out work.Chunks, out bool overLimit, out string containerError))
                {
                    return work.Fail(overLimit ? BasisGlbErrorKind.OverLimit : BasisGlbErrorKind.Malformed, containerError);
                }
                jsonStart = work.Chunks.JsonStart;
                jsonLength = work.Chunks.JsonLength;
            }
            else
            {
                // .gltf only: one leading BOM is skipped. The whole text is bounded by MaxSourceBytes (it carries base64).
                jsonStart = BasisGlbContainer.HasUtf8Bom(source) ? 3 : 0;
                jsonLength = source.Length - jsonStart;
            }
            if (!work.CheckCancellation()) return false;
            if (!BasisJsonReader.ValidateUtf8(source, jsonStart, jsonLength, out int utf8Error))
            {
                return work.Fail(BasisGlbErrorKind.Malformed, "The glTF JSON is not valid UTF-8 (byte " + BasisGlbErrors.N(utf8Error) + ").");
            }
            if (!work.CheckCancellation()) return false;
            if (!BasisGltfJsonBinder.TryBind(source, jsonStart, jsonLength, glb, limits, work.Cancellation,
                    out work.Document, out BasisGlbErrorKind bindKind, out string bindError))
            {
                return work.Fail(bindKind, bindError);
            }
            work.Plan = new BasisGltfPlan();
            return BasisGltfSemanticValidator.TryCheckHeader(work)
                && work.CheckCancellation()
                && BasisGltfSemanticValidator.TryBuildGraph(work)
                && work.CheckCancellation()
                && BasisGltfSemanticValidator.TryBuildReferences(work)
                && work.CheckCancellation()
                && BasisGltfSemanticValidator.TryResolveBuffers(work)
                && BasisGltfCanonicalizer.TryComputeTotals(work)
                && work.CheckCancellation()
                && BasisGltfAccessorScanner.TryScan(work)
                && work.CheckCancellation()
                && BasisGltfTransformMath.TryComputeBounds(work);
        }

        /// <summary>Layout, canonical JSON, stats and GLB once every image has its final PNG. <paramref name="input"/> is the receiver's wire bytes.</summary>
        private static BasisGlbValidationResult Complete(BasisGltfWork work, byte[] input)
        {
            if (work.Cancellation.IsCancellationRequested) return Cancelled();
            if (!BasisGltfCanonicalizer.TryLayout(work)) return BasisGlbValidationResult.Fail(work.ErrorKind, work.Error);
            if (!BasisGltfCanonicalWriter.TryBuildJson(work, out byte[] json, out int jsonLength))
            {
                return BasisGlbValidationResult.Fail(work.ErrorKind, work.Error);
            }
            BasisGltfPlan plan = work.Plan;
            long total = BasisGltfCanonicalWriter.GlbLength(jsonLength, plan.BinLength);
            if (total > work.Limits.MaxModelBytes)
            {
                return BasisGlbValidationResult.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes("Canonical model", total, work.Limits.MaxModelBytes));
            }
            int jsonChunk = (int)BasisGltfCanonicalizer.Align4(jsonLength);
            int binChunk = (int)BasisGltfCanonicalizer.Align4(plan.BinLength);
            if (!BasisGlbStatsCalculator.TryCompute(work, (int)total, jsonChunk, binChunk, out BasisGlbStats stats))
            {
                return BasisGlbValidationResult.Fail(work.ErrorKind, work.Error);
            }
            if (work.Cancellation.IsCancellationRequested) return Cancelled();
            byte[] glb = BasisGltfCanonicalWriter.WriteGlb(plan, json, jsonLength);
            bool canonical = input != null && input.Length == glb.Length && new ReadOnlySpan<byte>(input).SequenceEqual(glb);
            return new BasisGlbValidationResult
            {
                Ok = true,
                CleanGlb = canonical ? input : glb,
                Stats = stats,
                Stripped = work.Document.Stripped,
                InputWasCanonical = canonical,
            };
        }

        private static BasisGlbPrepareResult PrepareFail(BasisGlbErrorKind kind, string error)
        {
            return new BasisGlbPrepareResult { Ok = false, ErrorKind = kind, Error = error };
        }

        private static BasisGlbValidationResult Cancelled()
        {
            return BasisGlbValidationResult.Fail(BasisGlbErrorKind.Cancelled, "Validation was cancelled.");
        }
    }

    /// <summary>Per-call state shared by the stages. Never shared between calls, so concurrent validations are safe.</summary>
    public sealed class BasisGltfWork
    {
        public readonly byte[] Source;
        public readonly BasisModelSourceFormat Format;
        public readonly BasisModelLimits Limits;
        public readonly bool Sender;
        public CancellationToken Cancellation;
        public BasisGlbChunks Chunks;
        public BasisGltfDocument Document;
        public BasisGltfPlan Plan;
        public byte[][] BufferData;
        public long[] BufferBase;
        public long DecodedUriBytes;
        public int DataUris;
        public BasisGlbErrorKind ErrorKind;
        public string Error;

        public BasisGltfWork(byte[] source, BasisModelSourceFormat format, in BasisModelLimits limits, bool sender, CancellationToken cancellation)
        {
            Source = source;
            Format = format;
            Limits = limits;
            Sender = sender;
            Cancellation = cancellation;
        }

        public bool Fail(BasisGlbErrorKind kind, string message)
        {
            if (Error == null)
            {
                ErrorKind = kind;
                Error = message;
            }
            return false;
        }

        public bool Cancelled()
        {
            return Fail(BasisGlbErrorKind.Cancelled, "Validation was cancelled.");
        }

        public bool CheckCancellation()
        {
            return !Cancellation.IsCancellationRequested || Cancelled();
        }
    }
}
