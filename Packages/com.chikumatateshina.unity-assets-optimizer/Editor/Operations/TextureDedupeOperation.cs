using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// 内容ハッシュ + Importer 設定（sRGB/NormalMap/Alpha 等を含む .meta）で重複テクスチャを検出する。
    /// 統合は Material 統合の等価判定へ反映することで行い、テクスチャ単体の差し替え（Material コピー生成）は行わない。
    /// </summary>
    public sealed class TextureDedupeOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.TextureDedupe;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities { Phase = 2 };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var textures = context.Analysis.Renderers
                .SelectMany(r => r.Materials)
                .Where(m => m != null && m.shader != null)
                .Distinct()
                .SelectMany(m => Enumerable.Range(0, m.shader.GetPropertyCount())
                    .Where(i => m.shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                    .Select(i => m.GetTexture(m.shader.GetPropertyName(i))))
                .Where(t => t != null && AssetDatabase.IsMainAsset(t))
                .Distinct()
                .ToList();

            var groups = textures
                .Select(t => new { Texture = t, Key = TextureIdentity.ContentKey(AssetDatabase.GetAssetPath(t)) })
                .Where(x => x.Key != null)
                .GroupBy(x => x.Key)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var g in groups)
            {
                var paths = g.Select(x => AssetDatabase.GetAssetPath(x.Texture)).OrderBy(p => p, StringComparer.Ordinal).ToList();
                analysis.Items.Add(PlanItem.Supported(string.Join(" == ", paths), "内容・Importer 設定が同一"));
            }
            analysis.Notes.Add("重複テクスチャ単体の差し替え（Material コピー生成）は未実装です。Material 統合の等価判定に反映します。");
            return analysis;
        }

        public override void ApplyToContext(OperationPlan plan, OptimizationContext context)
        {
            context.TextureContentDedupe = true;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan) { }
    }
}
