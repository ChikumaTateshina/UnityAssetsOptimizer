using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// 全プロパティ・描画状態が等価な Material を代表 Material へ統合する。
    /// ワーキングコピーの Renderer スロットのみを差し替え、Material アセット自体は変更しない。
    /// </summary>
    public sealed class MaterialDedupeOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.MaterialDedupe;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            CanForce = true,
            Phase = 1,
            RunsAfter = new[] { FeatureIds.TextureDedupe },
        };

        sealed class DedupeData
        {
            public Dictionary<Material, Material> Map = new Dictionary<Material, Material>();
            public Dictionary<PlanItem, Material> ItemMaterials = new Dictionary<PlanItem, Material>();
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var materials = context.Analysis.Renderers.SelectMany(r => r.Materials).Where(m => m != null).Distinct().ToList();
            var map = MaterialEquivalence.BuildCanonicalMap(materials, context.TextureContentDedupe, out var groups);
            var data = new DedupeData { Map = map };

            foreach (var group in groups)
            {
                var rep = group[0];
                foreach (var m in group.Skip(1))
                {
                    var label = m.name + " → " + rep.name;
                    PlanItem item;
                    if (context.Analysis.ScriptReferencedMaterials.Contains(m) || context.Analysis.ScriptReferencedMaterials.Contains(rep))
                        item = PlanItem.Skip(label, SupportState.Unknown, "スクリプトから参照されている Material（参照同一性に依存する可能性）", true);
                    else
                        item = PlanItem.Supported(label, "全プロパティ・描画状態が等価");
                    analysis.Items.Add(item);
                    data.ItemMaterials[item] = m;
                }
            }
            if (context.TextureContentDedupe)
                analysis.Notes.Add("重複Texture検出が有効なため、内容と Importer 設定が同一のテクスチャを等価として判定しています。");
            analysis.Data = data;
            return analysis;
        }

        public override void ApplyToContext(OperationPlan plan, OptimizationContext context)
        {
            context.CanonicalMaterials = BuildIncludedMap(plan);
        }

        static Dictionary<Material, Material> BuildIncludedMap(OperationPlan plan)
        {
            var data = (DedupeData)plan.Data;
            var result = new Dictionary<Material, Material>();
            foreach (var item in plan.Items)
            {
                if (!item.included) continue;
                var m = data.ItemMaterials[item];
                result[m] = data.Map[m];
            }
            return result;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var map = BuildIncludedMap(plan);
            foreach (var pair in gen.Copy.AllRenderers)
            {
                var mats = pair.Value.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && map.TryGetValue(mats[i], out var rep))
                    {
                        mats[i] = rep;
                        changed = true;
                    }
                }
                if (changed) pair.Value.sharedMaterials = mats;
            }
        }

        public override ValidationResult Validate(GenerationContext gen, OperationPlan plan)
        {
            var result = new ValidationResult();
            var map = BuildIncludedMap(plan);
            foreach (var pair in gen.Copy.AllRenderers)
                foreach (var m in pair.Value.sharedMaterials)
                    if (m != null && map.ContainsKey(m))
                        result.Fail(pair.Key + ": 統合対象の Material が残っています（" + m.name + "）。");
            return result;
        }
    }
}
