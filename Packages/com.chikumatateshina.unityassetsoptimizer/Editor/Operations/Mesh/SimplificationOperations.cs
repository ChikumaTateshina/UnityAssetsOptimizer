using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>簡略化系 Operation の共通判定。</summary>
    static class SimplificationRules
    {
        public static string Label(RendererInfo info) => info.Path.Length == 0 ? "(root)" : info.Path;

        /// <summary>簡略化できない理由。forceable は「形状/変形の自動判定のみが理由」の場合 true。</summary>
        public static string Reject(RendererInfo info, out bool forceable)
        {
            forceable = false;
            if (info.Mesh == null || !(info.Renderer is MeshRenderer || info.Renderer is SkinnedMeshRenderer)) return "対象外の Renderer/Mesh なし";
            for (int s = 0; s < info.Mesh.subMeshCount; s++)
                if (info.Mesh.GetTopology(s) != MeshTopology.Triangles) return "三角形以外のトポロジー";
            if (info.ScriptReferenced || info.UdonOnSameObject) return "スクリプトから Mesh を参照・操作する可能性";
            if (info.Skinned || info.Mesh.blendShapeCount > 0)
            {
                forceable = true;
                return "スキン/BlendShape 変形後の形状誤差は検証できません（静止形状のみ検証）";
            }
            return null;
        }

        public static string Format(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    /// <summary>ポリゴン削減（Phase 4）。誤差が許容値を超える縮約は行わず、削減効果が小さければ不採用。</summary>
    public sealed class PolygonReductionOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.PolygonReduction;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            CanForce = true,
            Phase = 4,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.MeshCombine },
            WritesAssetKinds = new[] { "Mesh" },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            foreach (var info in context.Analysis.Renderers)
            {
                var reason = SimplificationRules.Reject(info, out bool forceable);
                analysis.Items.Add(reason == null
                    ? PlanItem.Supported(SimplificationRules.Label(info), "目標比率 " + context.Settings.polygonReductionRatio)
                    : PlanItem.Skip(SimplificationRules.Label(info), forceable ? SupportState.Unknown : SupportState.Unsupported, reason, forceable));
            }
            analysis.Notes.Add("頂点は既存頂点へ寄せるだけで新しい属性を作らないため、UV・法線・ボーンウェイト・BlendShape の値は保持されます。境界・UV の継ぎ目・Material 境界は固定します。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            var cache = new Dictionary<Mesh, Mesh>();
            foreach (var pair in gen.Copy.AllRenderers)
            {
                gen.ThrowIfCancelled();
                var r = pair.Value;
                var label = gen.Copy.PathOf(r.transform);
                if (!included.Contains(string.IsNullOrEmpty(label) ? "(root)" : label)) continue;
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                if (!cache.TryGetValue(mesh, out var reduced))
                {
                    var result = MeshDecimator.Decimate(mesh, gen.Settings.polygonReductionRatio, gen.Settings.decimationMaxError);
                    if (result.ResultTriangles > result.SourceTriangles * 0.95)
                    {
                        Object.DestroyImmediate(result.Mesh);
                        gen.Log.Info(Id, label + ": 許容誤差内で削減効果が 5% 未満のため不採用（" + result.SourceTriangles + " → " + result.ResultTriangles + "）");
                        cache[mesh] = null;
                        continue;
                    }
                    gen.Log.Info(Id, label + ": " + result.SourceTriangles + " → " + result.ResultTriangles + " 三角形（最大誤差 " +
                                     SimplificationRules.Format(result.MaxError) + " / 許容 " + SimplificationRules.Format(result.ErrorLimit) + "）");
                    reduced = gen.AddAsset(result.Mesh, "Meshes", result.Mesh.name, Id, MaterialEquivalence.StableId(mesh));
                    cache[mesh] = reduced;
                }
                if (reduced == null) continue;
                if (r is SkinnedMeshRenderer s)
                {
                    var weights = Enumerable.Range(0, mesh.blendShapeCount).Select(s.GetBlendShapeWeight).ToArray();
                    s.sharedMesh = reduced;
                    for (int i = 0; i < weights.Length; i++) s.SetBlendShapeWeight(i, weights[i]);
                }
                else r.GetComponent<MeshFilter>().sharedMesh = reduced;
            }
        }
    }

    /// <summary>
    /// LOD 生成（Phase 4, MeshRenderer のみ）。対象オブジェクトに LODGroup を追加し、
    /// 子 GameObject として簡略化メッシュの Renderer を作る。元の Renderer は LOD0 として残す。
    /// </summary>
    public sealed class LodGenerationOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.LodGeneration;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 4,
            RunsAfter = new[] { FeatureIds.PolygonReduction, FeatureIds.MeshCombine, FeatureIds.StaticBatching, FeatureIds.GpuInstancing },
            WritesAssetKinds = new[] { "Mesh" },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            foreach (var info in context.Analysis.Renderers)
            {
                var label = SimplificationRules.Label(info);
                var reason = SimplificationRules.Reject(info, out _);
                if (reason == null && !(info.Renderer is MeshRenderer)) reason = "MeshRenderer のみ対応";
                if (reason == null && info.Renderer.GetComponentInParent<LODGroup>() != null) reason = "既に LODGroup 管理下";
                if (reason == null && info.AnimatedAny) reason = "アニメーション対象（LOD 切替で表示制御が競合）";
                if (reason == null && context.Analysis.ReferencedTransforms.Contains(info.Renderer.transform)) reason = "他のコンポーネントから参照されている";
                analysis.Items.Add(reason == null
                    ? PlanItem.Supported(label, "LOD 比率 " + string.Join("/", context.Settings.lodRatios))
                    : PlanItem.Skip(label, SupportState.Unsupported, reason));
            }
            analysis.Notes.Add("LODGroup と子 GameObject（LOD1 以降）を追加します。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            var ratios = gen.Settings.lodRatios ?? new float[0];
            var heights = gen.Settings.lodScreenHeights ?? new float[0];
            foreach (var pair in gen.Copy.AllRenderers.ToList())
            {
                gen.ThrowIfCancelled();
                if (!(pair.Value is MeshRenderer r)) continue;
                var label = gen.Copy.PathOf(r.transform);
                if (!included.Contains(string.IsNullOrEmpty(label) ? "(root)" : label)) continue;
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;

                var lods = new List<LOD>();
                lods.Add(new LOD(heights.Length > 0 ? heights[0] : 0.5f, new Renderer[] { r }));
                int rootIndex = gen.Copy.RootIndexOf(r.transform);
                for (int i = 0; i < ratios.Length; i++)
                {
                    var result = MeshDecimator.Decimate(mesh, ratios[i], gen.Settings.decimationMaxError * (i + 1));
                    if (result.ResultTriangles > result.SourceTriangles * 0.95)
                    {
                        Object.DestroyImmediate(result.Mesh);
                        break;
                    }
                    var lodMesh = gen.AddAsset(result.Mesh, "Meshes", mesh.name + "_LOD" + (i + 1), Id, MaterialEquivalence.StableId(mesh));
                    var child = new GameObject(r.gameObject.name + "_LOD" + (i + 1));
                    child.transform.SetParent(r.transform, false);
                    child.layer = r.gameObject.layer;
                    GameObjectUtility.SetStaticEditorFlags(child, GameObjectUtility.GetStaticEditorFlags(r.gameObject));
                    child.AddComponent<MeshFilter>().sharedMesh = lodMesh;
                    var lr = child.AddComponent<MeshRenderer>();
                    EditorUtility.CopySerialized(r, lr);
                    lr.sharedMaterials = r.sharedMaterials;
                    gen.RegisterAdded(rootIndex, gen.Copy.PathOf(child.transform), "GameObject", Id);
                    lods.Add(new LOD(i + 1 < heights.Length ? heights[i + 1] : 0.01f, new Renderer[] { lr }));
                }
                if (lods.Count < 2)
                {
                    gen.Log.Info(Id, label + ": 許容誤差内で有効な LOD を作れないため不採用");
                    continue;
                }
                var group = r.gameObject.AddComponent<LODGroup>();
                group.SetLODs(lods.ToArray());
                group.RecalculateBounds();
                gen.RegisterAdded(rootIndex, label, typeof(LODGroup).FullName, Id);
            }
        }
    }

    /// <summary>Collider 簡略化（World, Phase 4）。非 Convex の MeshCollider のメッシュを簡略化したコピーに置き換える。</summary>
    public sealed class ColliderSimplifyOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.ColliderSimplify;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Avatar = false,
            Phase = 4,
            RunsAfter = new[] { FeatureIds.PolygonReduction },
            WritesAssetKinds = new[] { "Mesh" },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var a = context.Analysis;
            foreach (var root in context.Graph.Roots)
                foreach (var col in root.GetComponentsInChildren<MeshCollider>(true))
                {
                    var label = AssetPathUtility.HierarchyPath(col.transform, root.transform);
                    if (label.Length == 0) label = "(root)";
                    if (col.sharedMesh == null) analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "Mesh 未設定"));
                    else if (col.convex) analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "Convex（既に簡略形状）"));
                    else if (a.HasUdon || a.ReferencedTransforms.Contains(col.transform))
                        analysis.Items.Add(PlanItem.Skip(label, SupportState.Unknown, "Udon/スクリプトから参照される可能性（当たり判定の機能互換性を判定不能）"));
                    else if (MeshCombineOperation.Triangles(col.sharedMesh) < 64) analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "三角形数が少ない"));
                    else analysis.Items.Add(PlanItem.Supported(label, MeshCombineOperation.Triangles(col.sharedMesh) + " 三角形"));
                }
            analysis.Notes.Add("当たり判定の形状が変わります（許容誤差内）。Trigger/物理挙動の確認を推奨します。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            var cache = new Dictionary<Mesh, Mesh>();
            foreach (var root in gen.Copy.Roots)
                foreach (var col in root.GetComponentsInChildren<MeshCollider>(true))
                {
                    var label = AssetPathUtility.HierarchyPath(col.transform, root.transform);
                    if (!included.Contains(label.Length == 0 ? "(root)" : label) || col.sharedMesh == null) continue;
                    if (!cache.TryGetValue(col.sharedMesh, out var reduced))
                    {
                        var result = MeshDecimator.Decimate(col.sharedMesh, gen.Settings.polygonReductionRatio, gen.Settings.decimationMaxError);
                        if (result.ResultTriangles > result.SourceTriangles * 0.95)
                        {
                            Object.DestroyImmediate(result.Mesh);
                            cache[col.sharedMesh] = null;
                            continue;
                        }
                        reduced = gen.AddAsset(result.Mesh, "Meshes", col.sharedMesh.name + "_collider", Id, MaterialEquivalence.StableId(col.sharedMesh));
                        cache[col.sharedMesh] = reduced;
                    }
                    if (reduced != null) col.sharedMesh = reduced;
                }
        }
    }
}
