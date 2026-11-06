using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public static class MetricKeys
    {
        public const string MeshCount = "mesh_count";
        public const string RendererCount = "renderer_count";
        public const string UniqueMaterials = "unique_material_count";
        public const string MaterialSlots = "material_slot_count";
        public const string SubMeshes = "submesh_count";
        public const string Triangles = "triangle_count";
        public const string Vertices = "vertex_count";
        public const string TextureCount = "texture_count";
        public const string DiskSize = "dependency_disk_size";
        public const string TextureMemory = "texture_gpu_memory";
        public const string DrawCalls = "draw_calls";
        public const string SetPassCalls = "setpass_calls";
        public const string Batches = "batches";
        public const string CpuTime = "cpu_frame_time";
        public const string GpuTime = "gpu_frame_time";

        public static readonly string[] Ordered =
        {
            MeshCount, RendererCount, UniqueMaterials, MaterialSlots, SubMeshes, Triangles, Vertices,
            TextureCount, DiskSize, TextureMemory, DrawCalls, SetPassCalls, Batches, CpuTime, GpuTime,
        };
    }

    /// <summary>
    /// 階層・アセットから構造指標を算出する。描画系指標は計測手段がないため「取得不可」として理由を返す。
    /// </summary>
    public static class MetricsCalculator
    {
        const string TimeUnavailable = "フレーム時間は実機/Editor での実測が必要です（構造値からは算出しません）。";
        public const string RenderModel = "モデル推定（Built-in Forward・平行光源1灯・影なし・バッチング/インスタンシングなし・有効な Mesh/SkinnedMesh Renderer が全て可視）";

        /// <param name="plannedTextures">Dry Run で生成した未インポートのテクスチャ（推定方法を切り替える）。</param>
        public static MetricSet Compute(IList<GameObject> roots, MeasurementContext context, IList<GeneratedTexture> plannedTextures = null)
        {
            var set = new MetricSet { contextId = context.contextId };
            var cid = context.contextId;
            var renderers = roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).ToList();

            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            long slots = 0, submeshes = 0, triangles = 0, vertices = 0;
            foreach (var r in renderers)
            {
                var shared = r.sharedMaterials;
                slots += shared.Length;
                foreach (var m in shared) if (m != null) materials.Add(m);

                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else if (r is MeshRenderer) mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                meshes.Add(mesh);
                submeshes += mesh.subMeshCount;
                vertices += mesh.vertexCount;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (mesh.GetTopology(s) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(s) / 3;
            }

            var textures = new HashSet<Texture>();
            foreach (var m in materials)
            {
                if (m.shader == null) continue;
                for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                {
                    if (m.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var t = m.GetTexture(m.shader.GetPropertyName(i));
                    if (t != null) textures.Add(t);
                }
            }

            const string hierarchy = "階層の Renderer/Mesh から計算（非アクティブ含む）";
            set.Add(Metric.Of(MetricKeys.MeshCount, "Mesh数", meshes.Count, "", MetricKind.Calculated, hierarchy, cid));
            set.Add(Metric.Of(MetricKeys.RendererCount, "Renderer数", renderers.Count, "", MetricKind.Calculated, hierarchy, cid));
            set.Add(Metric.Of(MetricKeys.UniqueMaterials, "ユニークMaterial数", materials.Count, "", MetricKind.Calculated, hierarchy, cid));
            set.Add(Metric.Of(MetricKeys.MaterialSlots, "Materialスロット数", slots, "", MetricKind.Calculated, hierarchy, cid));
            set.Add(Metric.Of(MetricKeys.SubMeshes, "SubMesh数", submeshes, "", MetricKind.Calculated, hierarchy + "（Renderer ごとに合算）", cid));
            set.Add(Metric.Of(MetricKeys.Triangles, "三角形数", triangles, "", MetricKind.Calculated, hierarchy + "（Renderer ごとに合算）", cid));
            set.Add(Metric.Of(MetricKeys.Vertices, "頂点数", vertices, "", MetricKind.Calculated, hierarchy + "（Renderer ごとに合算）", cid));
            set.Add(Metric.Of(MetricKeys.TextureCount, "Texture数", textures.Count, "", MetricKind.Calculated, "Material が参照するテクスチャ", cid));

            set.Add(DiskSize(meshes, materials, textures, plannedTextures, cid));
            set.Add(TextureMemory(textures, plannedTextures, cid));

            AddRenderEstimates(set, renderers, roots, cid);
            set.Add(Metric.Unavailable(MetricKeys.CpuTime, "CPU描画時間", "ms", TimeUnavailable, cid));
            set.Add(Metric.Unavailable(MetricKeys.GpuTime, "GPU描画時間", "ms", TimeUnavailable, cid));
            return set;
        }

        static bool IsActive(Renderer r, IList<GameObject> roots)
        {
            if (!r.enabled) return false;
            for (var t = r.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (roots.Any(root => root.transform == t)) break;
            }
            return true;
        }

        /// <summary>
        /// 描画系のモデル推定。実測ではなく、前提条件（RenderModel）の下での上限目安。
        /// Draw Calls = Σ(有効スロット × Forward 系パス数)、SetPass = (シェーダー, キーワード, パス) の種類数。
        /// </summary>
        static void AddRenderEstimates(MetricSet set, List<Renderer> renderers, IList<GameObject> roots, string cid)
        {
            long draws = 0;
            var setPass = new HashSet<string>();
            int excluded = 0;
            foreach (var r in renderers)
            {
                if (!IsActive(r, roots)) continue;
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (mesh == null)
                {
                    excluded++;
                    continue;
                }
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    var keywords = string.Join(",", m.shaderKeywords.OrderBy(k => k, System.StringComparer.Ordinal));
                    for (int p = 0; p < m.passCount; p++)
                    {
                        var lightMode = m.shader.FindPassTagValue(p, new ShaderTagId("LightMode")).name ?? string.Empty;
                        if (!IsForwardPass(lightMode)) continue;
                        if (lightMode.Length > 0 && !m.GetShaderPassEnabled(lightMode)) continue;
                        draws++;
                        setPass.Add(m.shader.name + "|" + keywords + "|" + p);
                    }
                }
            }
            var method = RenderModel + (excluded > 0 ? "。Mesh 以外の Renderer（Particle/Trail/Line 等）" + excluded + " 個は含まない" : string.Empty);
            set.Add(Metric.Of(MetricKeys.DrawCalls, "Draw Calls", draws, "", MetricKind.Estimated, method, cid));
            set.Add(Metric.Of(MetricKeys.SetPassCalls, "SetPass Calls", setPass.Count, "", MetricKind.Estimated, method + "。シェーダー/キーワード/パスの種類数（下限目安）", cid));
            set.Add(Metric.Of(MetricKeys.Batches, "Batches", draws, "", MetricKind.Estimated, method + "。バッチングなしの前提のため Draw Calls と同値", cid));
        }

        static bool IsForwardPass(string lightMode)
        {
            switch (lightMode)
            {
                case "":
                case "Always":
                case "ForwardBase":
                case "SRPDefaultUnlit":
                case "UniversalForward":
                    return true;
                default:
                    return false;
            }
        }

        static Metric DiskSize(HashSet<Mesh> meshes, HashSet<Material> materials, HashSet<Texture> textures, IList<GeneratedTexture> planned, string cid)
        {
            var objects = meshes.Cast<Object>().Concat(materials).Concat(textures);
            var paths = new HashSet<string>();
            int transient = 0;
            foreach (var o in objects)
            {
                if (!EditorUtility.IsPersistent(o))
                {
                    transient++;
                    continue;
                }
                var p = AssetDatabase.GetAssetPath(o);
                if (!AssetPathUtility.IsBuiltin(p)) paths.Add(p);
            }
            long size = paths.Sum(AssetPathUtility.FileSize);
            const string label = "依存アセットのディスク容量";
            if (transient > 0)
                return Metric.Unavailable(MetricKeys.DiskSize, label, "bytes",
                    "Dry Run の生成物（" + transient + "件）は未保存のためファイル容量を算出できません（保存済み分: " + Metric.FormatNumber(size, "bytes") + "）。", cid);
            return Metric.Of(MetricKeys.DiskSize, label, size, "bytes", MetricKind.Calculated,
                "Mesh/Material/Texture のソースファイル容量合計（FBX は全体）。ビルドサイズではない", cid);
        }

        static Metric TextureMemory(HashSet<Texture> textures, IList<GeneratedTexture> planned, string cid)
        {
            const string label = "Texture GPUメモリ";
            long total = 0;
            var methods = new HashSet<string>();
            var unsupported = new List<string>();
            foreach (var t in textures)
            {
                var plannedRecord = planned?.FirstOrDefault(p => p.Texture == t && string.IsNullOrEmpty(p.AssetPath));
                if (plannedRecord != null)
                {
                    var est = TextureMemoryEstimator.EstimatePlanned(plannedRecord, out var method);
                    if (est == null)
                    {
                        unsupported.Add(t.name + ": " + method);
                        continue;
                    }
                    total += est.Value;
                    methods.Add(method);
                }
                else if (t is Texture2D t2d)
                {
                    total += TextureMemoryEstimator.Estimate(t2d);
                    methods.Add(TextureMemoryEstimator.MethodImported);
                }
                else
                {
                    unsupported.Add(t.name + ": " + t.GetType().Name + " は推定対象外");
                }
            }
            if (unsupported.Count > 0)
                return Metric.Unavailable(MetricKeys.TextureMemory, label, "bytes", "推定不能なテクスチャがあります: " + string.Join(" / ", unsupported), cid);
            return Metric.Of(MetricKeys.TextureMemory, label, total, "bytes", MetricKind.Estimated,
                string.Join(" + ", methods.OrderBy(m => m)) + "。実際の常駐 VRAM ではない", cid);
        }
    }
}
