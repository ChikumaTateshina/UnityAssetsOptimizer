using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.AssetsOptimizer
{
    public enum AtlasChannelKind { Color, Data, Normal }

    public sealed class AtlasChannel
    {
        public string Property;
        public AtlasChannelKind Kind;

        public AtlasChannel(string property, AtlasChannelKind kind)
        {
            Property = property;
            Kind = kind;
        }
    }

    /// <summary>プロパティ値の前提条件（満たさない場合はアトラス化しない）。</summary>
    public sealed class PropertyRule
    {
        public string Property;
        public float Expected;
        public bool VectorAllZero;
        public string Reason;

        public static PropertyRule Equals(string property, float expected, string reason) =>
            new PropertyRule { Property = property, Expected = expected, Reason = reason };

        public static PropertyRule Zero(string property, string reason) =>
            new PropertyRule { Property = property, VectorAllZero = true, Reason = reason };

        /// <summary>満たしていれば null、満たさなければ理由。プロパティを持たないシェーダーでは評価しない。</summary>
        public string Check(Material m)
        {
            if (!m.HasProperty(Property)) return null;
            if (VectorAllZero)
            {
                var v = m.GetVector(Property);
                return v == Vector4.zero ? null : Reason + "（" + Property + "=" + v + "）";
            }
            var f = m.GetFloat(Property);
            return Mathf.Approximately(f, Expected) ? null : Reason + "（" + Property + "=" + f.ToString(CultureInfo.InvariantCulture) + "）";
        }
    }

    public interface IShaderAdapter
    {
        string Id { get; }
        int Version { get; }
        /// <summary>検証に用いたソース（シェーダーのバージョン等）。</summary>
        string VerifiedAgainst { get; }
        bool Matches(Shader shader);
        /// <summary>UV0（＋メイン ST）でサンプルされ、アトラス化できるテクスチャ。</summary>
        IReadOnlyList<AtlasChannel> Channels { get; }
        /// <summary>UV に依存しない（MatCap・グラデーション等）ため、グループ内で同一であることを要求するテクスチャ。</summary>
        IReadOnlyList<string> SharedTextures { get; }
        /// <summary>値が設定されているとアトラス化できないテクスチャ（別 UV・別 ST を使うもの）。</summary>
        IReadOnlyList<string> ForbiddenTextures { get; }
        IReadOnlyList<PropertyRule> Rules { get; }
        /// <summary>このマテリアルが使用する頂点属性。不明な場合は null（属性削除を行わない）。</summary>
        HashSet<VertexAttribute> UsedAttributes(Material material);
    }

    public sealed class DataShaderAdapter : IShaderAdapter
    {
        readonly Func<Shader, bool> _matches;
        readonly Func<Material, HashSet<VertexAttribute>> _attributes;

        public string Id { get; }
        public int Version { get; }
        public string VerifiedAgainst { get; }
        public IReadOnlyList<AtlasChannel> Channels { get; }
        public IReadOnlyList<string> SharedTextures { get; }
        public IReadOnlyList<string> ForbiddenTextures { get; }
        public IReadOnlyList<PropertyRule> Rules { get; }

        public DataShaderAdapter(string id, int version, string verifiedAgainst, Func<Shader, bool> matches, AtlasChannel[] channels,
            string[] shared = null, string[] forbidden = null, PropertyRule[] rules = null, Func<Material, HashSet<VertexAttribute>> attributes = null)
        {
            Id = id;
            Version = version;
            VerifiedAgainst = verifiedAgainst;
            _matches = matches;
            Channels = channels;
            SharedTextures = shared ?? new string[0];
            ForbiddenTextures = forbidden ?? new string[0];
            Rules = rules ?? new PropertyRule[0];
            _attributes = attributes;
        }

        public bool Matches(Shader shader) => shader != null && _matches(shader);
        public HashSet<VertexAttribute> UsedAttributes(Material material) => _attributes?.Invoke(material);
    }

    /// <summary>
    /// 対応シェーダーの登録。各アダプターはシェーダーソースで UV の使われ方を確認したもののみ
    /// （Documentation~/CompatibilityMatrix.md 参照）。ここに無いシェーダー（Poiyomi 等）はアトラス化しない。
    /// </summary>
    public static class ShaderAdapterRegistry
    {
        static HashSet<VertexAttribute> Attrs(params VertexAttribute[] a) => new HashSet<VertexAttribute>(a);

        static readonly string[] LilToonShaderNames =
        {
            "lilToon", "Hidden/lilToonCutout", "Hidden/lilToonCutoutOutline", "Hidden/lilToonOutline",
            "Hidden/lilToonTransparent", "Hidden/lilToonTransparentOutline",
            "Hidden/lilToonOnePassTransparent", "Hidden/lilToonOnePassTransparentOutline",
            "Hidden/lilToonTwoPassTransparent", "Hidden/lilToonTwoPassTransparentOutline",
            "Hidden/lilToonLite", "Hidden/lilToonLiteCutout", "Hidden/lilToonLiteCutoutOutline", "Hidden/lilToonLiteOutline",
            "Hidden/lilToonLiteTransparent", "Hidden/lilToonLiteTransparentOutline",
            "Hidden/lilToonLiteOnePassTransparent", "Hidden/lilToonLiteOnePassTransparentOutline",
            "Hidden/lilToonLiteTwoPassTransparent", "Hidden/lilToonLiteTwoPassTransparentOutline",
        };

        static readonly List<IShaderAdapter> Adapters = new List<IShaderAdapter>
        {
            new DataShaderAdapter("unity.standard", 2, "Unity 2022.3 Built-in Standard", s => s.name == "Standard", new[]
            {
                new AtlasChannel("_MainTex", AtlasChannelKind.Color),
                new AtlasChannel("_MetallicGlossMap", AtlasChannelKind.Data),
                new AtlasChannel("_BumpMap", AtlasChannelKind.Normal),
                new AtlasChannel("_ParallaxMap", AtlasChannelKind.Data),
                new AtlasChannel("_OcclusionMap", AtlasChannelKind.Data),
                new AtlasChannel("_EmissionMap", AtlasChannelKind.Color),
                new AtlasChannel("_DetailMask", AtlasChannelKind.Data),
            }, forbidden: new[] { "_DetailAlbedoMap", "_DetailNormalMap" },
                attributes: m => Attrs(VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent,
                    VertexAttribute.TexCoord0, VertexAttribute.TexCoord1, VertexAttribute.TexCoord2)),
            new DataShaderAdapter("unity.standard-specular", 2, "Unity 2022.3 Built-in Standard (Specular setup)", s => s.name == "Standard (Specular setup)", new[]
            {
                new AtlasChannel("_MainTex", AtlasChannelKind.Color),
                new AtlasChannel("_SpecGlossMap", AtlasChannelKind.Data),
                new AtlasChannel("_BumpMap", AtlasChannelKind.Normal),
                new AtlasChannel("_ParallaxMap", AtlasChannelKind.Data),
                new AtlasChannel("_OcclusionMap", AtlasChannelKind.Data),
                new AtlasChannel("_EmissionMap", AtlasChannelKind.Color),
                new AtlasChannel("_DetailMask", AtlasChannelKind.Data),
            }, forbidden: new[] { "_DetailAlbedoMap", "_DetailNormalMap" },
                attributes: m => Attrs(VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent,
                    VertexAttribute.TexCoord0, VertexAttribute.TexCoord1, VertexAttribute.TexCoord2)),
            MainTexOnly("unity.unlit-texture", "Unlit/Texture", false),
            MainTexOnly("unity.unlit-transparent", "Unlit/Transparent", false),
            MainTexOnly("unity.unlit-transparent-cutout", "Unlit/Transparent Cutout", false),
            MainTexOnly("unity.legacy-diffuse", "Legacy Shaders/Diffuse", true),
            MainTexOnly("unity.mobile-diffuse", "Mobile/Diffuse", true),
            LilToon(),
        };

        public static IReadOnlyList<IShaderAdapter> All => Adapters;

        public static IShaderAdapter Find(Shader shader) => Adapters.FirstOrDefault(a => a.Matches(shader));

        static IShaderAdapter MainTexOnly(string id, string shaderName, bool lit) =>
            new DataShaderAdapter(id, 2, "Unity 2022.3 Built-in " + shaderName, s => s.name == shaderName,
                new[] { new AtlasChannel("_MainTex", AtlasChannelKind.Color) },
                attributes: m => lit
                    ? Attrs(VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.TexCoord0, VertexAttribute.TexCoord1)
                    : Attrs(VertexAttribute.Position, VertexAttribute.TexCoord0));

        /// <summary>
        /// lilToon 2.3.1 のシェーダーソース（Includes/lil_common_frag.hlsl ほか）で確認した内容:
        /// メイン UV（uv0 × _MainTex_ST + スクロール）でサンプルされるテクスチャのみアトラス化し、
        /// MatCap・グラデーション・ディザは UV 非依存のため同一性を要求する。
        /// サブ UV・独自 UV モード・UV アニメーション・UDIM・Decal・Glitter・AudioLink・Dissolve は非対応。
        /// </summary>
        static IShaderAdapter LilToon()
        {
            var names = new HashSet<string>(LilToonShaderNames);
            return new DataShaderAdapter("liltoon", 1, "lilToon 2.3.1", s => names.Contains(s.name), new[]
            {
                new AtlasChannel("_MainTex", AtlasChannelKind.Color),
                new AtlasChannel("_BaseMap", AtlasChannelKind.Color),
                new AtlasChannel("_BaseColorMap", AtlasChannelKind.Color),
                new AtlasChannel("_MainColorAdjustMask", AtlasChannelKind.Data),
                new AtlasChannel("_AlphaMask", AtlasChannelKind.Data),
                new AtlasChannel("_BumpMap", AtlasChannelKind.Normal),
                new AtlasChannel("_ShadowStrengthMask", AtlasChannelKind.Data),
                new AtlasChannel("_ShadowBorderMask", AtlasChannelKind.Data),
                new AtlasChannel("_ShadowBlurMask", AtlasChannelKind.Data),
                new AtlasChannel("_ShadowColorTex", AtlasChannelKind.Color),
                new AtlasChannel("_Shadow2ndColorTex", AtlasChannelKind.Color),
                new AtlasChannel("_Shadow3rdColorTex", AtlasChannelKind.Color),
                new AtlasChannel("_RimShadeMask", AtlasChannelKind.Data),
                new AtlasChannel("_BacklightColorTex", AtlasChannelKind.Color),
                new AtlasChannel("_SmoothnessTex", AtlasChannelKind.Data),
                new AtlasChannel("_MetallicGlossMap", AtlasChannelKind.Data),
                new AtlasChannel("_ReflectionColorTex", AtlasChannelKind.Color),
                new AtlasChannel("_MatCapBlendMask", AtlasChannelKind.Data),
                new AtlasChannel("_MatCapBumpMap", AtlasChannelKind.Normal),
                new AtlasChannel("_MatCap2ndBlendMask", AtlasChannelKind.Data),
                new AtlasChannel("_MatCap2ndBumpMap", AtlasChannelKind.Normal),
                new AtlasChannel("_RimColorTex", AtlasChannelKind.Color),
                new AtlasChannel("_EmissionMap", AtlasChannelKind.Color),
                new AtlasChannel("_EmissionBlendMask", AtlasChannelKind.Data),
                new AtlasChannel("_Emission2ndMap", AtlasChannelKind.Color),
                new AtlasChannel("_Emission2ndBlendMask", AtlasChannelKind.Data),
                new AtlasChannel("_OutlineTex", AtlasChannelKind.Color),
                new AtlasChannel("_OutlineWidthMask", AtlasChannelKind.Data),
                new AtlasChannel("_TriMask", AtlasChannelKind.Data),
            },
            shared: new[]
            {
                "_MatCapTex", "_MatCap2ndTex", "_MainGradationTex", "_EmissionGradTex", "_Emission2ndGradTex", "_DitherTex", "_Ramp",
            },
            forbidden: new[]
            {
                "_Main2ndTex", "_Main2ndBlendMask", "_Main2ndDissolveMask", "_Main2ndDissolveNoiseMask",
                "_Main3rdTex", "_Main3rdBlendMask", "_Main3rdDissolveMask", "_Main3rdDissolveNoiseMask",
                "_Bump2ndMap", "_Bump2ndScaleMask", "_AnisotropyTangentMap", "_AnisotropyScaleMask", "_AnisotropyShiftNoiseMask",
                "_GlitterColorTex", "_GlitterShapeTex", "_ParallaxMap", "_AudioLinkMask", "_AudioLinkLocalMap",
                "_DissolveMask", "_DissolveNoiseMask", "_OutlineVectorTex",
            },
            rules: new[]
            {
                PropertyRule.Zero("_MainTex_ScrollRotate", "メイン UV アニメーションが有効"),
                PropertyRule.Equals("_ShiftBackfaceUV", 0, "裏面 UV シフトが有効"),
                PropertyRule.Zero("_OutlineTex_ScrollRotate", "輪郭 UV アニメーションが有効"),
                PropertyRule.Equals("_EmissionMap_UVMode", 0, "発光マップが UV0 以外を使用"),
                PropertyRule.Zero("_EmissionMap_ScrollRotate", "発光マップの UV アニメーションが有効"),
                PropertyRule.Equals("_EmissionParallaxDepth", 0, "発光マップの視差が有効"),
                PropertyRule.Equals("_Emission2ndMap_UVMode", 0, "発光マップ2が UV0 以外を使用"),
                PropertyRule.Zero("_Emission2ndMap_ScrollRotate", "発光マップ2の UV アニメーションが有効"),
                PropertyRule.Equals("_Emission2ndParallaxDepth", 0, "発光マップ2の視差が有効"),
                PropertyRule.Equals("_UseMain2ndTex", 0, "メイン2nd（Decal 等）が有効"),
                PropertyRule.Equals("_UseMain3rdTex", 0, "メイン3rd（Decal 等）が有効"),
                PropertyRule.Equals("_UseBump2ndMap", 0, "ノーマルマップ2nd が有効"),
                PropertyRule.Equals("_UseGlitter", 0, "Glitter が有効（UV 依存）"),
                PropertyRule.Equals("_UseAudioLink", 0, "AudioLink が有効（UV 依存）"),
                PropertyRule.Equals("_UDIMDiscardCompile", 0, "UDIM Discard が有効"),
                PropertyRule.Equals("_UseParallax", 0, "視差マップが有効"),
            });
        }

        /// <summary>前提条件（禁止テクスチャ・プロパティ規則）を満たさない理由。満たしていれば null。</summary>
        public static string CheckPreconditions(IShaderAdapter adapter, Material m)
        {
            foreach (var forbidden in adapter.ForbiddenTextures)
                if (m.HasProperty(forbidden) && m.GetTexture(forbidden) != null)
                    return "アトラス非対応のテクスチャ（別UV/別ST/UV非依存）に値あり: " + forbidden;
            foreach (var rule in adapter.Rules)
            {
                var reason = rule.Check(m);
                if (reason != null) return reason;
            }
            return null;
        }
    }
}
