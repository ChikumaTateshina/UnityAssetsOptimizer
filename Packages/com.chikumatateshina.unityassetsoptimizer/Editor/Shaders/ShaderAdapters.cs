using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public enum AtlasChannelKind { Color, Data, Normal }

    public sealed class AtlasChannel
    {
        public string Property;
        public AtlasChannelKind Kind;
        /// <summary>このテクスチャが設定されている場合のみ評価する前提条件（UV チャンネル選択・パン等）。</summary>
        public PropertyRule[] Rules;
        /// <summary>
        /// 一部の Material にだけテクスチャがある場合に、ない Material の領域をシェーダーの既定テクスチャ（未割り当て時と同じ値）で埋めて統合できるか。
        /// テクスチャの有無でシェーダーキーワードの処理が切り替わる（既定テクスチャと同じ結果にならない）ものは false。
        /// </summary>
        public bool Fillable = true;
        /// <summary>テクスチャがある Material に付くキーワード（統合時、どれかにテクスチャがあればアトラス Material で有効にする）。</summary>
        public string KeywordWhenPresent;

        public AtlasChannel(string property, AtlasChannelKind kind, params PropertyRule[] rules)
        {
            Property = property;
            Kind = kind;
            Rules = rules ?? new PropertyRule[0];
        }

        public AtlasChannel NotFillable()
        {
            Fillable = false;
            return this;
        }

        public AtlasChannel WithKeyword(string keyword)
        {
            KeywordWhenPresent = keyword;
            return this;
        }
    }

    /// <summary>
    /// 数値プロパティのテクスチャへの焼き込み（例: Standard の Metallic/Smoothness → _MetallicGlossMap）。
    /// テクスチャのない Material は数値から作った単色、ある Material はアルファに倍率を掛けた画像にして、アトラス Material ではテクスチャを使う設定にする。
    /// </summary>
    public sealed class ParameterBakeSpec
    {
        public string Texture;
        /// <summary>焼き込める Material か（例: Smoothness の取得元がこのテクスチャであること）。</summary>
        public Func<Material, bool> Applicable;
        /// <summary>テクスチャがない場合の値（そのままテクスチャに書く値）。</summary>
        public Func<Material, Color> SolidValue;
        /// <summary>テクスチャがある場合にアルファへ掛ける倍率。</summary>
        public Func<Material, float> AlphaScale;
        public bool SRgb;
        public string[] ExcludeProperties = new string[0];
        public string[] ExcludeKeywords = new string[0];
        public Action<Material> Apply;
    }

    /// <summary>
    /// 発光の焼き込み: 発光 = Texture × ColorProperty（HDR 可）。無効な Material は黒（アルファ 0）として扱う。
    /// HDR の強さはアトラス Material の色（白 × 最大強度）に移し、各領域にはそれで割った色を焼き込む。
    /// </summary>
    public sealed class EmissionBakeSpec
    {
        public string Texture;
        public string ColorProperty;
        public Func<Material, bool> Enabled;
        /// <summary>焼き込むため等価性判定から外すプロパティ・キーワード。</summary>
        public string[] ExcludeProperties = new string[0];
        public string[] ExcludeKeywords = new string[0];
        /// <summary>Global Illumination フラグ（発光の有無で変わる）を等価性判定から外す。</summary>
        public bool ExcludeGlobalIllumination;
        /// <summary>アトラス Material で発光を有効にし、色を設定する（reference は発光が有効だった Material）。</summary>
        public Action<Material, Color, Material> Apply;
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
        /// <summary>メインテクスチャに乗算されるメインカラー（アトラスへ焼き込める場合）。なければ null。</summary>
        string MainColorProperty { get; }
        /// <summary>メインカラーが乗算されるテクスチャ。</summary>
        string MainColorTexture { get; }
        /// <summary>メインカラーを焼き込めない理由（シェーダー固有の条件）。焼き込めるなら null。</summary>
        string ColorBakeBlocker(Material material);
        /// <summary>発光の焼き込み（対応しない場合は null）。</summary>
        EmissionBakeSpec EmissionBake { get; }
        /// <summary>数値プロパティの焼き込み。</summary>
        IReadOnlyList<ParameterBakeSpec> ParameterBakes { get; }
        /// <summary>メインテクスチャのアルファが描画に使われない Material か（すべてそうならアトラスを不透明にして圧縮を軽くする）。</summary>
        bool MainAlphaUnused(Material material);
    }

    public sealed class DataShaderAdapter : IShaderAdapter
    {
        readonly Func<Shader, bool> _matches;
        readonly Func<Material, HashSet<VertexAttribute>> _attributes;
        readonly Func<Material, string> _colorBakeBlocker;

        public string Id { get; }
        public int Version { get; }
        public string VerifiedAgainst { get; }
        public IReadOnlyList<AtlasChannel> Channels { get; }
        public IReadOnlyList<string> SharedTextures { get; }
        public IReadOnlyList<string> ForbiddenTextures { get; }
        public IReadOnlyList<PropertyRule> Rules { get; }
        public string MainColorProperty { get; }
        public string MainColorTexture { get; }
        public EmissionBakeSpec EmissionBake { get; }
        public IReadOnlyList<ParameterBakeSpec> ParameterBakes { get; }
        readonly Func<Material, bool> _mainAlphaUnused;

        public DataShaderAdapter(string id, int version, string verifiedAgainst, Func<Shader, bool> matches, AtlasChannel[] channels,
            string[] shared = null, string[] forbidden = null, PropertyRule[] rules = null, Func<Material, HashSet<VertexAttribute>> attributes = null,
            string mainColor = null, string mainColorTexture = "_MainTex", Func<Material, string> colorBakeBlocker = null, EmissionBakeSpec emissionBake = null,
            ParameterBakeSpec[] parameterBakes = null, Func<Material, bool> mainAlphaUnused = null)
        {
            EmissionBake = emissionBake;
            ParameterBakes = parameterBakes ?? new ParameterBakeSpec[0];
            _mainAlphaUnused = mainAlphaUnused;
            MainColorProperty = mainColor;
            MainColorTexture = mainColorTexture;
            _colorBakeBlocker = colorBakeBlocker;
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
        public string ColorBakeBlocker(Material material) => _colorBakeBlocker?.Invoke(material);
        public bool MainAlphaUnused(Material material) => _mainAlphaUnused != null && _mainAlphaUnused(material);
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
            // Standard: _METALLICGLOSSMAP / _PARALLAXMAP / _DETAIL_MULX2 はテクスチャの有無で数値プロパティとの切り替えが起きるため埋められない。
            // _NORMALMAP は平らなノーマル、_OcclusionMap は白（未割り当て時の既定値）で埋めても結果が同じ
            new DataShaderAdapter("unity.standard", 3, "Unity 2022.3 Built-in Standard", s => s.name == "Standard", new[]
            {
                new AtlasChannel("_MainTex", AtlasChannelKind.Color),
                new AtlasChannel("_MetallicGlossMap", AtlasChannelKind.Data).NotFillable(),
                new AtlasChannel("_BumpMap", AtlasChannelKind.Normal).WithKeyword("_NORMALMAP"),
                new AtlasChannel("_ParallaxMap", AtlasChannelKind.Data).NotFillable(),
                new AtlasChannel("_OcclusionMap", AtlasChannelKind.Data),
                new AtlasChannel("_EmissionMap", AtlasChannelKind.Color),
                new AtlasChannel("_DetailMask", AtlasChannelKind.Data).NotFillable(),
            }, forbidden: new[] { "_DetailAlbedoMap", "_DetailNormalMap" },
                attributes: m => Attrs(VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent,
                    VertexAttribute.TexCoord0, VertexAttribute.TexCoord1, VertexAttribute.TexCoord2),
                mainColor: "_Color", emissionBake: StandardEmission(),
                parameterBakes: new[] { StandardGlossBake("_MetallicGlossMap", "_METALLICGLOSSMAP", m => { var v = m.GetFloat("_Metallic"); return new Color(v, v, v, m.GetFloat("_Glossiness")); }, true, "_Metallic") },
                mainAlphaUnused: StandardOpaqueAlphaUnused),
            new DataShaderAdapter("unity.standard-specular", 3, "Unity 2022.3 Built-in Standard (Specular setup)", s => s.name == "Standard (Specular setup)", new[]
            {
                new AtlasChannel("_MainTex", AtlasChannelKind.Color),
                new AtlasChannel("_SpecGlossMap", AtlasChannelKind.Data).NotFillable(),
                new AtlasChannel("_BumpMap", AtlasChannelKind.Normal).WithKeyword("_NORMALMAP"),
                new AtlasChannel("_ParallaxMap", AtlasChannelKind.Data).NotFillable(),
                new AtlasChannel("_OcclusionMap", AtlasChannelKind.Data),
                new AtlasChannel("_EmissionMap", AtlasChannelKind.Color),
                new AtlasChannel("_DetailMask", AtlasChannelKind.Data).NotFillable(),
            }, forbidden: new[] { "_DetailAlbedoMap", "_DetailNormalMap" },
                attributes: m => Attrs(VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent,
                    VertexAttribute.TexCoord0, VertexAttribute.TexCoord1, VertexAttribute.TexCoord2),
                mainColor: "_Color", emissionBake: StandardEmission(),
                parameterBakes: new[] { StandardGlossBake("_SpecGlossMap", "_SPECGLOSSMAP", m => { var c = m.GetColor("_SpecColor"); return new Color(c.r, c.g, c.b, m.GetFloat("_Glossiness")); }, true, "_SpecColor") },
                mainAlphaUnused: StandardOpaqueAlphaUnused),
            MainTexOnly("unity.unlit-texture", "Unlit/Texture", false),
            MainTexOnly("unity.unlit-transparent", "Unlit/Transparent", false),
            MainTexOnly("unity.unlit-transparent-cutout", "Unlit/Transparent Cutout", false),
            MainTexOnly("unity.legacy-diffuse", "Legacy Shaders/Diffuse", true, "_Color"),
            MainTexOnly("unity.mobile-diffuse", "Mobile/Diffuse", true),
            LilToon(),
            Poiyomi(),
        };

        public static IReadOnlyList<IShaderAdapter> All => Adapters;

        public static IShaderAdapter Find(Shader shader) => Adapters.FirstOrDefault(a => a.Matches(shader));

        /// <summary>Standard: _EMISSION キーワードが有効なら 発光 = _EmissionMap（既定は白）× _EmissionColor（HDR）。</summary>
        static EmissionBakeSpec StandardEmission() => new EmissionBakeSpec
        {
            Texture = "_EmissionMap",
            ColorProperty = "_EmissionColor",
            Enabled = m => m.IsKeywordEnabled("_EMISSION"),
            ExcludeProperties = new[] { "_EmissionColor" },
            ExcludeKeywords = new[] { "_EMISSION" },
            ExcludeGlobalIllumination = true,
            Apply = (atlas, color, reference) =>
            {
                atlas.EnableKeyword("_EMISSION");
                atlas.SetColor("_EmissionColor", color);
                atlas.globalIlluminationFlags = reference.globalIlluminationFlags;
            },
        };

        /// <summary>
        /// _Metallic は [Gamma]、_SpecColor は色のため、数値から作る単色はガンマ空間の値（sRGB テクスチャと同じ扱い）として書く。
        /// Standard（UnityStandardInput.cginc の MetallicGloss / SpecularGloss）: キーワードがあれば 値 = map.rgb、Smoothness = map.a × _GlossMapScale、
        /// なければ数値（_Metallic または _SpecColor と _Glossiness）。Smoothness の取得元がこのテクスチャ（_SmoothnessTextureChannel = 0）の場合のみ焼き込む。
        /// </summary>
        static ParameterBakeSpec StandardGlossBake(string texture, string keyword, Func<Material, Color> solid, bool srgb, string valueProperty) => new ParameterBakeSpec
        {
            Texture = texture,
            Applicable = m => !m.HasProperty("_SmoothnessTextureChannel") || m.GetFloat("_SmoothnessTextureChannel") < 0.5f,
            SolidValue = solid,
            AlphaScale = m => m.HasProperty("_GlossMapScale") ? m.GetFloat("_GlossMapScale") : 1f,
            SRgb = srgb,
            ExcludeProperties = new[] { valueProperty, "_Glossiness", "_GlossMapScale" },
            ExcludeKeywords = new[] { keyword },
            Apply = atlas =>
            {
                atlas.EnableKeyword(keyword);
                atlas.SetFloat("_GlossMapScale", 1f);
            },
        };

        /// <summary>Standard の Opaque で Smoothness をアルファから取らない場合、メインテクスチャのアルファは使われない。</summary>
        static bool StandardOpaqueAlphaUnused(Material m) =>
            m.HasProperty("_Mode") && m.GetFloat("_Mode") < 0.5f && !m.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") &&
            !m.IsKeywordEnabled("_ALPHATEST_ON") && !m.IsKeywordEnabled("_ALPHABLEND_ON") && !m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON");

        static IShaderAdapter MainTexOnly(string id, string shaderName, bool lit, string mainColor = null) =>
            new DataShaderAdapter(id, 2, "Unity 2022.3 Built-in " + shaderName, s => s.name == shaderName,
                new[] { new AtlasChannel("_MainTex", AtlasChannelKind.Color) },
                attributes: m => lit
                    ? Attrs(VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.TexCoord0, VertexAttribute.TexCoord1)
                    : Attrs(VertexAttribute.Position, VertexAttribute.TexCoord0),
                mainColor: mainColor);

        /// <summary>
        /// lilToon 2.3.1 のシェーダーソース（Includes/lil_common_frag.hlsl ほか）で確認した内容:
        /// メイン UV（uv0 × _MainTex_ST + スクロール）でサンプルされるテクスチャのみアトラス化し、
        /// MatCap・グラデーション・ディザは UV 非依存のため同一性を要求する。
        /// サブ UV・独自 UV モード・UV アニメーション・UDIM・Decal・Glitter・AudioLink・Dissolve は非対応。
        /// </summary>
        static IShaderAdapter LilToon()
        {
            var names = new HashSet<string>(LilToonShaderNames);
            return new DataShaderAdapter("liltoon", 2, "lilToon 2.3.1", s => names.Contains(s.name), new[]
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
            },
            // lilToon は色調補正・グラデーションマップを適用した後に _Color を乗算する（lil_common_frag.hlsl）。
            // それらが初期値なら、_Color をメインテクスチャへ焼き込んでも結果は同じ
            mainColor: "_Color",
            colorBakeBlocker: m =>
            {
                if (m.HasProperty("_MainTexHSVG") && m.GetVector("_MainTexHSVG") != new Vector4(0, 1, 1, 1)) return "メインの色調補正（HSVG）が有効";
                if (m.HasProperty("_MainGradationStrength") && !Mathf.Approximately(m.GetFloat("_MainGradationStrength"), 0)) return "グラデーションマップが有効";
                return null;
            },
            // lilToon の発光は _UseEmission が有効なら _EmissionColor × _EmissionMap（既定は白）を発光マスク等と合成する。
            // 色とテクスチャの積を焼き込み、無効な Material は透明な黒（合成に寄与しない）にする
            emissionBake: new EmissionBakeSpec
            {
                Texture = "_EmissionMap",
                ColorProperty = "_EmissionColor",
                Enabled = m => m.HasProperty("_UseEmission") && m.GetFloat("_UseEmission") > 0.5f,
                ExcludeProperties = new[] { "_EmissionColor", "_UseEmission" },
                Apply = (atlas, color, reference) =>
                {
                    atlas.SetFloat("_UseEmission", 1);
                    atlas.SetColor("_EmissionColor", color);
                },
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
                // Poiyomi はテクスチャの有無でシェーダーキーワード（ロック時の分岐）が変わるため、ない領域を既定値で埋めない
                return new AtlasChannel(prop, kind, rules.ToArray()).NotFillable();
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
            },
            // Poiyomi はサンプル直後に baseColor = mainTex.rgb * Theme(_Color)、alpha = mainTex.a * _Color.a とする。
            // テーマカラー・Ignore Alpha・Two Pass（2パス目は _TwoPassColor）のときは焼き込まない
            mainColor: "_Color",
            colorBakeBlocker: m =>
            {
                if (m.shader.name.Contains("Two Pass")) return "Two Pass 版（2パス目が別の色を使う）";
                if (m.HasProperty("_ColorThemeIndex") && !Mathf.Approximately(m.GetFloat("_ColorThemeIndex"), 0)) return "メインカラーにテーマカラーを使用";
                if (m.HasProperty("_MainIgnoreTexAlpha") && !Mathf.Approximately(m.GetFloat("_MainIgnoreTexAlpha"), 0)) return "Ignore Alpha が有効";
                return null;
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
