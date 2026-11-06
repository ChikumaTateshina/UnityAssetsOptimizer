using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;

namespace ChikumaTateshina.AssetsOptimizer
{
    [Serializable]
    public class RankStatRule
    {
        public string key;
        /// <summary>ranks と同じ順序の上限値（値 ≤ 閾値ならそのランク）。</summary>
        public double[] thresholds;
    }

    /// <summary>
    /// SDK 版・プラットフォームに紐付いたランク閾値。verified=true のものだけを評価に用いる。
    /// 閾値はコードに埋め込まず、検証済みデータとして配置する（Documentation~/CompatibilityMatrix.md 参照）。
    /// </summary>
    [Serializable]
    public class RankRuleSet
    {
        public int schemaVersion;
        public string ruleSetId;
        public string platform;
        public string sdkMinVersion;
        public string sdkMaxVersionExclusive;
        public bool verified;
        public string source;
        public string[] ranks;
        public RankStatRule[] stats;
        [NonSerialized] public string filePath;
    }

    [Serializable]
    public class RankStatRow
    {
        public string key;
        public string label;
        public double value;
        public string threshold;
        public string excess;
        public string rank;
    }

    [Serializable]
    public class RankResult
    {
        public string platform;
        public RankStatus status;
        public string statusLabel;
        public string rank;
        public string ruleSetId;
        public List<string> reasons = new List<string>();
        public List<RankStatRow> rows = new List<RankStatRow>();
    }

    public interface IVrcRankProvider
    {
        string Id { get; }
        RankResult Evaluate(IList<GameObject> roots, VrcPlatform platform, bool buildTimeToolsPresent);
    }

    /// <summary>
    /// SDK 公式評価器。公開された安定 API の存在と仕様を検証するまで評価しない。
    /// </summary>
    public sealed class SdkOfficialRankProvider : IVrcRankProvider
    {
        public string Id => "sdk-official";

        public RankResult Evaluate(IList<GameObject> roots, VrcPlatform platform, bool buildTimeToolsPresent)
        {
            var result = new RankResult { platform = platform.ToString(), status = RankStatus.Unavailable };
            result.reasons.Add(SdkInfo.Version("com.vrchat.avatars") == null
                ? "VRChat Avatars SDK が見つかりません。"
                : "SDK の公開評価 API は未検証のため呼び出しません（非公開 API には依存しない方針）。");
            result.statusLabel = result.status.Label();
            return result;
        }
    }

    /// <summary>SDK 版に対応した検証済み閾値 DB に基づく独立推定。</summary>
    public sealed class VersionedEstimateRankProvider : IVrcRankProvider
    {
        public string Id => "versioned-estimate";

        public RankResult Evaluate(IList<GameObject> roots, VrcPlatform platform, bool buildTimeToolsPresent)
        {
            var result = new RankResult { platform = platform.ToString() };
            var stats = AvatarStatsCollector.Collect(roots);
            var sdk = SdkInfo.Version("com.vrchat.avatars");
            var rules = RankRuleLoader.LoadAll();
            var candidates = rules.Where(r => r.platform == platform.ToString()).ToList();
            var ruleSet = candidates.FirstOrDefault(r => r.verified && sdk != null && RankRuleLoader.InRange(sdk, r.sdkMinVersion, r.sdkMaxVersionExclusive));

            if (ruleSet == null)
            {
                result.status = RankStatus.Unavailable;
                if (sdk == null) result.reasons.Add("VRChat Avatars SDK が見つからないため、対応する閾値 DB を選べません。");
                else if (candidates.Count == 0) result.reasons.Add(platform + " 用の閾値 DB がありません。");
                else result.reasons.Add("SDK " + sdk + " に対応する検証済み（verified=true）の閾値 DB がありません。未検証の閾値ではランクを算出しません。");
                foreach (var s in stats)
                    result.rows.Add(new RankStatRow { key = s.Key, label = s.Label, value = s.Value, threshold = "—", excess = "—", rank = "—" });
                result.rank = "算出不可";
                result.statusLabel = result.status.Label();
                return result;
            }

            result.ruleSetId = ruleSet.ruleSetId;
            int worst = -1;
            foreach (var s in stats)
            {
                var row = new RankStatRow { key = s.Key, label = s.Label, value = s.Value, threshold = "—", excess = "—", rank = "—" };
                var rule = ruleSet.stats?.FirstOrDefault(x => x.key == s.Key);
                if (rule != null && rule.thresholds != null && rule.thresholds.Length > 0)
                {
                    int idx = Array.FindIndex(rule.thresholds, t => s.Value <= t);
                    int rankIndex = idx < 0 ? ruleSet.ranks.Length : idx;
                    row.rank = rankIndex < ruleSet.ranks.Length ? ruleSet.ranks[rankIndex] : "Very Poor";
                    row.threshold = string.Join(" / ", rule.thresholds.Select(t => t.ToString(CultureInfo.InvariantCulture)));
                    // 目標ランク（最上位）との差
                    double excess = s.Value - rule.thresholds[0];
                    row.excess = excess > 0 ? "+" + excess.ToString(CultureInfo.InvariantCulture) : "0";
                    worst = Math.Max(worst, rankIndex);
                }
                result.rows.Add(row);
            }
            result.rank = worst < 0 ? "算出不可" : worst < ruleSet.ranks.Length ? ruleSet.ranks[worst] : "Very Poor";
            result.status = buildTimeToolsPresent ? RankStatus.Incomplete : RankStatus.Estimated;
            result.reasons.Add("閾値 DB '" + ruleSet.ruleSetId + "'（" + ruleSet.source + "）に基づく独立推定。SDK 公式評価ではありません。");
            if (buildTimeToolsPresent) result.reasons.Add("ビルド時変換ツールを検出。ビルド後構成を評価できないため評価不完全です。");
            result.statusLabel = result.status.Label();
            return result;
        }
    }

    public static class RankRuleLoader
    {
        public static List<RankRuleSet> LoadAll()
        {
            var list = new List<RankRuleSet>();
            var dirs = new List<string>();
            var packageDir = AssetPathUtility.ToFullPath(VaoInfo.PackageRoot + "/Editor/Platforms/Data/RankRules");
            if (packageDir != null) dirs.Add(packageDir);
            dirs.Add(Path.Combine(VaoInfo.ProjectRoot, "Assets", "VAOSettings", "RankRules"));
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    try
                    {
                        var rule = JsonUtility.FromJson<RankRuleSet>(File.ReadAllText(file));
                        if (rule == null || rule.schemaVersion != 1) continue;
                        rule.filePath = file;
                        list.Add(rule);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[VAO] 閾値 DB を読み込めません: " + file + " (" + e.Message + ")");
                    }
                }
            }
            return list;
        }

        public static bool InRange(string version, string min, string maxExclusive)
        {
            if (!TryParse(version, out var v)) return false;
            if (!string.IsNullOrEmpty(min) && (!TryParse(min, out var lo) || v < lo)) return false;
            if (!string.IsNullOrEmpty(maxExclusive) && (!TryParse(maxExclusive, out var hi) || v >= hi)) return false;
            return true;
        }

        static bool TryParse(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            var core = s.Split('-', '+')[0];
            return Version.TryParse(core, out v);
        }
    }

    public struct AvatarStat
    {
        public string Key;
        public string Label;
        public double Value;
    }

    /// <summary>ランク評価用の構成値（非アクティブを含む）。コンポーネントは型名で判定し SDK へのコンパイル時依存を持たない。</summary>
    public static class AvatarStatsCollector
    {
        public static List<AvatarStat> Collect(IList<GameObject> roots)
        {
            var renderers = roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).ToList();
            var components = roots.SelectMany(r => r.GetComponentsInChildren<Component>(true)).Where(c => c != null).ToList();
            int CountType(string fullName) => components.Count(c => c.GetType().FullName == fullName);
            int CountPrefix(string prefix) => components.Count(c => (c.GetType().FullName ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal));

            long triangles = 0;
            foreach (var r in renderers)
            {
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (mesh == null) continue;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (mesh.GetTopology(s) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(s) / 3;
            }
            var bones = new HashSet<Transform>();
            foreach (var smr in renderers.OfType<SkinnedMeshRenderer>())
                foreach (var b in smr.bones) if (b != null) bones.Add(b);

            return new List<AvatarStat>
            {
                Stat("triangles", "Triangles", triangles),
                Stat("skinned_meshes", "Skinned Meshes", renderers.OfType<SkinnedMeshRenderer>().Count()),
                Stat("basic_meshes", "Basic Meshes", renderers.OfType<MeshRenderer>().Count()),
                Stat("material_slots", "Material Slots", renderers.Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).Sum(r => r.sharedMaterials.Length)),
                Stat("bones", "Bones", bones.Count),
                Stat("animators", "Animators", components.OfType<Animator>().Count()),
                Stat("lights", "Lights", components.OfType<Light>().Count()),
                Stat("particle_systems", "Particle Systems", components.OfType<ParticleSystem>().Count()),
                Stat("trail_renderers", "Trail Renderers", components.OfType<TrailRenderer>().Count()),
                Stat("line_renderers", "Line Renderers", components.OfType<LineRenderer>().Count()),
                Stat("cloths", "Cloths", components.Count(c => c.GetType().FullName == "UnityEngine.Cloth")),
                Stat("physics_colliders", "Physics Colliders", components.OfType<Collider>().Count()),
                Stat("rigidbodies", "Rigidbodies", components.OfType<Rigidbody>().Count()),
                Stat("audio_sources", "Audio Sources", components.OfType<AudioSource>().Count()),
                Stat("physbone_components", "PhysBone Components", CountType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone")),
                Stat("physbone_colliders", "PhysBone Colliders", CountType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider")),
                Stat("contacts", "Contacts", CountType("VRC.SDK3.Dynamics.Contact.Components.VRCContactSender") + CountType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver")),
                Stat("constraints", "Constraints", components.Count(c => c is IConstraint) + CountPrefix("VRC.SDK3.Dynamics.Constraint.Components.")),
            };
        }

        static AvatarStat Stat(string key, string label, double value) => new AvatarStat { Key = key, Label = label, Value = value };
    }
}
