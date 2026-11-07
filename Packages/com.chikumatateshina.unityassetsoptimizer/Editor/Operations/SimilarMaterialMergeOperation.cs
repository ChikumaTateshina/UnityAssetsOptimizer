using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class MaterialDifference
    {
        public string Property;
        public string Label;
        public string Representative;
        public string Member;
    }

    public sealed class SimilarMaterialMember
    {
        public Material Material;
        public int Slots;
        public List<MaterialDifference> Differences = new List<MaterialDifference>();
    }

    public sealed class SimilarMaterialGroup
    {
        public Material Representative;
        public int RepresentativeSlots;
        public readonly List<SimilarMaterialMember> Members = new List<SimilarMaterialMember>();
    }

    /// <summary>
    /// 近似 Material の検出。シェーダー・描画状態・キーワード・テクスチャ（内容と Tiling/Offset）が同じで、
    /// 数値・色のプロパティだけが異なる Material を候補にする。統合するかはユーザーが選ぶ（自動では統合しない）。
    /// </summary>
    public static class MaterialSimilarity
    {
        public static List<SimilarMaterialGroup> FindGroups(AnalysisReport analysis, Func<Material, Material> canonical, int maxDifferences)
        {
            var slots = new Dictionary<Material, int>();
            foreach (var info in analysis.Renderers)
                foreach (var raw in info.Materials)
                {
                    var m = canonical(raw);
                    if (m == null || !EditorUtility.IsPersistent(m) || analysis.ScriptReferencedMaterials.Contains(m)) continue;
                    slots[m] = slots.TryGetValue(m, out var c) ? c + 1 : 1;
                }

            var groups = new List<SimilarMaterialGroup>();
            var structures = slots.Keys.GroupBy(m => MaterialEquivalence.Key(m, true, includeNonTextureValues: false));
            foreach (var structure in structures)
            {
                // 使用スロットの多いものを代表にする（同数なら安定 ID 順）
                var ordered = structure.OrderByDescending(m => slots[m]).ThenBy(MaterialEquivalence.StableId, StringComparer.Ordinal).ToList();
                var clusters = new List<SimilarMaterialGroup>();
                foreach (var m in ordered)
                {
                    SimilarMaterialGroup target = null;
                    List<MaterialDifference> diffs = null;
                    foreach (var cluster in clusters)
                    {
                        diffs = Differences(cluster.Representative, m);
                        if (diffs.Count <= maxDifferences)
                        {
                            target = cluster;
                            break;
                        }
                    }
                    if (target == null) clusters.Add(new SimilarMaterialGroup { Representative = m, RepresentativeSlots = slots[m] });
                    else target.Members.Add(new SimilarMaterialMember { Material = m, Slots = slots[m], Differences = diffs });
                }
                groups.AddRange(clusters.Where(c => c.Members.Count > 0));
            }
            return groups.OrderByDescending(g => g.Members.Count).ThenBy(g => MaterialEquivalence.StableId(g.Representative), StringComparer.Ordinal).ToList();
        }

        /// <summary>数値・色プロパティの差分（構造キーが同じ Material 同士で使う）。</summary>
        public static List<MaterialDifference> Differences(Material representative, Material member)
        {
            var list = new List<MaterialDifference>();
            var shader = representative.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                var type = shader.GetPropertyType(i);
                if (type == ShaderPropertyType.Texture) continue;
                var name = shader.GetPropertyName(i);
                var a = MaterialEquivalence.PropertyValue(representative, name, type);
                var b = MaterialEquivalence.PropertyValue(member, name, type);
                if (a == b) continue;
                var description = shader.GetPropertyDescription(i);
                list.Add(new MaterialDifference
                {
                    Property = name,
                    // 表示名だけでは区別できないシェーダーがある（lilToon は翻訳キー）ため、プロパティ名を併記する
                    Label = string.IsNullOrEmpty(description) || description == name ? name : name + "（" + description + "）",
                    Representative = Short(a),
                    Member = Short(b),
                });
            }
            return list;
        }

        static string Short(string value) =>
            string.Join(", ", value.Split(',').Select(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : v));

        public static string Describe(IEnumerable<MaterialDifference> diffs) =>
            string.Join(" / ", diffs.Select(d => d.Label + ": " + d.Representative + " → " + d.Member));
    }

    /// <summary>
    /// ユーザーが選んだ近似 Material を代表 Material に統合する（選択されたものだけ。外観が変わる場合がある）。
    /// ワーキングコピーの Renderer スロットだけを差し替え、Material アセットは変更しない。
    /// </summary>
    public sealed class SimilarMaterialMergeOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.SimilarMaterialMerge;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 1,
            RunsAfter = new[] { FeatureIds.MaterialDedupe },
        };

        sealed class MergeData
        {
            public readonly Dictionary<PlanItem, KeyValuePair<Material, Material>> Pairs = new Dictionary<PlanItem, KeyValuePair<Material, Material>>();
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var data = new MergeData();
            analysis.Data = data;
            var choices = context.Settings.similarMaterialMerges;
            if (choices.Count == 0) return analysis;

            var groups = MaterialSimilarity.FindGroups(context.Analysis, context.Canonical, context.Settings.similarMaxDifferences);
            var used = new HashSet<string>(context.Analysis.Renderers.SelectMany(r => r.Materials).Where(m => m != null)
                .SelectMany(m => new[] { m, context.Canonical(m) }).Select(MaterialEquivalence.StableId));
            foreach (var choice in choices)
            {
                if (!used.Contains(choice.member)) continue; // 他の対象で選んだもの
                var group = groups.FirstOrDefault(g => g.Members.Any(x => MaterialEquivalence.StableId(x.Material) == choice.member));
                var member = group?.Members.First(x => MaterialEquivalence.StableId(x.Material) == choice.member);
                var label = Name(choice.member) + " → " + Name(choice.representative);
                if (member == null || MaterialEquivalence.StableId(group.Representative) != choice.representative)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "選択時から Material が変わり、近似の条件（同じシェーダー・描画状態・テクスチャ）を満たさなくなりました。選び直してください"));
                    continue;
                }
                var item = PlanItem.Supported(label, "ユーザーが選択（差分 " + member.Differences.Count + " 件: " + MaterialSimilarity.Describe(member.Differences) + "）");
                analysis.Items.Add(item);
                data.Pairs[item] = new KeyValuePair<Material, Material>(member.Material, group.Representative);
            }
            if (analysis.Items.Count > 0)
                analysis.Notes.Add("近似 Material の統合は等価ではありません。差分のプロパティは代表 Material の値になります（選択したものだけ統合）。");
            return analysis;
        }

        static string Name(string stableId)
        {
            var path = stableId.Split('#')[0];
            return string.IsNullOrEmpty(path) ? stableId : System.IO.Path.GetFileNameWithoutExtension(path);
        }

        public override OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            int n = plan.Items.Count(i => i.included);
            plan.ExpectedEffect = n == 0 ? "選択なし" : "Material " + n + " 個を代表へ統合";
            return plan;
        }

        static Dictionary<Material, Material> IncludedMap(OperationPlan plan)
        {
            var data = (MergeData)plan.Data;
            return plan.Items.Where(i => i.included && data.Pairs.ContainsKey(i)).ToDictionary(i => data.Pairs[i].Key, i => data.Pairs[i].Value);
        }

        public override void ApplyToContext(OperationPlan plan, OptimizationContext context)
        {
            var map = IncludedMap(plan);
            if (map.Count == 0) return;
            var composed = new Dictionary<Material, Material>();
            if (context.CanonicalMaterials != null)
                foreach (var pair in context.CanonicalMaterials)
                    composed[pair.Key] = map.TryGetValue(pair.Value, out var rep) ? rep : pair.Value;
            foreach (var pair in map) composed[pair.Key] = pair.Value;
            context.CanonicalMaterials = composed;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var map = IncludedMap(plan);
            foreach (var pair in gen.Copy.AllRenderers)
            {
                var mats = pair.Value.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && map.TryGetValue(mats[i], out var rep))
                    {
                        mats[i] = rep;
                        changed = true;
                    }
                if (changed) pair.Value.sharedMaterials = mats;
            }
        }

        public override ValidationResult Validate(GenerationContext gen, OperationPlan plan)
        {
            var result = new ValidationResult();
            var map = IncludedMap(plan);
            foreach (var pair in gen.Copy.AllRenderers)
                foreach (var m in pair.Value.sharedMaterials)
                    if (m != null && map.ContainsKey(m))
                        result.Fail(pair.Key + ": 統合対象の Material が残っています（" + m.name + "）。");
            return result;
        }
    }
}
