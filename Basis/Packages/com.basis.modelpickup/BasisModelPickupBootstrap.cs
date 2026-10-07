using Basis.Scripts.Platform;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Arms the model pickup at startup and subscribes it to the shared file-drop bridge. The bridge hands every
    /// subscriber the whole batch; <see cref="BasisModelPickupManager.SpawnFromFiles"/> keeps the .glb and .gltf
    /// paths and leaves the rest to the image pickup and anyone else listening.
    /// </summary>
    public static class BasisModelPickupBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            BasisModelPickupManager.Initialize();

            // The editor's Scene-view bridge only forwards extensions someone registered.
            BasisDesktopFileDrop.RegisterAcceptedExtensions(".glb", ".gltf");
            BasisDesktopFileDrop.OnFilesDropped -= OnFilesDropped;
            BasisDesktopFileDrop.OnFilesDropped += OnFilesDropped;
        }

        private static void OnFilesDropped(string[] paths)
        {
            BasisModelPickupManager.SpawnFromFiles(paths);
        }
    }
}
