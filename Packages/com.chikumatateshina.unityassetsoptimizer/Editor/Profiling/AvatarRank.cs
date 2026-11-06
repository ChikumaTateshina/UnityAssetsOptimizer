using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Animations;

namespace ChikumaTateshina.UnityAssetsOptimizer
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
    /// SDK 公式評価器。VRChat SDK（3.9.0 で確認）の公開クラス
    /// VRC.SDKBase.Validation.Performance.AvatarPerformance.CalculatePerformanceStats と
    /// AvatarPerformanceStats.GetPerformanceRatingForCategory / GetStatLevelForRating をリフレクションで呼ぶ
    /// （SDK へのコンパイル時依存を持たない）。API が見つからない場合は unavailable。
    /// </summary>
    public sealed class SdkOfficialRankProvider : IVrcRankProvider
    {
        const string Ns = "VRC.SDKBase.Validation.Performance.";

        public string Id => "sdk-official";

        static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

        public static bool IsAvailable(out string reason)
        {
            reason = null;
            if (FindType(Ns + "AvatarPerformance") == null || FindType(Ns + "Stats.AvatarPerformanceStats") == null)
            {
                reason = "VRChat SDK の評価 API（AvatarPerformance）が見つかりません。";
                return false;
            }
            return true;
        }

        public RankResult Evaluate(IList<GameObject> roots, VrcPlatform platform, bool buildTimeToolsPresent)
        {
            var result = new RankResult { platform = platform.ToString(), status = RankStatus.Unavailable, rank = "算出不可" };
            try
            {
                if (!IsAvailable(out var reason))
                {
                    result.reasons.Add(reason);
                    return Finish(result);
                }
                if (roots.Count != 1)
                {
                    result.reasons.Add("Avatar は1体ずつ評価します（選択ルート " + roots.Count + " 個）。");
                    return Finish(result);
                }
                var perfType = FindType(Ns + "AvatarPerformance");
                var statsType = FindType(Ns + "Stats.AvatarPerformanceStats");
                var categoryType = FindType(Ns + "AvatarPerformanceCategory");
                var ratingType = FindType(Ns + "PerformanceRating");
                bool mobile = platform != VrcPlatform.PC;

                var stats = Activator.CreateInstance(statsType, new object[] { mobile });
                var calc = perfType.GetMethod("CalculatePerformanceStats", new[] { typeof(string), typeof(GameObject), statsType, typeof(bool) });
                if (calc == null) throw new MissingMethodException("AvatarPerformance.CalculatePerformanceStats");
                calc.Invoke(null, new object[] { roots[0].name, roots[0], stats, mobile });

                var getRating = statsType.GetMethod("GetPerformanceRatingForCategory", new[] { categoryType });
                var displayName = statsType.GetMethod("GetPerformanceRatingDisplayName", BindingFlags.Public | BindingFlags.Static);
                var levelFor = statsType.GetMethod("GetStatLevelForRating", BindingFlags.Public | BindingFlags.Static);
                string Display(object rating) => displayName != null ? (string)displayName.Invoke(null, new[] { rating }) : rating.ToString();

                var overall = getRating.Invoke(stats, new[] { Enum.Parse(categoryType, "Overall") });
                result.rank = Display(overall);

                var rankNames = new[] { "Excellent", "Good", "Medium", "Poor" };
                var levels = rankNames.Select(n => levelFor?.Invoke(null, new object[] { Enum.Parse(ratingType, n), mobile })).ToArray();

                foreach (var name in Enum.GetNames(categoryType))
                {
                    if (name == "Overall" || name.EndsWith("Count", StringComparison.Ordinal) && name.StartsWith("AvatarPerformanceCategory", StringComparison.Ordinal)) continue;
                    var category = Enum.Parse(categoryType, name);
                    var rating = getRating.Invoke(stats, new[] { category });
                    var fieldName = char.ToLowerInvariant(name[0]) + name.Substring(1);
                    var value = ReadNumber(stats, fieldName);
                    var thresholds = levels.Select(l => l == null ? null : ReadNumber(l, fieldName)).ToArray();
                    var row = new RankStatRow
                    {
                        key = name,
                        label = name,
                        value = value ?? 0,
                        rank = Display(rating),
                        threshold = thresholds.All(t => t.HasValue) ? string.Join(" / ", thresholds.Select(t => t.Value.ToString(CultureInfo.InvariantCulture))) : "—",
                        excess = value.HasValue && thresholds[0].HasValue && value.Value > thresholds[0].Value
                            ? "+" + (value.Value - thresholds[0].Value).ToString(CultureInfo.InvariantCulture) : (value.HasValue && thresholds[0].HasValue ? "0" : "—"),
                    };
                    if (!value.HasValue) row.label += "（値は SDK 内部形式）";
                    result.rows.Add(row);
                }

                result.status = buildTimeToolsPresent ? RankStatus.Incomplete : RankStatus.Official;
                result.ruleSetId = "VRChat SDK " + (SdkInfo.Version("com.vrchat.avatars") ?? "?") + (mobile ? " (Mobile)" : " (PC)");
                result.reasons.Add("VRChat SDK の評価 API で算出（" + (mobile ? "モバイル閾値" : "PC 閾値") + "）。閾値列は Excellent / Good / Medium / Poor の上限。");
                if (platform == VrcPlatform.iOS)
                    result.reasons.Add("SDK の評価はモバイル共通（SDK 内の閾値リソースは Windows と Quest のみ）のため、iOS にも同じモバイル閾値を適用しています。");
                if (buildTimeToolsPresent)
                    result.reasons.Add("ビルド時変換ツールを検出。ビルド後の構成ではないため評価不完全です。");
            }
            catch (Exception e)
            {
                result.status = RankStatus.Unavailable;
                result.rank = "算出不可";
                result.reasons.Add("SDK 評価 API の呼び出しに失敗しました: " + (e.InnerException ?? e).Message);
            }
            return Finish(result);
        }

        static RankResult Finish(RankResult r)
        {
            r.statusLabel = r.status.Label();
            return r;
        }

        /// <summary>
        /// フィールド/プロパティの数値（int / float / Nullable）を読む。見つからなければ
        /// 先頭一致するフィールドの入れ子（例: PhysBoneComponentCount → physBone.componentCount）を探す。
        /// </summary>
        static double? ReadNumber(object obj, string name)
        {
            var direct = ReadDirect(obj, name);
            if (direct.HasValue) return direct;
            foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (name.Length <= f.Name.Length || !name.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var nested = f.GetValue(obj);
                if (nested == null || nested.GetType().IsPrimitive) continue;
                var rest = name.Substring(f.Name.Length);
                var value = ReadDirect(nested, char.ToLowerInvariant(rest[0]) + rest.Substring(1));
                if (value.HasValue) return value;
            }
            return null;
        }

        static double? ReadDirect(object obj, string name)
        {
            var type = obj.GetType();
            object v = type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj)
                       ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            switch (v)
            {
                case int i: return i;
                case float f: return f;
                case double d: return d;
                case long l: return l;
                default: return null;
            }
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
            var packageDir = AssetPathUtility.ToFullPath(UnityAssetsOptimizerInfo.PackageRoot + "/Editor/Platforms/Data/RankRules");
            if (packageDir != null) dirs.Add(packageDir);
            dirs.Add(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, "Assets", "UnityAssetsOptimizerSettings", "RankRules"));
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
                        Debug.LogWarning("[UnityAssetsOptimizer] 閾値 DB を読み込めません: " + file + " (" + e.Message + ")");
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
