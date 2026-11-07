using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// AAO の Trace And Optimize を出力アバターに付ける（ビルド時に BlendShape 最適化・未使用オブジェクト/ボーン削除・
    /// PhysBone/Animator 最適化・SkinnedMesh 自動結合・テクスチャ最適化が行われる）。原本には付けない。
    /// </summary>
    public sealed class AaoTraceAndOptimizeOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.AaoTraceAndOptimize;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            World = false,
            Phase = 3,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.MeshCombine, FeatureIds.BuildPolygonReduction },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var tool = ExternalTools.AvatarOptimizer;
            for (int i = 0; i < context.Graph.Roots.Count; i++)
            {
                var root = context.Graph.Roots[i];
                if (!tool.IsAvailable(out var reason))
                {
                    analysis.Items.Add(PlanItem.Skip(root.name, SupportState.Unsupported, reason));
                    continue;
                }
                if (!root.GetComponents<MonoBehaviour>().Any(m => m != null && m.GetType().FullName == AssetAnalyzer.AvatarDescriptorType))
                {
                    analysis.Items.Add(PlanItem.Skip(root.name, SupportState.Unsupported, "アバターのルート（VRCAvatarDescriptor）ではありません"));
                    continue;
                }
                var type = tool.ResolveType(tool.MainType);
                if (root.GetComponentInChildren(type, true) != null)
                {
                    analysis.Items.Add(PlanItem.Skip(root.name, SupportState.Unsupported, "既に AAO Trace And Optimize が設定されています"));
                    continue;
                }
                analysis.Items.Add(PlanItem.Supported(root.name, "ビルド時に AAO Trace And Optimize を適用（" + reason + "）"));
            }
            analysis.Notes.Add("効果はアップロード時（NDMF のビルド）に現れます。「ビルド後（NDMF）」の評価で確認できます。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var tool = ExternalTools.AvatarOptimizer;
            var type = tool.ResolveType(tool.MainType);
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            for (int i = 0; i < gen.Copy.Roots.Count; i++)
            {
                var root = gen.Copy.Roots[i];
                if (!included.Contains(root.name) || root.GetComponentInChildren(type, true) != null) continue;
                root.AddComponent(type);
                gen.RegisterAdded(i, string.Empty, type.FullName, Id);
            }
        }
    }

    /// <summary>
    /// lilNDMFMeshSimplifier によるビルド時のポリゴン削減。SkinnedMesh も BlendShape・ボーンを保ったまま削減される。
    /// 目標三角形数（既定は SDK の PC Poor 基準）に収まるよう品質を決め、顔（Viseme / まぶた）のメッシュは保護する。
    /// </summary>
    public sealed class BuildPolygonReductionOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.BuildPolygonReduction;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            World = false,
            Phase = 4,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.MeshCombine, FeatureIds.PolygonReduction },
        };

        sealed class Target
        {
            public RendererInfo Info;
            public long Triangles;
            public float Quality;
            public PlanItem Item;
        }

        static long Triangles(Mesh mesh) => mesh == null ? 0 : MeshCombineOperation.Triangles(mesh);

        /// <summary>顔として保護する Renderer（VRCAvatarDescriptor の Viseme / まぶた用メッシュ）。</summary>
        static HashSet<Renderer> FaceRenderers(GameObject root)
        {
            var set = new HashSet<Renderer>();
            var descriptor = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m != null && m.GetType().FullName == AssetAnalyzer.AvatarDescriptorType);
            if (descriptor == null) return set;
            var so = new SerializedObject(descriptor);
            foreach (var path in new[] { "VisemeSkinnedMesh", "customEyeLookSettings.eyelidsSkinnedMesh" })
                if (so.FindProperty(path)?.objectReferenceValue is Renderer r) set.Add(r);
            return set;
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var tool = ExternalTools.LilMeshSimplifier;
            var targets = new List<Target>();
            if (!tool.IsAvailable(out var unavailable))
            {
                analysis.Items.Add(PlanItem.Skip("ビルド時ポリゴン削減", SupportState.Unsupported, unavailable));
                return analysis;
            }
            var simplifierType = tool.ResolveType(tool.MainType);
            long goal = context.Settings.buildReductionTargetTriangles > 0
                ? context.Settings.buildReductionTargetTriangles
                : (long)(SdkOfficialRankProvider.StatLimit("Poor", false, "polyCount") ?? 0);
            if (goal <= 0)
            {
                analysis.Items.Add(PlanItem.Skip("ビルド時ポリゴン削減", SupportState.Unsupported, "目標三角形数を決められません（VRChat SDK の閾値を取得できないため、詳細パラメータで指定してください）"));
                return analysis;
            }

            long total = 0, fixedTris = 0;
            var faces = new HashSet<Renderer>(context.Graph.Roots.SelectMany(FaceRenderers));
            foreach (var info in context.Analysis.Renderers)
            {
                var label = info.Path.Length == 0 ? "(root)" : info.Path;
                long tris = Triangles(info.Mesh);
                total += tris;
                string reason = null;
                if (!(info.Renderer is SkinnedMeshRenderer || info.Renderer is MeshRenderer) || info.Mesh == null) reason = "対象外の Renderer/Mesh なし";
                else if (info.Renderer.GetComponent(simplifierType) != null) reason = "既に lilNDMFMeshSimplifier が設定されています（設定を尊重）";
                else if (context.Settings.protectFaceMesh && faces.Contains(info.Renderer)) reason = "顔のメッシュ（Viseme/まぶた）を保護";
                else if (tris < 300) reason = "三角形数が少ない（" + tris + "）";
                if (reason != null)
                {
                    fixedTris += tris;
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, reason));
                    continue;
                }
                var item = PlanItem.Supported(label);
                analysis.Items.Add(item);
                targets.Add(new Target { Info = info, Triangles = tris, Item = item });
            }

            long reducible = targets.Sum(t => t.Triangles);
            if (total <= goal || reducible == 0)
            {
                foreach (var t in targets)
                {
                    t.Item.support = SupportState.Unsupported;
                    t.Item.reason = total <= goal ? "既に目標（" + goal + " 三角形）以下" : "削減できるメッシュがありません";
                }
                return analysis;
            }
            float quality = Mathf.Clamp((float)(goal - fixedTris) / reducible, context.Settings.buildReductionMinQuality, 1f);
            foreach (var t in targets)
            {
                t.Quality = quality;
                t.Item.reason = "品質 " + quality.ToString("0.00") + "（" + t.Triangles + " → 約 " + (long)(t.Triangles * quality) + " 三角形）";
            }
            analysis.Data = targets;
            var expected = fixedTris + (long)(reducible * quality);
            analysis.Notes.Add("目標 " + goal + " 三角形（" + (context.Settings.buildReductionTargetTriangles > 0 ? "指定値" : "VRChat SDK の PC Poor 基準") + "）。現在 " + total +
                               " → 約 " + expected + "（保護・対象外 " + fixedTris + "）。" + (expected > goal ? "最低品質 " + context.Settings.buildReductionMinQuality + " の制限により目標には届きません。" : string.Empty));
            analysis.Notes.Add("削減はアップロード時（NDMF のビルド）に lilNDMFMeshSimplifier が行います。「ビルド後（NDMF）」の評価で確認できます。");
            return analysis;
        }

        public override OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            var targets = analysis.Data as List<Target>;
            plan.ExpectedEffect = targets == null || targets.Count == 0 ? "削減対象なし" : "SkinnedMesh/Mesh " + targets.Count(t => t.Item.included) + " 個に品質 " + targets[0].Quality.ToString("0.00") + " を設定";
            return plan;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var targets = plan.Data as List<Target>;
            if (targets == null) return;
            var tool = ExternalTools.LilMeshSimplifier;
            var type = tool.ResolveType(tool.MainType);
            foreach (var t in targets.Where(t => t.Item.included))
            {
                var r = gen.Copy.FindRenderer(t.Info.Key);
                if (r == null || r.GetComponent(type) != null) continue; // 結合で削除済み、または既に設定済み
                var component = r.gameObject.AddComponent(type);
                var so = new SerializedObject(component);
                var quality = so.FindProperty("quality");
                if (quality == null) throw new InvalidOperationException("lilNDMFMeshSimplifier の quality プロパティが見つかりません。");
                quality.floatValue = t.Quality;
                so.ApplyModifiedPropertiesWithoutUndo();
                gen.RegisterAdded(gen.Copy.RootIndexOf(r.transform), gen.Copy.PathOf(r.transform), type.FullName, Id);
            }
        }
    }
}
