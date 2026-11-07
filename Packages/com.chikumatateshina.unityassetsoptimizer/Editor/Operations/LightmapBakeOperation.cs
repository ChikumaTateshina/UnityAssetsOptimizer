using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>ライトマップ焼き込み後のシェーダー。</summary>
    public enum LightBakeShaderMode
    {
        /// <summary>同梱の Unlit シェーダーに切り替える（色 × 光。強度は Material で調整できる）。</summary>
        Unlit,
        /// <summary>元のシェーダーのまま、UV1 で色に掛け合わせる機能（Standard の Detail ×2、lilToon のメイン 2nd 乗算）に光を入れる。</summary>
        KeepShader,
    }

    /// <summary>
    /// ベイク済みのライトマップ（Unity の Progressive / Bakery。どちらもシーンのライトマップとして適用されたもの）を、
    /// Renderer ごとに使っている部分だけ切り出して 1 枚の「光テクスチャ」にまとめ、UV1 で色に掛け合わせる。
    /// 色のテクスチャ（アトラス）は複製しないため、同じ Material を使うオブジェクトもそれぞれの場所の光になる。
    /// 焼き込んだオブジェクトは Contribute GI を外し、再ベイクで光が二重にならないようにする。
    /// </summary>
    public sealed class LightmapBakeOperation : OptimizationOperationBase
    {
        public const string UnlitShaderName = "UnityAssetsOptimizer/Baked Light Unlit";
        /// <summary>Linear 色空間での unity_ColorSpaceDouble（2^2.2）。</summary>
        const float LinearDouble = 4.594794f;
        const int Margin = 1;

        public override string Id => FeatureIds.LightmapBake;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Avatar = false,
            Phase = 3,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.MaterialDedupe, FeatureIds.SimilarMaterialMerge },
            WritesAssetKinds = new[] { "Texture2D", "Material", "Mesh" },
        };

        sealed class Target
        {
            public RendererInfo Info;
            public int LightmapIndex;
            public Vector4 ScaleOffset;
            /// <summary>ライトマップ上の使用範囲（画素）。</summary>
            public RectInt Source;
            public PlanItem Item;
        }

        sealed class BakeData
        {
            public readonly List<Target> Targets = new List<Target>();
            public AtlasPackResult Pack;
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var data = new BakeData();
            analysis.Data = data;
            var lightmaps = LightmapSettings.lightmaps;
            var mode = context.Settings.lightBakeShaderMode;
            foreach (var info in context.Analysis.Renderers)
            {
                var label = info.Path.Length == 0 ? "(root)" : info.Path;
                var reason = Check(info, lightmaps, mode, out var source);
                if (reason != null)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, reason));
                    continue;
                }
                var item = PlanItem.Supported(label, "ライトマップ " + info.Renderer.lightmapIndex + " の " + source.width + "x" + source.height + " 画素");
                analysis.Items.Add(item);
                data.Targets.Add(new Target
                {
                    Info = info, LightmapIndex = info.Renderer.lightmapIndex, ScaleOffset = info.Renderer.lightmapScaleOffset, Source = source, Item = item,
                });
            }
            analysis.Notes.Add("Unity（Progressive）・Bakery でベイクし、シーンに適用されたライトマップを読み取ります。焼き込んだオブジェクトは Contribute GI を外します（再ベイクで光が二重にならないように）。");
            analysis.Notes.Add(mode == LightBakeShaderMode.Unlit
                ? "Unlit に切り替えます（強度は Material の Light Strength で後から調整できます）。ワールドのライトや反射の影響は受けなくなります。"
                : "元のシェーダーのまま光を乗せます（Standard: Detail Albedo ×2、lilToon: メイン 2nd の乗算）。リアルタイムのライト・環境光も引き続き当たるため、明るさが一部二重になる場合があります。");
            return analysis;
        }

        static string Check(RendererInfo info, LightmapData[] lightmaps, LightBakeShaderMode mode, out RectInt source)
        {
            source = default;
            var r = info.Renderer;
            if (!(r is MeshRenderer)) return "MeshRenderer 以外（ライトマップは静的メッシュのみ）";
            if (EditorUtility.IsPersistent(r)) return "Prefab アセットにはライトマップ情報がありません（ベイクしたシーン上のオブジェクトを選んでください）";
            int index = r.lightmapIndex;
            if (index < 0 || index >= lightmaps.Length) return "ライトマップがありません（未ベイク、または Contribute GI / Receive GI がライトマップでない）";
            var texture = lightmaps[index].lightmapColor;
            if (texture == null) return "ライトマップのテクスチャがありません";
            if (info.Mesh == null) return "Mesh がありません";
            if (!info.Mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1) && !info.Mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
                return "UV がありません";
            foreach (var m in info.Materials)
            {
                var why = MaterialBlocker(m, mode);
                if (why != null) return (m != null ? m.name + ": " : string.Empty) + why;
            }
            var so = r.lightmapScaleOffset;
            int w = texture.width, h = texture.height;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(so.z * w) - Margin, 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(so.w * h) - Margin, 0, h - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((so.z + so.x) * w) + Margin, x0 + 1, w);
            int y1 = Mathf.Clamp(Mathf.CeilToInt((so.w + so.y) * h) + Margin, y0 + 1, h);
            source = new RectInt(x0, y0, x1 - x0, y1 - y0);
            return null;
        }

        static bool IsStandard(Material m) => m.shader.name == "Standard" || m.shader.name == "Standard (Specular setup)";
        static bool IsLilToon(Material m) => ShaderAdapterRegistry.Find(m.shader)?.Id == "liltoon";

        static string MaterialBlocker(Material m, LightBakeShaderMode mode)
        {
            if (m == null || m.shader == null) return "Material がありません";
            if (mode == LightBakeShaderMode.Unlit)
            {
                if (m.renderQueue >= 3000) return "半透明の Material（Unlit への切り替えは不透明・カットアウトのみ）";
                if (!m.HasProperty("_MainTex")) return "メインテクスチャのないシェーダー";
                return null;
            }
            if (IsStandard(m))
            {
                if (m.GetTexture("_DetailAlbedoMap") != null || m.IsKeywordEnabled("_DETAIL_MULX2")) return "Detail Albedo が使用中（光を乗せる場所がありません）";
                return null;
            }
            if (IsLilToon(m))
            {
                if (m.HasProperty("_UseMain2ndTex") && m.GetFloat("_UseMain2ndTex") > 0.5f) return "メイン 2nd が使用中（光を乗せる場所がありません）";
                return m.HasProperty("_Main2ndTex") ? null : "このバリエーションの lilToon にはメイン 2nd がありません";
            }
            return "元のシェーダーのまま光を乗せられるのは Standard と lilToon のみです（Unlit モードを使ってください）: " + m.shader.name;
        }

        public override OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            var data = (BakeData)analysis.Data;
            var targets = data.Targets.Where(t => t.Item.included).ToList();
            var result = new BakeData();
            plan.Data = result;
            if (targets.Count == 0)
            {
                plan.ExpectedEffect = "焼き込むオブジェクトなし";
                return plan;
            }
            try
            {
                result.Pack = AtlasPacker.Pack(targets.Select(t => t.Source.size).ToList(), 2, context.Settings.lightBakeMaxSize);
                result.Targets.AddRange(targets);
            }
            catch (InvalidOperationException e)
            {
                foreach (var t in targets)
                {
                    t.Item.included = false;
                    t.Item.reason = e.Message;
                }
                return plan;
            }
            plan.ExpectedEffect = "Renderer " + targets.Count + " 個の光を " + result.Pack.Width + "x" + result.Pack.Height + " の光テクスチャに焼き込み（" +
                                  (context.Settings.lightBakeShaderMode == LightBakeShaderMode.Unlit ? "Unlit" : "元のシェーダー") + "、強度 " + context.Settings.lightBakeStrength.ToString("0.00") +
                                  (result.Pack.Scale < 1f ? "、縮小率 " + result.Pack.Scale.ToString("0.00") : string.Empty) + "）";
            return plan;
        }

        public override void ApplyToContext(OperationPlan plan, OptimizationContext context)
        {
            context.LightBakedRendererKeys = new HashSet<string>(((BakeData)plan.Data).Targets.Select(t => t.Info.Key));
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var data = (BakeData)plan.Data;
            if (data.Targets.Count == 0) return;
            var settings = gen.Settings;
            bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;
            // 元のシェーダーのままの場合、Standard の Detail には強度を調整する値がないため強度を焼き込む。Unlit / lilToon は Material で調整する
            float bakedStrength = settings.lightBakeShaderMode == LightBakeShaderMode.KeepShader ? Mathf.Clamp01(settings.lightBakeStrength) : 1f;

            var lightmaps = LightmapSettings.lightmaps;
            var decoded = new Dictionary<int, (Color[] px, int w, int h)>();
            var sources = new List<AtlasComposer.Source>();
            for (int i = 0; i < data.Targets.Count; i++)
            {
                var t = data.Targets[i];
                if (!decoded.TryGetValue(t.LightmapIndex, out var lm))
                {
                    var tex = lightmaps[t.LightmapIndex].lightmapColor;
                    lm = (ReadLightmap(tex, linear), tex.width, tex.height);
                    decoded[t.LightmapIndex] = lm;
                }
                var crop = new Color32[t.Source.width * t.Source.height];
                for (int y = 0; y < t.Source.height; y++)
                    for (int x = 0; x < t.Source.width; x++)
                    {
                        var c = lm.px[(t.Source.y + y) * lm.w + t.Source.x + x];
                        crop[y * t.Source.width + x] = Encode(c, bakedStrength, linear);
                    }
                sources.Add(new AtlasComposer.Source { Pixels = crop, Width = t.Source.width, Height = t.Source.height, Destination = data.Pack.Rects[i] });
            }
            var pack = data.Pack;
            var composed = AtlasComposer.Compose(pack.Width, pack.Height, sources, 2, Encode(Color.white, 1f, linear));
            var lightTexture = new Texture2D(pack.Width, pack.Height, TextureFormat.RGBA32, false, false) { name = "BakedLight" };
            lightTexture.SetPixels32(composed);
            lightTexture.Apply(false, false);
            var record = new GeneratedTexture { OperationId = Id, SourceRef = "lightmaps:" + string.Join(",", decoded.Keys), HasAlpha = false };
            record.Import.SRgb = true;
            record.Import.MaxSize = pack.Width;
            var savedLight = gen.AddTexture(lightTexture, record, "BakedLight");

            var converted = new Dictionary<Material, Material>();
            var targetKeys = new HashSet<string>(data.Targets.Select(t => t.Info.Key));
            for (int i = 0; i < data.Targets.Count; i++)
            {
                gen.ThrowIfCancelled();
                var t = data.Targets[i];
                var clone = gen.Copy.FindRenderer(t.Info.Key);
                if (clone == null) throw new InvalidOperationException("ワーキングコピー上の Renderer が見つかりません: " + t.Info.Key);
                var filter = clone.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) throw new InvalidOperationException("Mesh が見つかりません: " + t.Info.Key);

                // UV1（なければ UV0）をライトマップ上の位置 → 光テクスチャ上の位置へ写す
                var uv = new List<Vector2>();
                mesh.GetUVs(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1) ? 1 : 0, uv);
                var lm = decoded[t.LightmapIndex];
                var dst = pack.Rects[i];
                var so = t.ScaleOffset;
                for (int v = 0; v < uv.Count; v++)
                {
                    float px = (uv[v].x * so.x + so.z) * lm.w, py = (uv[v].y * so.y + so.w) * lm.h;
                    uv[v] = new Vector2(
                        (dst.x + (px - t.Source.x) / t.Source.width * dst.width) / pack.Width,
                        (dst.y + (py - t.Source.y) / t.Source.height * dst.height) / pack.Height);
                }
                var copy = Object.Instantiate(mesh);
                copy.name = mesh.name + "_light";
                copy.SetUVs(1, uv);
                filter.sharedMesh = gen.AddAsset(copy, "Meshes", copy.name, Id, t.Info.Key);

                clone.sharedMaterials = clone.sharedMaterials.Select(m => Convert(gen, m, savedLight, converted, targetKeys)).ToArray();
                var flags = GameObjectUtility.GetStaticEditorFlags(clone.gameObject);
                GameObjectUtility.SetStaticEditorFlags(clone.gameObject, flags & ~StaticEditorFlags.ContributeGI);
            }
            gen.Log.Info(Id, "Renderer " + data.Targets.Count + " 個の光を " + pack.Width + "x" + pack.Height + " の光テクスチャに焼き込みました（ライトマップ " + decoded.Count + " 枚から）。");
        }

        /// <summary>光の値（線形、HDR）を光テクスチャの画素にする。Detail Albedo ×2 と同じ表現（Linear: sRGB で L / 4.59、Gamma: L / 2）。</summary>
        static Color32 Encode(Color light, float strength, bool linear)
        {
            float Channel(float l)
            {
                l = Mathf.Lerp(1f, Mathf.Max(0f, l), strength);
                return linear ? Mathf.LinearToGammaSpace(Mathf.Clamp01(l / LinearDouble)) : Mathf.Clamp01(Mathf.Pow(l, 1f / 2.2f) / 2f);
            }
            return new Color32((byte)Mathf.RoundToInt(Channel(light.r) * 255), (byte)Mathf.RoundToInt(Channel(light.g) * 255), (byte)Mathf.RoundToInt(Channel(light.b) * 255), 255);
        }

        /// <summary>
        /// ライトマップを GPU 経由で読み戻し、保存形式（HDR / RGBM / dLDR）に応じて線形の光の値に戻す。
        /// 形式は Player Settings の Lightmap Encoding（取得できなければテクスチャ形式）で判断する。
        /// </summary>
        static Color[] ReadLightmap(Texture2D texture, bool linear)
        {
            int w = texture.width, h = texture.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var readback = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readback.Apply(false, false);
                var px = readback.GetPixels();
                var encoding = EncodingOf(texture);
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    switch (encoding)
                    {
                        case LightmapEncoding.Rgbm:
                            float m = linear ? 34.49324f * Mathf.Pow(c.a, 2.2f) : 5f * c.a;
                            px[i] = new Color(c.r * m, c.g * m, c.b * m, 1);
                            break;
                        case LightmapEncoding.DoubleLdr:
                            float d = linear ? LinearDouble : 2f;
                            px[i] = new Color(c.r * d, c.g * d, c.b * d, 1);
                            break;
                        default:
                            px[i] = new Color(c.r, c.g, c.b, 1);
                            break;
                    }
                    if (!linear) px[i] = new Color(Mathf.GammaToLinearSpace(px[i].r), Mathf.GammaToLinearSpace(px[i].g), Mathf.GammaToLinearSpace(px[i].b), 1);
                }
                return px;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(readback);
            }
        }

        enum LightmapEncoding { Hdr, Rgbm, DoubleLdr }

        static LightmapEncoding EncodingOf(Texture2D texture)
        {
            switch (texture.format)
            {
                case TextureFormat.BC6H:
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGBAFloat:
                case TextureFormat.RGB9e5Float:
                    return LightmapEncoding.Hdr;
            }
            // Player Settings の Lightmap Encoding（Low = dLDR / Normal = RGBM / High = HDR）。公開 API がないため取得できなければ形式で推定する
            try
            {
                var method = typeof(PlayerSettings).GetMethod("GetLightmapEncodingQualityForPlatformGroup", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (method != null)
                {
                    var quality = System.Convert.ToInt32(method.Invoke(null, new object[] { EditorUserBuildSettings.selectedBuildTargetGroup }));
                    if (quality == 0) return LightmapEncoding.DoubleLdr;
                    if (quality == 1) return LightmapEncoding.Rgbm;
                    return LightmapEncoding.Hdr;
                }
            }
            catch (Exception)
            {
                // 推定へ
            }
            return UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat) ? LightmapEncoding.Rgbm : LightmapEncoding.DoubleLdr;
        }

        /// <summary>
        /// 光を乗せた Material を作る（同じ Material は 1 度だけ）。この実行で生成した Material で、使う Renderer がすべて焼き込み対象なら、それ自体を書き換える。
        /// </summary>
        Material Convert(GenerationContext gen, Material source, Texture lightTexture, Dictionary<Material, Material> cache, HashSet<string> targetKeys)
        {
            if (source == null) return null;
            if (cache.TryGetValue(source, out var done)) return done;
            var settings = gen.Settings;
            bool reuse = gen.IsGenerated(source) && gen.Copy.AllRenderers.Where(p => p.Value.sharedMaterials.Contains(source)).All(p => targetKeys.Contains(p.Key));
            Material result;
            if (settings.lightBakeShaderMode == LightBakeShaderMode.Unlit)
            {
                var shader = Shader.Find(UnlitShaderName);
                if (shader == null) throw new InvalidOperationException("シェーダー " + UnlitShaderName + " が見つかりません。");
                result = new Material(shader) { name = source.name + "_BakedLight" };
                if (source.HasProperty("_MainTex"))
                {
                    result.SetTexture("_MainTex", source.GetTexture("_MainTex"));
                    result.SetTextureScale("_MainTex", source.GetTextureScale("_MainTex"));
                    result.SetTextureOffset("_MainTex", source.GetTextureOffset("_MainTex"));
                }
                if (source.HasProperty("_Color")) result.SetColor("_Color", source.GetColor("_Color"));
                bool emission = (IsStandard(source) && source.IsKeywordEnabled("_EMISSION")) ||
                                (IsLilToon(source) && source.HasProperty("_UseEmission") && source.GetFloat("_UseEmission") > 0.5f);
                if (emission && source.HasProperty("_EmissionColor"))
                {
                    if (source.HasProperty("_EmissionMap") && source.GetTexture("_EmissionMap") != null)
                    {
                        result.SetTexture("_EmissionMap", source.GetTexture("_EmissionMap"));
                        result.SetTextureScale("_EmissionMap", source.GetTextureScale("_EmissionMap"));
                        result.SetTextureOffset("_EmissionMap", source.GetTextureOffset("_EmissionMap"));
                    }
                    result.SetColor("_EmissionColor", source.GetColor("_EmissionColor"));
                }
                bool cutout = source.IsKeywordEnabled("_ALPHATEST_ON") || (source.renderQueue >= 2450 && source.renderQueue < 3000);
                if (cutout)
                {
                    result.EnableKeyword("_ALPHATEST_ON");
                    result.SetFloat("_AlphaTest", 1);
                    if (source.HasProperty("_Cutoff")) result.SetFloat("_Cutoff", source.GetFloat("_Cutoff"));
                }
                result.renderQueue = source.renderQueue;
                result.SetTexture("_LightTex", lightTexture);
                result.SetFloat("_LightStrength", Mathf.Clamp01(settings.lightBakeStrength));
                result.enableInstancing = source.enableInstancing;
            }
            else
            {
                result = reuse ? source : new Material(source) { name = source.name + "_BakedLight" };
                if (IsStandard(result))
                {
                    result.SetTexture("_DetailAlbedoMap", lightTexture);
                    result.SetTextureScale("_DetailAlbedoMap", Vector2.one);
                    result.SetTextureOffset("_DetailAlbedoMap", Vector2.zero);
                    result.SetFloat("_UVSec", 1);
                    result.EnableKeyword("_DETAIL_MULX2");
                }
                else
                {
                    // lilToon: 色2nd = _Color2nd × テクスチャ(UV1) を乗算で合成（lilBlendColor の Multiply）。_Color2nd = 2（Linear では 4.59 として扱われる）で
                    // Detail ×2 と同じ表現になり、アルファが強度（lerp）になる
                    result.SetFloat("_UseMain2ndTex", 1);
                    result.SetTexture("_Main2ndTex", lightTexture);
                    result.SetTextureScale("_Main2ndTex", Vector2.one);
                    result.SetTextureOffset("_Main2ndTex", Vector2.zero);
                    result.SetFloat("_Main2ndTex_UVMode", 1);
                    result.SetFloat("_Main2ndTexBlendMode", 3);
                    result.SetColor("_Color2nd", new Color(2f, 2f, 2f, 1f));
                }
            }
            if (result != source) result = gen.AddAsset(result, "Materials", result.name, Id, MaterialEquivalence.StableId(source));
            cache[source] = result;
            return result;
        }

        public override ValidationResult Validate(GenerationContext gen, OperationPlan plan)
        {
            var result = new ValidationResult();
            foreach (var t in ((BakeData)plan.Data).Targets)
            {
                var clone = gen.Copy.FindRenderer(t.Info.Key);
                if (clone == null) continue; // 後続の結合で削除済み（検証は焼き込み直後に行う）
                var mesh = clone.GetComponent<MeshFilter>()?.sharedMesh;
                var uv = new List<Vector2>();
                mesh?.GetUVs(1, uv);
                if (mesh == null || uv.Count != mesh.vertexCount) result.Fail(t.Info.Key + ": 光テクスチャ用の UV1 がありません。");
                else if (uv.Any(u => u.x < -1e-3f || u.y < -1e-3f || u.x > 1.001f || u.y > 1.001f)) result.Fail(t.Info.Key + ": UV1 が光テクスチャの範囲外です。");
                if ((GameObjectUtility.GetStaticEditorFlags(clone.gameObject) & StaticEditorFlags.ContributeGI) != 0) result.Fail(t.Info.Key + ": Contribute GI が残っています。");
            }
            return result;
        }
    }
}
