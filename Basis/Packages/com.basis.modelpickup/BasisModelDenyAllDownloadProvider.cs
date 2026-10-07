using System;
using System.Threading.Tasks;
using GLTFast.Loading;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// glTFast's download provider for every model import: it refuses every request and never reads the URI.
    /// A backstop only; canonical GLBs carry no <c>uri</c> anywhere. Without it, glTFast's default provider would
    /// fetch any absolute http:// or file:// URI a peer put in a file, whatever base URI the load was given.
    /// </summary>
    public sealed class BasisModelDenyAllDownloadProvider : IDownloadProvider
    {
        public const string RefusedError = "External URIs are refused";

        public static BasisModelDenyAllDownloadProvider Instance = new BasisModelDenyAllDownloadProvider();

        // One completed, immutable refusal serves every request: Dispose is a no-op, so sharing it is safe.
        private static readonly Task<IDownload> RefusedDownload = Task.FromResult<IDownload>(Refused.Instance);
        private static readonly Task<ITextureDownload> RefusedTexture = Task.FromResult<ITextureDownload>(Refused.Instance);

        public Task<IDownload> Request(Uri url)
        {
            return RefusedDownload;
        }

        public Task<ITextureDownload> RequestTexture(Uri url, bool nonReadable)
        {
            return RefusedTexture;
        }

        public sealed class Refused : ITextureDownload
        {
            public static Refused Instance = new Refused();

            public bool Success => false;
            public string Error => RefusedError;
            public byte[] Data => null;
            public string Text => null;
            public bool? IsBinary => null;
            public Texture2D Texture => null;

            public void Dispose() { }
        }
    }
}
