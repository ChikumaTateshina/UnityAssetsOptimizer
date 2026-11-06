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
        /// <summary>このテクスチャが設定されている場合のみ評価する前提条件（UV チャンネル選択・パン等）。</summary>
        public PropertyRule[] Rules;

        public AtlasChannel(string property, AtlasChannelKind kind, params PropertyRule[] rules)
        {
            Property = property;
            Kind = kind;
            Rules = rules ?? new PropertyRule[0];
        }
    }

    /// <summary>プロパティ値の前提条件（満たさない場合はアトラス化しない）。</summary>
    public sealed class PropertyRule
    {
        public string Property;
        public float Expected;
        public bool VectorAllZero;
        public bool VectorXY;
        public Vector2 ExpectedXY;
        public string Reason;

        public static PropertyRule Equals(string property, float expected, string reason) =>
            new PropertyRule { Property = property, Expected = expected, Reason = reason };

        public static PropertyRule Zero(string property, string reason) =>
            new PropertyRule { Property = property, VectorAllZero = true, Reason = reason };

        /// <summary>ベクトルの xy 成分のみを比較する（zw は未使用のプロパティ用）。</summary>
        public static PropertyRule XY(string property, float x, float y, string reason) =>
            new PropertyRule { Property = property, VectorXY = true, ExpectedXY = new Vector2(x, y), Reason = reason };

        /// <summary>満たしていれば null、満たさなければ理由。プロパティを持たないシェーダーでは評価しない。</summary>
        public string Check(Material m)
        {
            if (!m.HasProperty(Property)) return null;
            if (VectorXY)
            {
                var xy = (Vector2)m.GetVector(Property);
                return xy == ExpectedXY ? null : Reason + "（" + Property + "=" + xy + "）";
            }
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
            Poiyomi(),
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

        static readonly HashSet<string> PoiyomiShaderNames = new HashSet<string>
        {
            ".poiyomi/Poiyomi Toon", ".poiyomi/Poiyomi Toon Two Pass", ".poiyomi/Poiyomi Toon Outline Early", ".poiyomi/Poiyomi Toon World",
        };

        /// <summary>
        /// Poiyomi Toon 10.0.29（無料版）のシェーダーソースで確認した内容:
        /// 各テクスチャは poiUV(poiMesh.uv[&lt;名前&gt;UV], &lt;名前&gt;_ST) + パン でサンプルされ、poiMesh.uv[0] は
        /// 頂点 UV0 に全体 UV 設定（_UVSettings*0）と視差を適用したもの。よって UV チャンネル選択 = UV0・パン 0・
        /// Stochastic 無効・全体 UV 設定が単位値・視差/ピクセルモード/ビデオ効果/UDIM 無効の場合のみアトラス化する。
        /// MatCap・Toon Ramp・LUT 類は UV に依存しないため同一性を要求する。ロック済み（Hidden/Locked/...）は対象外。
        /// Grab Pass / Lil Fur 版と Poiyomi Pro は未検証のため対象外。
        /// </summary>
        static IShaderAdapter Poiyomi()
        {
            AtlasChannel P(string prop, AtlasChannelKind kind, string uv, string pan, string stoch)
            {
                var rules = new List<PropertyRule>();
                if (uv != null) rules.Add(PropertyRule.Equals(uv, 0, prop + " が UV0 以外を使用"));
                if (pan != null) rules.Add(PropertyRule.Zero(pan, prop + " の UV パンが有効"));
                if (stoch != null) rules.Add(PropertyRule.Equals(stoch, 0, prop + " の Stochastic サンプリングが有効"));
                if (prop.StartsWith("_EmissionMap", StringComparison.Ordinal))
                    rules.Add(PropertyRule.Equals("_EmissionCenterOutEnabled" + prop.Substring("_EmissionMap".Length), 0, prop + " の Center Out が有効（UV 非依存）"));
                return new AtlasChannel(prop, kind, rules.ToArray());
            }

            return new DataShaderAdapter("poiyomi-toon", 1, "Poiyomi Toon 10.0.29", s => PoiyomiShaderNames.Contains(s.name), new[]
            {
                P("_MainTex", AtlasChannelKind.Color, "_MainTexUV", "_MainTexPan", "_MainTexStochastic"),
                P("_OutlineMask", AtlasChannelKind.Color, "_OutlineMaskUV", "_OutlineMaskPan", null),
                P("_EmissionMap", AtlasChannelKind.Color, "_EmissionMapUV", "_EmissionMapPan", null),
                P("_EmissionMask", AtlasChannelKind.Color, "_EmissionMaskUV", "_EmissionMaskPan", null),
                P("_EmissionMap1", AtlasChannelKind.Color, "_EmissionMap1UV", "_EmissionMap1Pan", null),
                P("_EmissionMask1", AtlasChannelKind.Color, "_EmissionMask1UV", "_EmissionMask1Pan", null),
                P("_EmissionMap2", AtlasChannelKind.Color, "_EmissionMap2UV", "_EmissionMap2Pan", null),
                P("_EmissionMask2", AtlasChannelKind.Color, "_EmissionMask2UV", "_EmissionMask2Pan", null),
                P("_EmissionMap3", AtlasChannelKind.Color, "_EmissionMap3UV", "_EmissionMap3Pan", null),
                P("_EmissionMask3", AtlasChannelKind.Color, "_EmissionMask3UV", "_EmissionMask3Pan", null),
                P("_BumpMap", AtlasChannelKind.Normal, "_BumpMapUV", "_BumpMapPan", "_BumpMapStochastic"),
                P("_AlphaMask", AtlasChannelKind.Color, "_AlphaMaskUV", "_AlphaMaskPan", null),
                P("_MainColorAdjustTexture", AtlasChannelKind.Color, "_MainColorAdjustTextureUV", "_MainColorAdjustTexturePan", null),
                P("_MainTintTexture", AtlasChannelKind.Color, "_MainTintTextureUV", "_MainTintTexturePan", null),
                P("_Bump2ndMap", AtlasChannelKind.Normal, "_Bump2ndMapUV", "_Bump2ndMapPan", "_Bump2ndMapStochastic"),
                P("_Bump2ndScaleMask", AtlasChannelKind.Color, "_Bump2ndScaleMaskUV", "_Bump2ndScaleMaskPan", null),
                P("_BentNormalMap", AtlasChannelKind.Normal, "_BentNormalMapUV", "_BentNormalMapPan", "_BentNormalMapStochastic"),
                P("_DetailMask", AtlasChannelKind.Color, "_DetailMaskUV", "_DetailMaskPan", "_DetailMaskStochastic"),
                P("_DetailTex", AtlasChannelKind.Color, "_DetailTexUV", "_DetailTexPan", "_DetailTexStochastic"),
                P("_DetailNormalMap", AtlasChannelKind.Normal, "_DetailNormalMapUV", "_DetailNormalMapPan", "_DetailNormalMapStochastic"),
                P("_BackFaceTexture", AtlasChannelKind.Color, "_BackFaceTextureUV", "_BackFaceTexturePan", null),
                P("_BackFaceMask", AtlasChannelKind.Color, "_BackFaceMaskUV", "_BackFaceMaskPan", null),
                P("_RGBMask", AtlasChannelKind.Color, "_RGBMaskUV", "_RGBMaskPan", null),
                P("_DecalMask", AtlasChannelKind.Color, "_DecalMaskUV", "_DecalMaskPan", null),
                P("_LightingAOMaps", AtlasChannelKind.Color, "_LightingAOMapsUV", "_LightingAOMapsPan", null),
                P("_LightingDetailShadowMaps", AtlasChannelKind.Color, "_LightingDetailShadowMapsUV", "_LightingDetailShadowMapsPan", null),
                P("_LightingShadowMasks", AtlasChannelKind.Color, "_LightingShadowMasksUV", "_LightingShadowMasksPan", null),
                P("_ShadowColorTex", AtlasChannelKind.Color, "_ShadowColorTexUV", "_ShadowColorTexPan", null),
                P("_Shadow2ndColorTex", AtlasChannelKind.Color, "_Shadow2ndColorTexUV", "_Shadow2ndColorTexPan", null),
                P("_Shadow3rdColorTex", AtlasChannelKind.Color, "_Shadow3rdColorTexUV", "_Shadow3rdColorTexPan", null),
                P("_ShadowStrengthMask", AtlasChannelKind.Color, "_ShadowStrengthMaskUV", "_ShadowStrengthMaskPan", null),
                P("_MultilayerMathBlurMap", AtlasChannelKind.Color, "_MultilayerMathBlurMapUV", "_MultilayerMathBlurMapPan", null),
                P("_SkinThicknessMap", AtlasChannelKind.Color, "_SkinThicknessMapUV", "_SkinThicknessMapPan", null),
                P("_ClothMetallicSmoothnessMap", AtlasChannelKind.Color, "_ClothMetallicSmoothnessMapUV", "_ClothMetallicSmoothnessMapPan", null),
                P("_AnisoColorMap", AtlasChannelKind.Color, "_AnisoColorMapUV", "_AnisoColorMapPan", null),
                P("_MatcapMask", AtlasChannelKind.Color, "_MatcapMaskUV", "_MatcapMaskPan", null),
                P("_Matcap2Mask", AtlasChannelKind.Color, "_Matcap2MaskUV", "_Matcap2MaskPan", null),
                P("_Matcap3Mask", AtlasChannelKind.Color, "_Matcap3MaskUV", "_Matcap3MaskPan", null),
                P("_Matcap4Mask", AtlasChannelKind.Color, "_Matcap4MaskUV", "_Matcap4MaskPan", null),
                P("_CubeMapMask", AtlasChannelKind.Color, "_CubeMapMaskUV", "_CubeMapMaskPan", null),
                P("_RimMask", AtlasChannelKind.Color, "_RimMaskUV", "_RimMaskPan", null),
                P("_RimTex", AtlasChannelKind.Color, "_RimTexUV", "_RimTexPan", null),
                P("_RimColorTex", AtlasChannelKind.Color, "_RimColorTexUV", "_RimColorTexPan", null),
                P("_Rim2Mask", AtlasChannelKind.Color, "_Rim2MaskUV", "_Rim2MaskPan", null),
                P("_Rim2Tex", AtlasChannelKind.Color, "_Rim2TexUV", "_Rim2TexPan", null),
                P("_Rim2ColorTex", AtlasChannelKind.Color, "_Rim2ColorTexUV", "_Rim2ColorTexPan", null),
                P("_DepthRimMask", AtlasChannelKind.Color, "_DepthRimMaskUV", "_DepthRimMaskPan", null),
                P("_SSSThicknessMap", AtlasChannelKind.Color, "_SSSThicknessMapUV", "_SSSThicknessMapPan", null),
                P("_AnisotropyMap", AtlasChannelKind.Color, "_AnisotropyMapUV", "_AnisotropyMapPan", null),
                P("_AnisotropyDirectionMap", AtlasChannelKind.Color, "_AnisotropyDirectionMapUV", "_AnisotropyDirectionMapPan", null),
                P("_ClearCoatMaps", AtlasChannelKind.Color, "_ClearCoatMapsUV", "_ClearCoatMapsPan", "_ClearCoatMapsStochastic"),
                P("_SmoothnessTex", AtlasChannelKind.Color, "_SmoothnessTexUV", "_SmoothnessTexPan", null),
                P("_MetallicGlossMap", AtlasChannelKind.Color, "_MetallicGlossMapUV", "_MetallicGlossMapPan", null),
                P("_ReflectionColorTex", AtlasChannelKind.Color, "_ReflectionColorTexUV", "_ReflectionColorTexPan", null),
                P("_BacklightColorTex", AtlasChannelKind.Color, "_BacklightColorTexUV", "_BacklightColorTexPan", null),
                P("_OutlineTexture", AtlasChannelKind.Color, "_OutlineTextureUV", "_OutlineTexturePan", null),
                P("_DissolveToTexture", AtlasChannelKind.Color, "_DissolveToTextureUV", "_DissolveToTexturePan", null),
                P("_DissolveMask", AtlasChannelKind.Color, "_DissolveMaskUV", "_DissolveMaskPan", null),
                P("_FlipbookMask", AtlasChannelKind.Color, "_FlipbookMaskUV", "_FlipbookMaskPan", null),
                P("_FlipbookMask1", AtlasChannelKind.Color, "_FlipbookMaskUV1", "_FlipbookMaskPan1", null),
                P("_GlitterMask", AtlasChannelKind.Color, "_GlitterMaskUV", "_GlitterMaskPan", null),
                P("_PathingMap", AtlasChannelKind.Color, "_PathingMapUV", "_PathingMapPan", null),
                P("_PathingColorMap", AtlasChannelKind.Color, "_PathingColorMapUV", "_PathingColorMapPan", null),
                P("_MirrorTexture", AtlasChannelKind.Color, "_MirrorTextureUV", "_MirrorTexturePan", null),
                P("_DepthMask", AtlasChannelKind.Color, "_DepthMaskUV", "_DepthMaskPan", null),
                P("_ParallaxInternalMapMask", AtlasChannelKind.Color, "_ParallaxInternalMapMaskUV", "_ParallaxInternalMapMaskPan", null),
                P("_VideoMaskTexture", AtlasChannelKind.Color, "_VideoMaskTextureUV", "_VideoMaskTexturePan", null),
                P("_VoronoiNoise", AtlasChannelKind.Color, "_VoronoiNoiseUV", "_VoronoiNoisePan", null),
                P("_VoronoiMask", AtlasChannelKind.Color, "_VoronoiMaskUV", "_VoronoiMaskPan", null),
                P("_ALDecalColorMask", AtlasChannelKind.Color, "_ALDecalColorMaskUV", "_ALDecalColorMaskPan", null),
                P("_PPMask", AtlasChannelKind.Color, "_PPMaskUV", "_PPMaskPan", null)
            },
            // UV に依存しないルックアップ（シェーダー既定で割り当てられるものを含む）。歪みフローは Distorted UV（UV0 必須の対象では未使用）
            shared: new[]
            {
                "_Matcap", "_Matcap2", "_Matcap3", "_Matcap4", "_ToonRamp", "_PoiSkinLUT", "_SkinLUT", "_ClothDFG",
                "_TextGlyphs", "_DistortionFlowTexture", "_DistortionFlowTexture1",
            },
            rules: new[]
            {
                PropertyRule.XY("_UVSettingsTiling0", 1, 1, "UV0 の全体 Tiling が単位値でない"),
                PropertyRule.Zero("_UVSettingsOffset0", "UV0 の全体 Offset が有効"),
                PropertyRule.Equals("_UVSettingsAngle0", 0, "UV0 の全体回転が有効"),
                PropertyRule.Zero("_UVSettingsPan0", "UV0 の全体パンが有効"),
                PropertyRule.Equals("_UVSettingsRotate0", 0, "UV0 の回転アニメーションが有効"),
                PropertyRule.Equals("_PoiParallax", 0, "視差（UV0 を変更）が有効"),
                PropertyRule.Equals("_MainPixelMode", 0, "Pixel Mode（テクセル単位のスナップ）が有効"),
                PropertyRule.Equals("_VideoEffectsEnable", 0, "Video Effects（_MainTex を特殊サンプル）が有効"),
                PropertyRule.Equals("_EnableUDIMDiscardOptions", 0, "UDIM Discard が有効"),
                PropertyRule.Equals("_TextEnabled", 0, "Text（UV 上にグリフを描画）が有効"),
                PropertyRule.Equals("_EnableDistortion", 0, "UV Distortion が有効"),
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
