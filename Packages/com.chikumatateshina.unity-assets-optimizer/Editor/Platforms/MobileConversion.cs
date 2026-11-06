using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    public enum RunKind { Optimize, Android, iOS }

    /// <summary>
    /// モバイル向けシェーダー変換規則。変換先プロパティは VRChat SDK 3.9.0 同梱シェーダーのソースで確認したもの。
    /// Equivalent=false の規則は外観が変わるため AUTO では実行しない（FORCE のみ）。
    /// </summary>
    public sealed class ShaderConversionRule
    {
        public string Name;
        public Func<Material, bool> Matches;
        public string TargetShader;
        public Func<Material, bool> IsEquivalent;
        public string NonEquivalentReason;
        public Action<Material, Material> Copy;
    }

    public static class MobileShaderRules
    {
        static bool IsOpaqueStandard(Material m) =>
            m.renderQueue < 2450 && !m.IsKeywordEnabled("_ALPHATEST_ON") && !m.IsKeywordEnabled("_ALPHABLEND_ON") && !m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON");

        static void CopyTex(Material s, Material d, string from, string to = null)
        {
            to = to ?? from;
            if (!s.HasProperty(from) || !d.HasProperty(to)) return;
            d.SetTexture(to, s.GetTexture(from));
            d.SetTextureScale(to, s.GetTextureScale(from));
            d.SetTextureOffset(to, s.GetTextureOffset(from));
        }

        static void CopyColor(Material s, Material d, string from, string to = null)
        {
            to = to ?? from;
            if (s.HasProperty(from) && d.HasProperty(to)) d.SetColor(to, s.GetColor(from));
        }

        static void CopyFloat(Material s, Material d, string from, string to = null)
        {
            to = to ?? from;
            if (s.HasProperty(from) && d.HasProperty(to)) d.SetFloat(to, s.GetFloat(from));
        }

        static readonly HashSet<string> LilToonOpaque = new HashSet<string> { "lilToon", "Hidden/lilToonOutline", "Hidden/lilToonLite", "Hidden/lilToonLiteOutline" };

        public static readonly IReadOnlyList<ShaderConversionRule> All = new[]
        {
            new ShaderConversionRule
            {
                Name = "Standard → VRChat/Mobile/Standard Lite",
                Matches = m => m.shader.name == "Standard" && IsOpaqueStandard(m),
                TargetShader = "VRChat/Mobile/Standard Lite",
                IsEquivalent = m => m.GetTexture("_ParallaxMap") == null && m.GetTexture("_DetailAlbedoMap") == null &&
                                    m.GetTexture("_DetailNormalMap") == null && Mathf.Approximately(m.GetFloat("_SmoothnessTextureChannel"), 0),
                NonEquivalentReason = "視差/Detail マップまたはアルベドα平滑度を使用（Standard Lite 非対応）",
                Copy = (s, d) =>
                {
                    CopyTex(s, d, "_MainTex"); CopyColor(s, d, "_Color");
                    CopyTex(s, d, "_MetallicGlossMap"); CopyFloat(s, d, "_Metallic"); CopyFloat(s, d, "_Glossiness");
                    CopyTex(s, d, "_BumpMap"); CopyFloat(s, d, "_BumpScale");
                    CopyTex(s, d, "_OcclusionMap"); CopyFloat(s, d, "_OcclusionStrength");
                    CopyTex(s, d, "_EmissionMap"); CopyColor(s, d, "_EmissionColor");
                    if (s.IsKeywordEnabled("_EMISSION")) d.EnableKeyword("_EMISSION");
                },
            },
            new ShaderConversionRule
            {
                Name = "Legacy/Mobile Diffuse → VRChat/Mobile/Diffuse",
                Matches = m => (m.shader.name == "Legacy Shaders/Diffuse" || m.shader.name == "Mobile/Diffuse") && m.renderQueue < 2450,
                TargetShader = "VRChat/Mobile/Diffuse",
                IsEquivalent = m => !m.HasProperty("_Color") || m.GetColor("_Color") == Color.white,
                NonEquivalentReason = "_Color が白以外（変換先に色指定なし）",
                Copy = (s, d) => CopyTex(s, d, "_MainTex"),
            },
            new ShaderConversionRule
            {
                Name = "Unlit/Texture → VRChat/Mobile/Toon Lit",
                Matches = m => m.shader.name == "Unlit/Texture",
                TargetShader = "VRChat/Mobile/Toon Lit",
                IsEquivalent = m => false,
                NonEquivalentReason = "Unlit → Lit（ライティングの影響を受けるようになる）",
                Copy = (s, d) => CopyTex(s, d, "_MainTex"),
            },
            new ShaderConversionRule
            {
                Name = "lilToon（不透明） → VRChat/Mobile/Toon Standard",
                Matches = m => LilToonOpaque.Contains(m.shader.name),
                TargetShader = "VRChat/Mobile/Toon Standard",
                IsEquivalent = m => false,
                NonEquivalentReason = "lilToon の影・MatCap・輪郭・リム等は再現されない（主要テクスチャと色のみ移行）",
                Copy = (s, d) =>
                {
                    CopyTex(s, d, "_MainTex"); CopyColor(s, d, "_Color");
                    CopyTex(s, d, "_BumpMap"); CopyFloat(s, d, "_BumpScale");
                    if (s.HasProperty("_BumpMap") && s.GetTexture("_BumpMap") != null && s.HasProperty("_UseBumpMap") && s.GetFloat("_UseBumpMap") > 0)
                        d.EnableKeyword("USE_NORMAL_MAPS");
                    if (s.HasProperty("_UseEmission") && s.GetFloat("_UseEmission") > 0)
                    {
                        CopyTex(s, d, "_EmissionMap");
                        CopyColor(s, d, "_EmissionColor");
                    }
                    CopyFloat(s, d, "_Cull", "_Culling");
                },
            },
        };

        public static ShaderConversionRule Find(Material m) => m == null || m.shader == null ? null : All.FirstOrDefault(r => r.Matches(m));
    }

    /// <summary>VRChat SDK のアバター検証 API（VRC.SDK3.Validation.AvatarValidation）をリフレクションで呼ぶ。</summary>
    public static class SdkAvatarValidation
    {
        static Type Type => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("VRC.SDK3.Validation.AvatarValidation", false)).FirstOrDefault(t => t != null);

        public static bool Available => Type?.GetMethod("FindIllegalShaders") != null;

        /// <summary>モバイルで許可されたシェーダー一覧（VRC.SDKBase.Validation.AvatarValidation.ShaderWhiteList）。</summary>
        public static string[] ShaderWhiteList()
        {
            var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDKBase.Validation.AvatarValidation", false)).FirstOrDefault(x => x != null);
            return t?.GetField("ShaderWhiteList")?.GetValue(null) as string[];
        }

        public static List<Shader> FindIllegalShaders(GameObject root)
        {
            var result = Type.GetMethod("FindIllegalShaders").Invoke(null, new object[] { root }) as IEnumerable<Shader>;
            return result?.Where(s => s != null).Distinct().ToList() ?? new List<Shader>();
        }

        public static List<Component> FindIllegalComponents(GameObject root)
        {
            var result = Type.GetMethod("FindIllegalComponents")?.Invoke(null, new object[] { root }) as IEnumerable<Component>;
            return result?.Where(c => c != null).ToList() ?? new List<Component>();
        }
    }

    [Serializable]
    public class ConversionReport : ISerializationCallbackReceiver
    {
        public string platform;
        public ConversionState state;
        [SerializeField] string stateName;
        /// <summary>実機検証済みかは別ステータス（本ツールは実機検証を行わない）。</summary>
        public bool deviceVerified;
        public List<string> reasons = new List<string>();
        public List<string> illegalShaders = new List<string>();
        public List<string> removedBySdk = new List<string>();

        public string StateLabel
        {
            get
            {
                switch (state)
                {
                    case ConversionState.Verified: return "完了（SDK検証済）";
                    case ConversionState.Partial: return "部分変換";
                    default: return "変換不可";
                }
            }
        }

        public void OnBeforeSerialize() { stateName = state.ToString(); }
        public void OnAfterDeserialize() { }
    }

    /// <summary>モバイル向けシェーダー変換（Material をコピーして変換先シェーダーへ移行する）。</summary>
    public sealed class MobileShaderConversionOperation : OptimizationOperationBase
    {
        public const string OperationId = "mobile-shader-conversion";
        readonly VrcPlatform _platform;

        public MobileShaderConversionOperation(VrcPlatform platform) { _platform = platform; }

        public override string Id => OperationId;
        public override string DisplayName => _platform + " シェーダー変換";

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            CanForce = true,
            Phase = 5,
            WritesAssetKinds = new[] { "Material" },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var whitelist = SdkAvatarValidation.ShaderWhiteList() ?? new string[0];
            bool avatar = context.Settings.preset == ContentPreset.Avatar;
            var map = new Dictionary<PlanItem, (Material, ShaderConversionRule)>();
            var materials = context.Analysis.Renderers.SelectMany(r => r.Materials).Where(m => m != null).Distinct()
                .OrderBy(MaterialEquivalence.StableId, StringComparer.Ordinal);
            foreach (var m in materials)
            {
                var label = m.name + "（" + (m.shader != null ? m.shader.name : "none") + "）";
                if (m.shader != null && whitelist.Contains(m.shader.name))
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "既にモバイル用シェーダー（変換不要）"));
                    continue;
                }
                var rule = MobileShaderRules.Find(m);
                if (rule == null)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported,
                        avatar ? "対応する変換規則がありません（透明/未対応シェーダー）。モバイルアバターでは使用できません" : "対応する変換規則なし（ワールドでは元シェーダーを維持）"));
                    continue;
                }
                if (Shader.Find(rule.TargetShader) == null)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "変換先シェーダー " + rule.TargetShader + " が見つかりません（VRChat SDK 未導入）"));
                    continue;
                }
                var item = rule.IsEquivalent(m)
                    ? PlanItem.Supported(label, rule.Name)
                    : PlanItem.Skip(label, SupportState.Unknown, rule.Name + ": 非等価変換 — " + rule.NonEquivalentReason, true);
                analysis.Items.Add(item);
                map[item] = (m, rule);
            }
            analysis.Data = map;
            analysis.Notes.Add("非等価な変換（外観が変わる）は AUTO では行いません。Manual Override の FORCE で個別承認して実行できます。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var map = (Dictionary<PlanItem, (Material material, ShaderConversionRule rule)>)plan.Data;
            var converted = new Dictionary<Material, Material>();
            foreach (var pair in map.Where(p => p.Key.included))
            {
                var (src, rule) = pair.Value;
                var dst = new Material(Shader.Find(rule.TargetShader)) { name = src.name + "_" + _platform };
                rule.Copy(src, dst);
                dst.renderQueue = -1;
                converted[src] = gen.AddAsset(dst, "Materials", dst.name, Id, MaterialEquivalence.StableId(src));
            }
            foreach (var r in gen.Copy.AllRenderers.Select(p => p.Value))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && converted.TryGetValue(mats[i], out var c))
                    {
                        mats[i] = c;
                        changed = true;
                    }
                if (changed) r.sharedMaterials = mats;
            }
        }
    }

    /// <summary>対象プラットフォーム用の ASTC オーバーライドを、テクスチャのコピーに設定する（PC 設定は変更しない）。</summary>
    public sealed class MobileTextureOperation : OptimizationOperationBase
    {
        public const string OperationId = "mobile-texture";
        readonly VrcPlatform _platform;

        public MobileTextureOperation(VrcPlatform platform) { _platform = platform; }

        public override string Id => OperationId;
        public override string DisplayName => _platform + " テクスチャ設定";
        string ImporterPlatform => _platform == VrcPlatform.iOS ? "iPhone" : "Android";

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 5,
            RunsAfter = new[] { MobileShaderConversionOperation.OperationId },
            WritesAssetKinds = new[] { "Texture2D(copy)" },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            foreach (var tex in TextureImportOperationBase.ReferencedOriginalTextures(context.Analysis.Renderers.SelectMany(r => r.Materials)))
            {
                var path = AssetDatabase.GetAssetPath(tex);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                if (importer.GetPlatformTextureSettings(ImporterPlatform).overridden)
                    analysis.Items.Add(PlanItem.Skip(path, SupportState.Unsupported, ImporterPlatform + " のオーバーライド設定済み"));
                else
                    analysis.Items.Add(PlanItem.Supported(path, "ASTC " + context.Settings.mobileAstcBlock + "x" + context.Settings.mobileAstcBlock));
            }
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            var materials = gen.Copy.AllRenderers.SelectMany(p => p.Value.sharedMaterials);
            foreach (var tex in TextureImportOperationBase.ReferencedOriginalTextures(materials).Where(t => !gen.IsGenerated(t)))
            {
                if (!included.Contains(AssetDatabase.GetAssetPath(tex))) continue;
                var record = gen.GetOrPlanTextureCopy(tex, Id);
                if (record == null) continue;
                record.Import.MobileAstcBlock = gen.Settings.mobileAstcBlock;
                record.Import.MobilePlatforms = new[] { ImporterPlatform };
            }
        }
    }

    public static class MobileConversionValidator
    {
        /// <summary>
        /// 変換結果の状態を判定する。アバターは SDK のシェーダー検証 API で確認できた場合のみ Verified。
        /// ワールドは SDK の検証 API に接続していないため Partial を上限とする。
        /// </summary>
        public static ConversionReport Evaluate(IList<GameObject> roots, VrcPlatform platform, ContentPreset content, IEnumerable<OperationPlan> plans)
        {
            var report = new ConversionReport { platform = platform.ToString() };
            var shaderPlan = plans.FirstOrDefault(p => p.Decision.operationId == MobileShaderConversionOperation.OperationId);
            int skipped = shaderPlan?.Items.Count(i => !i.included && i.reason != null && !i.reason.StartsWith("既にモバイル", StringComparison.Ordinal)) ?? 0;

            if (content == ContentPreset.World)
            {
                report.state = ConversionState.Partial;
                report.reasons.Add("ワールドの SDK 検証/ビルド API には接続していないため、SDK 検証済みとは表示しません。");
                if (skipped > 0) report.reasons.Add("変換しなかった Material " + skipped + " 個（元シェーダーを維持）。");
                return report;
            }
            if (!SdkAvatarValidation.Available)
            {
                report.state = ConversionState.Incompatible;
                report.reasons.Add("VRChat Avatars SDK の検証 API が見つからないため検証できません。");
                return report;
            }
            if (roots.Count != 1)
            {
                report.state = ConversionState.Partial;
                report.reasons.Add("アバターは1体ずつ検証します。");
                return report;
            }
            var illegal = SdkAvatarValidation.FindIllegalShaders(roots[0]);
            report.illegalShaders.AddRange(illegal.Select(s => s.name));
            report.removedBySdk.AddRange(SdkAvatarValidation.FindIllegalComponents(roots[0]).Select(c => AssetPathUtility.DisplayPath(c.gameObject) + " (" + c.GetType().Name + ")"));
            if (illegal.Count == 0)
            {
                report.state = ConversionState.Verified;
                report.reasons.Add("SDK のシェーダー検証（AvatarValidation.FindIllegalShaders）で問題なし。");
            }
            else
            {
                bool anyConverted = shaderPlan != null && shaderPlan.Items.Any(i => i.included);
                report.state = anyConverted ? ConversionState.Partial : ConversionState.Incompatible;
                report.reasons.Add("モバイルで使用できないシェーダーが残っています: " + string.Join(", ", report.illegalShaders));
            }
            if (report.removedBySdk.Count > 0)
                report.reasons.Add("SDK がビルド時に削除するコンポーネント " + report.removedBySdk.Count + " 個（ギミックが動作しない可能性）。本ツールは削除しません。");
            report.reasons.Add("実機検証は未実施です（別ステータス）。");
            return report;
        }
    }
}
