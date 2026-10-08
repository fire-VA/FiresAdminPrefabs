using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.164 (Fire 10-05: "we should create imposters for them"): the bundle prefabs that ship with no LODGroup of their own
    // (Balrond's Poplar, Willow, Cypress, sapling_Poplar ...; 1.1.163 gave them a cull LODGroup) get a baked IMPOSTOR as a second
    // LOD level: the prefab is rendered once at load into a cutout texture (front view, the same technique as FAT's
    // FarVegImpostors), drawn on crossed quads (16 tris). The LODGroup then reads: the full mesh up to BundleImpostorDistance,
    // the impostor up to BundleImpostorCullDistance, culled past it. The impostor is a child of the prefab, so every placed copy has it.
    internal static class VABundleImpostors
    {
        private const int BakeRes = 256, BakeLayer = 30, PerFrame = 2, Views = 8, AtlasCols = 4, AtlasRows = 2;
        private const float BakeLightIntensity = 0.75f, BakeAmbient = 0.35f, Cutoff = 0.4f;
        private const string ChildName = "FAP_Impostor", LightName = "FAP_ImpostorBakeLight";

        private static readonly List<GameObject> s_pending = new List<GameObject>();
        internal static int Baked, Failed, Skipped;
        private const float MinImpostorHeight = 4f;   // 1.1.166: metres; below this a prefab keeps the cull-only LODGroup

        internal static void Queue(GameObject prefab) { if (prefab != null && !s_pending.Contains(prefab)) s_pending.Add(prefab); }

        internal static IEnumerator BakeAll()
        {
            if (s_pending.Count == 0 || !(ConfigManager.Instance?.configBundleImpostors?.Value ?? true)) yield break;
            // A dedicated server (or any -nographics process) renders nothing: there is nothing to bake, and nobody to see it.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { s_pending.Clear(); yield break; }
            yield return null;
            Shader shader = ShaderReplacement.VegetationShader
                            ?? Shader.Find("Unlit/Transparent Cutout") ?? Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse");
            if (shader == null) { Debug.LogWarning($"{VaPieces.PluginName}: impostors: no cutout shader; the trees keep the cull-only LODGroup"); yield break; }

            var camGO = new GameObject("FAP_ImpostorBakeCam");
            var cam = camGO.AddComponent<Camera>();
            cam.enabled = false; cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f); cam.cullingMask = 1 << BakeLayer; cam.nearClipPlane = 0.1f;
            cam.allowHDR = false; cam.allowMSAA = false;
            var lightGO = new GameObject(LightName);
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = BakeLightIntensity; light.color = Color.white;
            light.cullingMask = 1 << BakeLayer; light.transform.rotation = Quaternion.Euler(40f, 205f, 0f);
            var rt = new RenderTexture(BakeRes, BakeRes, 24, RenderTextureFormat.ARGB32);
            try
            {
                var list = new List<GameObject>(s_pending);
                s_pending.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    var env = Environment.Capture(); env.Apply();
                    try { BakeOne(list[i], cam, rt, shader); }
                    catch (Exception e) { Failed++; Debug.LogWarning($"{VaPieces.PluginName}: impostor bake failed for '{list[i]?.name}': {e.Message}"); }
                    finally { env.Restore(); }
                    if ((i + 1) % PerFrame == 0) yield return null;
                }
            }
            finally
            {
                cam.targetTexture = null;
                UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(camGO); UnityEngine.Object.Destroy(lightGO);
            }
            VaPieces.Log?.LogDebug($"impostors — {Baked} tree-sized bundle prefab(s) given a baked impostor, {Skipped} under {MinImpostorHeight:0} m tall kept cull-only, "
                      + $"{Failed} failed ({Views} views of {BakeRes}px in one DXT5 atlas each, no CPU copy, 16 tris, shader {shader.name}); "
                      + $"full mesh to {ConfigManager.Instance.configBundleImpostorDistance.Value:0} m, impostor to {ConfigManager.Instance.configBundleImpostorCullDistance.Value:0} m.");
        }

        private static void BakeOne(GameObject prefab, Camera cam, RenderTexture rt, Shader shader)
        {
            if (prefab == null || prefab.transform.Find(ChildName) != null) return;
            var group = prefab.GetComponent<LODGroup>();
            if (group == null) return;
            var full = group.GetLODs();
            if (full.Length != 1) return;   // only the cull LODGroup FAP added

            GameObject root = null;
            Texture2D tex;
            float side, y0, cx, cz;
            try
            {
                // An inactive root: no Awake runs (no ZNetView registers); MonoBehaviours stripped before it renders.
                root = new GameObject("FAP_ImpostorBakeRoot");
                root.transform.position = new Vector3(0f, -6000f, 0f);
                root.SetActive(false);
                var inst = UnityEngine.Object.Instantiate(prefab, root.transform);
                inst.transform.localPosition = Vector3.zero; inst.transform.localRotation = Quaternion.identity;
                foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true)) try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
                foreach (var lg in inst.GetComponentsInChildren<LODGroup>(true)) lg.enabled = false;
                foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = BakeLayer;
                root.SetActive(true);

                Bounds b = default; bool got = false;
                foreach (var r in inst.GetComponentsInChildren<Renderer>())
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (!got) { b = r.bounds; got = true; } else b.Encapsulate(r.bounds);
                }
                if (!got) return;
                // 1.1.166: tree-sized only (Fire's ask was the trees). A smaller prop keeps its cull LODGroup and no impostor,
                // which is what held ~5 MiB each for 431 prefabs. Bounds-based, so any tree mod's trees qualify.
                if (b.size.y < MinImpostorHeight) { Skipped++; return; }
                // Fire (10-05): "bake them from multiple angles too". Views every 45 deg round the tree; the frame is wide
                // enough for its widest horizontal reach, so every view uses the same scale.
                float w = Mathf.Sqrt(b.size.x * b.size.x + b.size.z * b.size.z), half = Mathf.Max(w, b.size.y) * 0.5f * 1.05f;
                cam.orthographicSize = half;
                cam.farClipPlane = w * 4f + 50f;
                cam.targetTexture = rt;
                tex = new Texture2D(BakeRes * AtlasCols, BakeRes * AtlasRows, TextureFormat.RGBA32, true);
                for (int k = 0; k < Views; k++)
                {
                    Vector3 n = ViewNormal(k);
                    cam.transform.position = b.center + n * (w * 2f + 10f);
                    cam.transform.rotation = Quaternion.LookRotation(-n, Vector3.up);
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, BakeRes, BakeRes), (k % AtlasCols) * BakeRes, (k / AtlasCols) * BakeRes);
                    RenderTexture.active = null;
                }
                var px = tex.GetPixels32();
                Bleed(px, BakeRes * AtlasCols, BakeRes * AtlasRows);
                tex.SetPixels32(px);
                tex.Apply(true, false);
                // 1.1.166: DXT5 (4:1, alpha kept for the cutout) and no CPU copy (nothing reads these pixels after the bake),
                // so an atlas is ~0.67 MiB of texture memory instead of 2.67 MiB on the GPU plus 2.67 MiB in RAM.
                tex.Compress(true);
                tex.Apply(false, true);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = prefab.name + "_impostor";
                side = half * 2f;
                y0 = (b.center.y - inst.transform.position.y) - half;
                cx = b.center.x - inst.transform.position.x; cz = b.center.z - inst.transform.position.z;
            }
            finally { if (root != null) UnityEngine.Object.Destroy(root); }

            var mat = new Material(shader) { mainTexture = tex, name = prefab.name + "_impostor" };
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", Cutoff);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            var child = new GameObject(ChildName);
            child.transform.SetParent(prefab.transform, false);
            child.layer = prefab.layer;
            child.AddComponent<MeshFilter>().sharedMesh = StarPlanes(side, y0, cx, cz);
            var mr = child.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            float size = group.size > 0f ? group.size : side;
            float near = ConfigManager.Instance.configBundleImpostorDistance.Value, far = ConfigManager.Instance.configBundleImpostorCullDistance.Value;
            float h0 = VABakeLodPolicy.ScreenRelativeHeightAt(near, size), h1 = far > 0f ? VABakeLodPolicy.ScreenRelativeHeightAt(far, size) : 0f;
            if (h1 >= h0) h1 = h0 * 0.5f;
            group.SetLODs(new[] { new LOD(h0, full[0].renderers), new LOD(h1, new Renderer[] { mr }) });
            group.RecalculateBounds();
            Baked++;
            VaPieces.Log?.LogDebug($"[VAMaterialRepair] {prefab.name}: impostor baked ({Views} views, {side:0.0} m planes, 16 tris); full mesh to {near:0} m, impostor to {far:0} m");
        }

        // Colour pushed into the transparent surround so the mips don't fade the edges to black (FAT's BleedIntoSurround).
        private static void Bleed(Color32[] p, int W, int H)
        {
            var filled = new bool[p.Length];
            for (int i = 0; i < p.Length; i++) filled[i] = p[i].a >= 128;
            var spread = new List<int>();
            for (int pass = 0; pass < 8; pass++)
            {
                spread.Clear();
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        if (filled[i]) continue;
                        int r = 0, g = 0, bl = 0, n = 0;
                        void Add(int xx, int yy) { if (xx < 0 || yy < 0 || xx >= W || yy >= H) return; int j = yy * W + xx; if (!filled[j]) return; r += p[j].r; g += p[j].g; bl += p[j].b; n++; }
                        Add(x - 1, y); Add(x + 1, y); Add(x, y - 1); Add(x, y + 1);
                        if (n == 0) continue;
                        p[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(bl / n), p[i].a);
                        spread.Add(i);
                    }
                if (spread.Count == 0) break;
                foreach (int i in spread) filled[i] = true;
            }
        }

        // View k is baked from direction k * 45 deg (yaw) round the tree, looking at its centre.
        private static Vector3 ViewNormal(int k)
        {
            float a = k * (360f / Views) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        // A star of Views/2 vertical planes through the tree's centre, every 45 deg. Each plane has two faces, and each face
        // shows the view baked from the side it faces (front: view p, back: view p + Views/2), so the impostor changes as the
        // viewer circles it. The back face sits 1 cm toward its own viewer, so with a cull-off shader each side still shows its own view. 16 triangles.
        private static Mesh StarPlanes(float side, float y0, float cx, float cz)
        {
            int planes = Views / 2;
            float hw = side * 0.5f, y1 = y0 + side;
            var verts = new Vector3[planes * 8]; var uvs = new Vector2[planes * 8]; var tris = new int[planes * 12];
            int v = 0, t = 0;
            var o = new Vector3(cx, 0f, cz);
            void Face(Vector3 m, int view, float push)
            {
                Vector3 right = Quaternion.LookRotation(-m, Vector3.up) * Vector3.right;
                Vector3 c0 = o + m * push;   // toward its own viewer, so it wins the depth test from its side
                Vector3 a = c0 - right * hw + Vector3.up * y0, b = c0 + right * hw + Vector3.up * y0;
                Vector3 c = c0 + right * hw + Vector3.up * y1, d = c0 - right * hw + Vector3.up * y1;
                float u0 = (view % AtlasCols) / (float)AtlasCols, u1 = u0 + 1f / AtlasCols;
                float v0 = (view / AtlasCols) / (float)AtlasRows, v1 = v0 + 1f / AtlasRows;
                verts[v] = a; verts[v + 1] = b; verts[v + 2] = c; verts[v + 3] = d;
                uvs[v] = new Vector2(u0, v0); uvs[v + 1] = new Vector2(u1, v0); uvs[v + 2] = new Vector2(u1, v1); uvs[v + 3] = new Vector2(u0, v1);
                // Front-facing toward m (Unity: the face whose Cross(e1, e2) normal points at the viewer).
                bool acb = Vector3.Dot(Vector3.Cross(c - a, b - a), m) > 0f;
                if (acb) { tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1; tris[t + 3] = v; tris[t + 4] = v + 3; tris[t + 5] = v + 2; }
                else { tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2; tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3; }
                v += 4; t += 6;
            }
            for (int p = 0; p < planes; p++)
            {
                Vector3 n = ViewNormal(p);
                Face(n, p, 0f);
                Face(-n, p + planes, 0.01f);
            }
            var mesh = new Mesh { name = "FAP_ImpostorStar", vertices = verts, uv = uvs, triangles = tris };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        // The bake must not inherit the scene's sky (FAT's BakeEnvironment): fog off, flat ambient, scene directionals off.
        private sealed class Environment
        {
            private bool _fog; private UnityEngine.Rendering.AmbientMode _mode; private Color _ambient; private float _intensity;
            private readonly List<Light> _off = new List<Light>();
            internal static Environment Capture() => new Environment { _fog = RenderSettings.fog, _mode = RenderSettings.ambientMode, _ambient = RenderSettings.ambientLight, _intensity = RenderSettings.ambientIntensity };
            internal void Apply()
            {
                RenderSettings.fog = false; RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(BakeAmbient, BakeAmbient, BakeAmbient, 1f); RenderSettings.ambientIntensity = 1f;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                    if (l != null && l.enabled && l.type == LightType.Directional && l.gameObject.name != LightName) { l.enabled = false; _off.Add(l); }
            }
            internal void Restore()
            {
                foreach (var l in _off) if (l != null) l.enabled = true;
                _off.Clear();
                RenderSettings.fog = _fog; RenderSettings.ambientMode = _mode; RenderSettings.ambientLight = _ambient; RenderSettings.ambientIntensity = _intensity;
            }
        }
    }
}
