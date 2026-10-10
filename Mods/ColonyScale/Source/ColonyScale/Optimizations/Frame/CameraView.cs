using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyScale.Optimizations.Frame
{
    /// <summary>
    /// The map area the main camera renders, read from the camera itself. CameraDriver.CurrentViewRect is not used:
    /// it caches on first read per frame, so reading it earlier than vanilla would change what vanilla sees.
    /// </summary>
    internal static class CameraView
    {
        /// <summary>
        /// True only for the usual map camera (orthographic, looking straight down, +z up on screen) when no gravship
        /// capture is rendering; then the out values are the world x/z extents it renders, expanded by
        /// <paramref name="margin"/>. A layer-0 mesh entirely outside that box produces no pixels: the main camera does
        /// not see it and the subcameras (children of the camera, same view) only render the WaterDepth and
        /// GravshipMask layers.
        /// </summary>
        public static bool TryGet(float margin, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
            if (WorldComponent_GravshipController.GravshipRenderInProgess) return false;
            Camera cam = Find.Camera;
            if (cam == null || !cam.orthographic) return false;
            Transform tr = cam.transform;
            if (tr.forward.y > -0.9999f || tr.up.z < 0.9999f) return false;
            float halfZ = cam.orthographicSize;
            float halfX = halfZ * cam.aspect;
            if (!(halfZ > 0f) || !(halfX > 0f) || float.IsInfinity(halfX)) return false;
            Vector3 pos = tr.position;
            minX = pos.x - halfX - margin;
            maxX = pos.x + halfX + margin;
            minZ = pos.z - halfZ - margin;
            maxZ = pos.z + halfZ + margin;
            return !(float.IsNaN(minX) || float.IsNaN(minZ) || float.IsInfinity(maxX) || float.IsInfinity(maxZ));
        }
    }
}
