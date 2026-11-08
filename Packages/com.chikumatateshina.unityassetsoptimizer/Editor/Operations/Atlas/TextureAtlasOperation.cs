using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// テクスチャアトラス化 + Material 統合 + サブメッシュ統合。
    /// 対応 Shader Adapter・単位 ST・UV0 が [0,1] 内・読み取り可能なテクスチャの組み合わせのみを処理する。
    /// </summary>
    public sealed class TextureAtlasOperation : OptimizationOperationBase, IStepwiseOperation
    {
        const float UvEpsilon = 1e-4f;

        public override string Id => FeatureIds.Atlas;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            CanForce = true,
            Phase = 1,
            RunsAfter = new[] { FeatureIds.MaterialDedupe, FeatureIds.SimilarMaterialMerge },
            WritesAssetKinds = new[] { "Texture2D(png)", "Material", "Mesh" },
        };

        sealed class MaterialCandidate
        {
            public Material Material;
            public IShaderAdapter Adapter;
            public SupportState Support = SupportState.Supported;
            public string Reason;
            public bool Forceable;
            public string StrictKey;
            public string LooseKey;
            public readonly List<AtlasChannel> Channels = new List<AtlasChannel>();
            public readonly Dictionary<string, TextureSourceInfo> Sources = new Dictionary<string, TextureSourceInfo>();
            public Vector2Int Size;
            /// <summary>メインカラーをアトラスへ焼き込む（アトラス Material のメインカラーは白にする）。</summary>
            public bool ColorBaked;
            /// <summary>テクスチャへ乗算する色（白なら null）。単色ソースの場合は色がソース自体に入っている。</summary>
            public Color? BakeColor;
            /// <summary>
            /// 既定値で埋められるチャンネル（と発光）の Importer 設定。グループ内でテクスチャがある Material 同士はこれが一致する必要がある。
            /// 埋められないチャンネルは StrictKey に含める（有無も一致が必要）。
            /// </summary>
            public readonly Dictionary<string, string> FillableKeys = new Dictionary<string, string>();
            /// <summary>発光を焼き込む（アダプターが対応している場合）。</summary>
            public bool EmissionBaked;
            public bool EmissionOn;
            /// <summary>数値プロパティを焼き込むチャンネル（テクスチャ → アルファへ掛ける倍率。テクスチャがなければ単色ソース）。</summary>
            public readonly Dictionary<string, float> ParameterAlphaScales = new Dictionary<string, float>();
            public readonly List<ParameterBakeSpec> ParameterBakes = new List<ParameterBakeSpec>();
            /// <summary>発光色（Material に保存されている値。HDR 可）。</summary>
            public Color EmissionColor;
            /// <summary>使用している面のワールド面積（m²）と UV 面積（0〜1）。テクセル密度の配分に使う。</summary>
            public double WorldArea;
            public double UvArea;
        }

        sealed class SubmeshEntry
        {
            public RendererInfo Renderer;
            public int Submesh;
            public MaterialCandidate Candidate;
            public PlanItem Item;
        }

        public sealed class AtlasGroup
        {
            public int Index;
            public IShaderAdapter Adapter;
            public List<Material> Materials = new List<Material>();
            public List<AtlasChannel> Channels = new List<AtlasChannel>();
            public Dictionary<Material, Dictionary<string, TextureSourceInfo>> Sources = new Dictionary<Material, Dictionary<string, TextureSourceInfo>>();
            public List<Vector2> Sizes = new List<Vector2>();
            public AtlasPackResult Pack;
            public bool MixedProperties;
            public string ColorProperty;
            public string ColorTexture;
            public Dictionary<Material, Color?> BakeColors = new Dictionary<Material, Color?>();
            /// <summary>発光の焼き込み（なければ null）: 各領域に掛ける色と、アトラス Material の発光色。</summary>
            public EmissionBakeSpec Emission;
            public Dictionary<Material, Color> EmissionFactors = new Dictionary<Material, Color>();
            public Color EmissionAtlasColor;
            public Material EmissionReference;
            /// <summary>数値プロパティの焼き込み（アトラス Material で適用）と、アルファへ掛ける倍率（Material, チャンネル）。</summary>
            public List<ParameterBakeSpec> ParameterBakes = new List<ParameterBakeSpec>();
            public Dictionary<(Material, string), float> AlphaScales = new Dictionary<(Material, string), float>();
            /// <summary>メインテクスチャのアルファがどの Material でも使われない（不透明として保存する）。</summary>
            public bool MainAlphaUnused;
            /// <summary>どれかの Material にテクスチャがあるため、アトラス Material で有効にするキーワード。</summary>
            public List<string> Keywords = new List<string>();
            /// <summary>テクスチャのない Material の領域を埋めた（チャンネル → Material 数）。</summary>
            public Dictionary<string, int> FilledChannels = new Dictionary<string, int>();

            // Execute 時に設定
            public Material AtlasMaterial;
            public Dictionary<Material, SubmeshMapping> Mappings = new Dictionary<Material, SubmeshMapping>();
        }

        public sealed class AtlasPlanData
        {
            public readonly List<AtlasGroup> Groups = new List<AtlasGroup>();
            /// <summary>rendererKey → (submesh → group)</summary>
            public readonly Dictionary<string, Dictionary<int, AtlasGroup>> Assignments = new Dictionary<string, Dictionary<int, AtlasGroup>>();
            public readonly Dictionary<string, RendererInfo> Renderers = new Dictionary<string, RendererInfo>();
            public readonly List<string> ProcessedRendererKeys = new List<string>();
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var entries = new List<SubmeshEntry>();
            var candidates = new Dictionary<Material, MaterialCandidate>();
            bool contentDedupe = context.TextureContentDedupe;

            foreach (var info in context.Analysis.Renderers)
            {
                var label = info.Path.Length == 0 ? "(root)" : info.Path;
                if (!(info.Renderer is MeshRenderer) && !(info.Renderer is SkinnedMeshRenderer))
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "対象外の Renderer 種別（" + info.Renderer.GetType().Name + "）"));
                    continue;
                }
                if (info.Mesh == null)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "Mesh が未設定"));
                    continue;
                }
                if (info.Materials.Length != info.Mesh.subMeshCount)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "Material スロット数とサブメッシュ数が不一致（マルチパス描画等）"));
                    continue;
                }
                string meshReason = CheckMesh(info.Mesh);
                if (meshReason != null)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, meshReason));
                    continue;
                }

                string rendererUnknown = null;
                if (info.AnimatedMaterial) rendererUnknown = "マテリアルを操作するアニメーションが存在";
                else if (info.ScriptReferenced) rendererUnknown = "スクリプトから参照されている Renderer";
                else if (info.UdonOnSameObject) rendererUnknown = "同一オブジェクトに UdonBehaviour（実行時操作を解析不能）";

                for (int s = 0; s < info.Materials.Length; s++)
                {
                    var mat = context.Canonical(info.Materials[s]);
                    var itemLabel = label + " [slot " + s + "] " + (mat != null ? mat.name : "(null)");
                    if (mat == null)
                    {
                        analysis.Items.Add(PlanItem.Skip(itemLabel, SupportState.Unsupported, "Material 未設定"));
                        continue;
                    }
                    if (!candidates.TryGetValue(mat, out var cand))
                    {
                        cand = EvaluateMaterial(mat, context, contentDedupe);
                        candidates[mat] = cand;
                    }

                    PlanItem item;
                    if (cand.Support != SupportState.Supported)
                        item = PlanItem.Skip(itemLabel, cand.Support, cand.Reason, cand.Forceable);
                    else if (!AtlasMeshBuilder.TryGetSubmeshUvBounds(info.Mesh, s, out var min, out var max))
                        item = PlanItem.Skip(itemLabel, SupportState.Unsupported, "UV0 がありません");
                    else if (min.x < -UvEpsilon || min.y < -UvEpsilon || max.x > 1 + UvEpsilon || max.y > 1 + UvEpsilon)
                        item = PlanItem.Skip(itemLabel, SupportState.Unsupported, "UV が [0,1] 外（タイル/ミラー/負座標）: min" + min + " max" + max);
                    else if (rendererUnknown != null)
                        item = PlanItem.Skip(itemLabel, SupportState.Unknown, rendererUnknown, true);
                    else
                        item = PlanItem.Supported(itemLabel);

                    analysis.Items.Add(item);
                    if (cand.Support == SupportState.Supported || cand.Forceable)
                        entries.Add(new SubmeshEntry { Renderer = info, Submesh = s, Candidate = cand, Item = item });
                }
            }

            analysis.Data = entries;
            analysis.Notes.Add("対応シェーダー: " + string.Join(", ", ShaderAdapterRegistry.All.Select(a => a.Id)));
            analysis.Notes.Add("MaterialPropertyBlock による実行時のプロパティ上書きは静的に検出できません。");
            return analysis;
        }

        static string CheckMesh(Mesh mesh)
        {
            try
            {
                if (mesh.vertexCount == 0) return "頂点データを読み取れません（Read/Write 無効の可能性）";
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (mesh.GetTopology(s) != MeshTopology.Triangles) return "三角形以外のトポロジー";
            }
            catch (Exception e)
            {
                return "メッシュを解析できません: " + e.Message;
            }
            return null;
        }

        MaterialCandidate EvaluateMaterial(Material mat, OptimizationContext context, bool contentDedupe)
        {
            var cand = new MaterialCandidate { Material = mat, Adapter = ShaderAdapterRegistry.Find(mat.shader) };
            if (cand.Adapter == null && mat.shader != null && mat.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal))
                return Reject(cand, SupportState.Unsupported, "ロック済みシェーダー（" + mat.shader.name + "）。ロックを解除してから実行してください（生成後に再ロックできます）");
            if (cand.Adapter == null)
                return Reject(cand, SupportState.Unsupported, "未対応シェーダー: " + (mat.shader != null ? mat.shader.name : "(none)"));

            var shader = mat.shader;
            var known = new HashSet<string>(cand.Adapter.Channels.Select(c => c.Property)
                .Concat(cand.Adapter.SharedTextures).Concat(cand.Adapter.ForbiddenTextures));
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var name = shader.GetPropertyName(i);
                if (known.Contains(name) || mat.GetTexture(name) == null) continue;
                return Reject(cand, SupportState.Unsupported, "アダプター未定義のテクスチャプロパティに値あり: " + name);
            }
            var precondition = ShaderAdapterRegistry.CheckPreconditions(cand.Adapter, mat);
            if (precondition != null) return Reject(cand, SupportState.Unsupported, precondition);

            // メインカラーの焼き込み可否（HDR の色やシェーダー固有の条件があれば焼き込まない）
            var colorProp = cand.Adapter.MainColorProperty;
            string bakeBlocker = null;
            if (colorProp == null || !mat.HasProperty(colorProp) || !mat.HasProperty(cand.Adapter.MainColorTexture)) bakeBlocker = "メインカラーなし";
            else
            {
                var c = mat.GetColor(colorProp);
                if (c.r < 0 || c.g < 0 || c.b < 0 || c.a < 0 || c.r > 1 || c.g > 1 || c.b > 1 || c.a > 1) bakeBlocker = "HDR のメインカラー";
                else bakeBlocker = cand.Adapter.ColorBakeBlocker(mat);
            }
            cand.ColorBaked = bakeBlocker == null;

            // 発光の焼き込み（無効な Material は黒として同じグループにまとめる）
            var emission = cand.Adapter.EmissionBake;
            if (emission != null && mat.HasProperty(emission.ColorProperty) && mat.HasProperty(emission.Texture))
            {
                cand.EmissionBaked = true;
                cand.EmissionOn = emission.Enabled(mat);
                cand.EmissionColor = mat.GetColor(emission.ColorProperty);
                if (cand.EmissionColor.r < 0 || cand.EmissionColor.g < 0 || cand.EmissionColor.b < 0)
                    return Reject(cand, SupportState.Unsupported, "発光色に負の値があります");
            }

            // 数値プロパティの焼き込み（例: Metallic/Smoothness → _MetallicGlossMap）
            foreach (var spec in cand.Adapter.ParameterBakes)
                if (mat.HasProperty(spec.Texture) && spec.Applicable(mat)) cand.ParameterBakes.Add(spec);

            var importKeys = new List<string>();
            foreach (var channel in cand.Adapter.Channels)
            {
                if (!mat.HasProperty(channel.Property)) continue;
                bool isEmission = cand.EmissionBaked && channel.Property == emission.Texture;
                var parameterBake = cand.ParameterBakes.FirstOrDefault(b => b.Texture == channel.Property);
                if (isEmission && !cand.EmissionOn) continue; // 発光が無効ならマップは使われない（黒で埋める）
                var tex = mat.GetTexture(channel.Property);
                if (tex == null) continue;
                if (mat.GetTextureScale(channel.Property) != Vector2.one || mat.GetTextureOffset(channel.Property) != Vector2.zero)
                    return Reject(cand, SupportState.Unsupported, channel.Property + " の Tiling/Offset が単位値ではありません");
                foreach (var rule in channel.Rules)
                {
                    var violated = rule.Check(mat);
                    if (violated != null) return Reject(cand, SupportState.Unsupported, violated);
                }
                if (!TextureReader.TryInspect(tex, channel.Kind, out var src, out var reason))
                    return Reject(cand, SupportState.Unsupported, channel.Property + ": " + reason);
                cand.Channels.Add(channel);
                cand.Sources[channel.Property] = src;
                cand.Size = new Vector2Int(Math.Max(cand.Size.x, src.Width), Math.Max(cand.Size.y, src.Height));
                var key = channel.Property + "=" + src.ImportKey + ":" + src.Filter + ":" + src.AnisoLevel;
                if (channel.Fillable || isEmission || parameterBake != null) cand.FillableKeys[channel.Property] = key;
                else importKeys.Add(key);
                if (parameterBake != null) cand.ParameterAlphaScales[channel.Property] = parameterBake.AlphaScale(mat);
            }
            foreach (var spec in cand.ParameterBakes.Where(b => !cand.Sources.ContainsKey(b.Texture)))
            {
                // テクスチャがなければ数値から単色の領域を作る（値はそのまま書く。sRGB の扱いは合成時に揃える）
                var solid = TextureSourceInfo.SolidSource(spec.SolidValue(mat));
                solid.SRgb = spec.SRgb;
                cand.Channels.Add(cand.Adapter.Channels.First(ch => ch.Property == spec.Texture));
                cand.Sources[spec.Texture] = solid;
            }
            var mainChannel = cand.Adapter.Channels.FirstOrDefault(ch => ch.Property == cand.Adapter.MainColorTexture);
            bool hasMain = cand.Sources.ContainsKey(cand.Adapter.MainColorTexture);
            if (!hasMain && cand.ColorBaked && mainChannel != null && (cand.Channels.Count == 0 || mainChannel.Fillable))
            {
                // メインテクスチャのない Material は、メインカラーを単色の領域として焼き込む（未割り当ての白 × 色と同じ）
                var solid = TextureSourceInfo.SolidSource(mat.GetColor(colorProp));
                cand.Channels.Add(mainChannel);
                cand.Sources[mainChannel.Property] = solid;
                if (cand.Size == Vector2Int.zero) cand.Size = new Vector2Int(solid.Width, solid.Height);
                // 単色の領域は Importer 設定を持たないため、埋められるチャンネルでは一致判定に加えない
                if (!mainChannel.Fillable) importKeys.Add(mainChannel.Property + "=" + solid.ImportKey + ":" + solid.Filter + ":" + solid.AnisoLevel);
            }
            else if (!cand.Sources.Values.Any(x => !x.Solid) && !cand.ColorBaked)
            {
                // 実際のテクスチャがなく、色も焼き込めない（数値から作る単色だけでは統合の意味がない）
                return Reject(cand, SupportState.Unsupported, "アトラス化するテクスチャがありません（メインカラーも焼き込めません: " + bakeBlocker + "）");
            }
            else if (cand.ColorBaked)
            {
                if (hasMain)
                {
                    var c = mat.GetColor(colorProp);
                    if (c != Color.white) cand.BakeColor = c;
                }
                else if (mat.GetColor(colorProp) != Color.white)
                {
                    cand.ColorBaked = false; // メインテクスチャを埋められないシェーダーでは、色を焼き込めない
                }
            }
            if (cand.Size == Vector2Int.zero) cand.Size = new Vector2Int(16, 16); // 単色の発光だけの場合

            foreach (var shared in cand.Adapter.SharedTextures)
                if (mat.HasProperty(shared))
                    importKeys.Add(shared + "=shared:" + TextureIdentity.Key(mat.GetTexture(shared), contentDedupe) + ":" +
                                   mat.GetTextureScale(shared) + mat.GetTextureOffset(shared));
            var tail = cand.Adapter.Id + "#" + cand.Adapter.Version + "|" + string.Join(";", importKeys) +
                       (cand.ColorBaked ? "|colorbake" : string.Empty) + (cand.EmissionBaked ? "|emissionbake" : string.Empty) +
                       string.Concat(cand.ParameterBakes.Select(b => "|bake:" + b.Texture));
            // 焼き込むプロパティと、テクスチャの有無で付くキーワードは等価性判定から外す（アトラス Material で設定する）
            var exclude = new List<string>();
            if (cand.ColorBaked) exclude.Add(colorProp);
            if (cand.EmissionBaked) exclude.AddRange(emission.ExcludeProperties);
            foreach (var b in cand.ParameterBakes) exclude.AddRange(b.ExcludeProperties);
            var excludeKeywords = cand.Adapter.Channels.Where(ch => ch.Fillable && ch.KeywordWhenPresent != null).Select(ch => ch.KeywordWhenPresent).ToList();
            if (cand.EmissionBaked) excludeKeywords.AddRange(emission.ExcludeKeywords);
            foreach (var b in cand.ParameterBakes) excludeKeywords.AddRange(b.ExcludeKeywords);
            bool includeGi = !(cand.EmissionBaked && emission.ExcludeGlobalIllumination);
            cand.StrictKey = tail + "|" + MaterialEquivalence.Key(mat, contentDedupe, true, false, exclude, excludeKeywords, includeGi);
            cand.LooseKey = tail + "|" + MaterialEquivalence.Key(mat, contentDedupe, false, false, exclude, excludeKeywords, includeGi);

            if (context.Analysis.ScriptReferencedMaterials.Contains(mat))
            {
                cand.Support = SupportState.Unknown;
                cand.Reason = "スクリプトから参照されている Material";
                cand.Forceable = true;
            }
            return cand;
        }

        static MaterialCandidate Reject(MaterialCandidate cand, SupportState state, string reason)
        {
            cand.Support = state;
            cand.Reason = reason;
            cand.Forceable = false;
            return cand;
        }

        public override OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            var entries = (List<SubmeshEntry>)analysis.Data;
            var data = new AtlasPlanData();
            plan.Data = data;
            bool force = mode == ExecutionMode.Force && ManualOverride.IsEnabled;

            var included = entries.Where(e => e.Item.included).ToList();

            // 各テクスチャを参照している Material（グループ外でも使われるテクスチャをアトラスへ複製するとメモリが増える）
            var textureUsers = new Dictionary<Texture, HashSet<Material>>();
            foreach (var m in context.Analysis.Renderers.SelectMany(r => r.Materials).Select(context.Canonical).Where(m => m != null && m.shader != null).Distinct())
                for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                {
                    if (m.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var t = m.GetTexture(m.shader.GetPropertyName(i));
                    if (t == null) continue;
                    if (!textureUsers.TryGetValue(t, out var users)) textureUsers[t] = users = new HashSet<Material>();
                    users.Add(m);
                }
            var byKey = included
                .GroupBy(e => force ? e.Candidate.LooseKey : e.Candidate.StrictKey)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            // 既定値で埋められるチャンネルは、テクスチャがある Material 同士で Importer 設定が一致すれば同じグループにできる
            var partitions = new List<List<SubmeshEntry>>();
            foreach (var g in byKey) partitions.AddRange(PartitionByFillableChannels(g.ToList()));

            foreach (var g in partitions)
            {
                var materials = g.Select(e => e.Candidate.Material).Distinct()
                    .OrderBy(MaterialEquivalence.StableId, StringComparer.Ordinal).ToList();
                if (materials.Count < 2)
                {
                    foreach (var e in g)
                    {
                        e.Item.included = false;
                        e.Item.reason = AppendReason(e.Item.reason, "互換グループ内に統合相手がありません");
                    }
                    continue;
                }

                var first = g.First().Candidate;
                var candidates = materials.Select(m => g.First(e => e.Candidate.Material == m).Candidate).ToList();
                var group = new AtlasGroup { Index = data.Groups.Count, Adapter = first.Adapter };
                // チャンネルは全 Material の和集合（アダプターの定義順）
                group.Channels = first.Adapter.Channels.Where(ch => candidates.Any(c => c.Sources.ContainsKey(ch.Property))).ToList();
                if (first.EmissionBaked && candidates.Any(c => c.EmissionOn))
                {
                    var emissionChannel = first.Adapter.Channels.First(ch => ch.Property == first.Adapter.EmissionBake.Texture);
                    if (!group.Channels.Contains(emissionChannel))
                        group.Channels = first.Adapter.Channels.Where(ch => ch == emissionChannel || group.Channels.Contains(ch)).ToList();
                    group.Emission = first.Adapter.EmissionBake;
                }
                if (first.ColorBaked)
                {
                    group.ColorProperty = first.Adapter.MainColorProperty;
                    group.ColorTexture = first.Adapter.MainColorTexture;
                }
                group.Keywords.AddRange(group.Channels.Where(ch => ch.KeywordWhenPresent != null).Select(ch => ch.KeywordWhenPresent));
                group.ParameterBakes.AddRange(first.ParameterBakes);
                foreach (var c in candidates)
                    foreach (var scale in c.ParameterAlphaScales.Where(x => !Mathf.Approximately(x.Value, 1f)))
                        group.AlphaScales[(c.Material, scale.Key)] = scale.Value;
                group.MainAlphaUnused = candidates.All(c => c.Adapter.MainAlphaUnused(c.Material));
                PlanEmission(group, candidates);
                foreach (var cand in candidates)
                {
                    var m = cand.Material;
                    var sources = new Dictionary<string, TextureSourceInfo>(cand.Sources);
                    foreach (var ch in group.Channels.Where(ch => !sources.ContainsKey(ch.Property)))
                    {
                        sources[ch.Property] = group.Emission != null && ch.Property == group.Emission.Texture
                            ? TextureSourceInfo.SolidSource(cand.EmissionOn ? Color.white : new Color(0, 0, 0, 0))
                            : FillSource(m, ch);
                        group.FilledChannels[ch.Property] = group.FilledChannels.TryGetValue(ch.Property, out var n) ? n + 1 : 1;
                    }
                    group.Materials.Add(m);
                    group.Sources[m] = sources;
                    group.BakeColors[m] = cand.BakeColor;
                }
                group.Sizes.AddRange(BalancedSizes(candidates, g, context.Settings.atlasBalanceByObjectSize));
                group.MixedProperties = g.Select(e => e.Candidate.StrictKey).Distinct().Count() > 1;

                var sharedOutside = group.Sources.Values.SelectMany(d => d.Values).Where(s => !s.Solid && s.Texture != null)
                    .Select(s => s.Texture).Distinct()
                    .Where(t => textureUsers.TryGetValue(t, out var users) && users.Any(u => !group.Materials.Contains(u)))
                    .Select(t => t.name).ToList();
                if (sharedOutside.Count > 0)
                {
                    var why = "元テクスチャ（" + string.Join(", ", sharedOutside.Take(3)) + "）が統合対象外の Material でも使われるため、アトラスへ複製するとテクスチャメモリが増えます";
                    if (!force)
                    {
                        foreach (var e in g)
                        {
                            e.Item.included = false;
                            e.Item.reason = AppendReason(e.Item.reason, why);
                        }
                        continue;
                    }
                    plan.ForceRisks.Add("グループ" + data.Groups.Count + ": " + why);
                }
                try
                {
                    group.Pack = AtlasPacker.Pack(group.Sizes, context.Settings.atlasPadding, context.Settings.atlasMaxSize);
                    if (group.Pack.Scale < 1f)
                        plan.Notes.Add("グループ" + group.Index + ": 最大サイズ " + context.Settings.atlasMaxSize + " に収めるため一律 " + group.Pack.Scale.ToString("0.00") + " 倍に縮小します。");
                }
                catch (InvalidOperationException ex)
                {
                    foreach (var e in g)
                    {
                        e.Item.included = false;
                        e.Item.reason = AppendReason(e.Item.reason, ex.Message);
                    }
                    continue;
                }

                if (group.MixedProperties)
                    plan.ForceRisks.Add("グループ" + group.Index + ": 非テクスチャプロパティが異なる Material を統合します（" + materials[0].name + " の値を使用）");
                data.Groups.Add(group);

                foreach (var e in g)
                {
                    var key = e.Renderer.Key;
                    data.Renderers[key] = e.Renderer;
                    if (!data.Assignments.TryGetValue(key, out var perSubmesh))
                        data.Assignments[key] = perSubmesh = new Dictionary<int, AtlasGroup>();
                    perSubmesh[e.Submesh] = group;
                    e.Item.reason = AppendReason(e.Item.reason, "グループ" + group.Index);
                }
            }

            data.ProcessedRendererKeys.AddRange(data.Assignments.Keys.OrderBy(k => k, StringComparer.Ordinal));
            plan.ExpectedEffect = data.Groups.Count == 0
                ? "統合可能な Material グループなし"
                : string.Join(" / ", data.Groups.Select(gr =>
                    "グループ" + gr.Index + ": Material " + gr.Materials.Count + "個 → 1個, " +
                    gr.Pack.Width + "x" + gr.Pack.Height + " × " + gr.Channels.Count + "ch（使用率 " + (gr.Pack.FillRatio * 100).ToString("0") + "%" +
                    (gr.Pack.Scale < 1f ? "、縮小率 " + gr.Pack.Scale.ToString("0.00") : string.Empty) + "）" +
                    (gr.Emission != null ? "、発光を焼き込み" : string.Empty) +
                    (gr.ParameterBakes.Count > 0 ? "、" + string.Join("・", gr.ParameterBakes.Select(b => b.Texture)) + " に数値を焼き込み" : string.Empty) +
                    (gr.FilledChannels.Count > 0 ? "、" + string.Join("・", gr.FilledChannels.Keys) + " のない Material は既定値で補完" : string.Empty)));
            return plan;
        }

        static string AppendReason(string reason, string add) => string.IsNullOrEmpty(reason) ? add : reason + " / " + add;

        /// <summary>既定値で埋められるチャンネルの Importer 設定が、テクスチャがある Material 同士で一致するように分ける（決定的）。</summary>
        static IEnumerable<List<SubmeshEntry>> PartitionByFillableChannels(List<SubmeshEntry> entries)
        {
            var parts = new List<(Dictionary<string, string> keys, List<SubmeshEntry> entries)>();
            foreach (var byMaterial in entries.GroupBy(e => e.Candidate.Material).OrderBy(x => MaterialEquivalence.StableId(x.Key), StringComparer.Ordinal))
            {
                var keys = byMaterial.First().Candidate.FillableKeys;
                var target = parts.FirstOrDefault(p => keys.All(k => !p.keys.TryGetValue(k.Key, out var v) || v == k.Value));
                if (target.entries == null)
                {
                    target = (new Dictionary<string, string>(), new List<SubmeshEntry>());
                    parts.Add(target);
                }
                foreach (var k in keys) target.keys[k.Key] = k.Value;
                target.entries.AddRange(byMaterial);
            }
            return parts.Select(p => p.entries);
        }

        /// <summary>テクスチャのない Material の領域: シェーダーの既定テクスチャ（未割り当て時に使われる値）の単色。</summary>
        static TextureSourceInfo FillSource(Material material, AtlasChannel channel)
        {
            Color color;
            if (channel.Kind == AtlasChannelKind.Normal) color = new Color(0.5f, 0.5f, 1f, 1f);
            else
            {
                var shader = material.shader;
                int index = shader.FindPropertyIndex(channel.Property);
                switch (index >= 0 ? shader.GetPropertyTextureDefaultName(index) : "white")
                {
                    case "black": color = new Color(0, 0, 0, 0); break;
                    case "gray": color = new Color(0.5f, 0.5f, 0.5f, 0.5f); break;
                    case "red": color = new Color(1, 0, 0, 0); break;
                    case "bump": color = new Color(0.5f, 0.5f, 1f, 0.5f); break;
                    default: color = Color.white; break;
                }
            }
            var src = TextureSourceInfo.SolidSource(color);
            src.SRgb = channel.Kind == AtlasChannelKind.Color;
            src.IsNormal = channel.Kind == AtlasChannelKind.Normal;
            return src;
        }

        /// <summary>
        /// 発光の焼き込み: アトラス Material の発光色を「白 × 最大強度」にし、各領域にはそれで割った色を掛ける。
        /// Linear 色空間では、シェーダーが受け取る線形値で比を取る（色は Material にガンマ値で保存されている）。
        /// </summary>
        static void PlanEmission(AtlasGroup group, List<MaterialCandidate> candidates)
        {
            if (group.Emission == null) return;
            bool linear = UnityEditor.PlayerSettings.colorSpace == ColorSpace.Linear;
            float ToShader(float v) => linear ? ColorBake.SrgbToLinear(v) : v;
            float FromShader(float v) => linear ? ColorBake.LinearToSrgb(v) : v;
            var on = candidates.Where(c => c.EmissionOn).ToList();
            float max = on.SelectMany(c => new[] { ToShader(c.EmissionColor.r), ToShader(c.EmissionColor.g), ToShader(c.EmissionColor.b) }).DefaultIfEmpty(0f).Max();
            if (max <= 0f) max = 1f;
            float atlas = FromShader(max);
            group.EmissionAtlasColor = new Color(atlas, atlas, atlas, 1f);
            group.EmissionReference = on.Select(c => c.Material).FirstOrDefault();
            foreach (var c in on)
                group.EmissionFactors[c.Material] = new Color(
                    FromShader(ToShader(c.EmissionColor.r) / max), FromShader(ToShader(c.EmissionColor.g) / max), FromShader(ToShader(c.EmissionColor.b) / max),
                    Mathf.Clamp01(c.EmissionColor.a));
        }

        /// <summary>
        /// 各 Material の領域サイズ。オブジェクトの大きさ（使用面のワールド面積）に対して解像度が過剰なテクスチャは、
        /// グループの中央値のテクセル密度まで縮める（拡大はしない）。単色の領域は対象外。
        /// </summary>
        static List<Vector2> BalancedSizes(List<MaterialCandidate> candidates, List<SubmeshEntry> entries, bool balance)
        {
            foreach (var c in candidates)
            {
                c.WorldArea = 0;
                c.UvArea = 0;
                foreach (var e in entries.Where(e => e.Candidate == c))
                {
                    var (world, uv) = SurfaceArea(e.Renderer, e.Submesh);
                    c.WorldArea += world;
                    c.UvArea += uv;
                }
            }
            var sizes = candidates.Select(c => (Vector2)c.Size).ToList();
            if (!balance) return sizes;
            bool Textured(MaterialCandidate c) => c.Sources.Values.Any(s => !s.Solid) && c.WorldArea > 1e-8;
            double Density(MaterialCandidate c) => (double)c.Size.x * c.Size.y * Math.Min(1.0, Math.Max(c.UvArea, 1e-4)) / c.WorldArea;
            var densities = candidates.Where(Textured).Select(Density).OrderBy(d => d).ToList();
            if (densities.Count < 2) return sizes;
            double median = densities[(densities.Count - 1) / 2]; // 下側の中央値（2 つなら小さい方に揃える）
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!Textured(candidates[i])) continue;
                double d = Density(candidates[i]);
                if (d <= median) continue;
                float r = (float)Math.Sqrt(median / d);
                sizes[i] = new Vector2(Math.Max(16f, sizes[i].x * r), Math.Max(16f, sizes[i].y * r));
            }
            return sizes;
        }

        /// <summary>サブメッシュのワールド面積（Renderer の変換を適用。SkinnedMesh はバインドポーズ）と UV0 の面積。</summary>
        static (double world, double uv) SurfaceArea(RendererInfo info, int submesh)
        {
            var mesh = info.Mesh;
            if (mesh == null || submesh >= mesh.subMeshCount || mesh.GetTopology(submesh) != MeshTopology.Triangles) return (0, 0);
            var vertices = mesh.vertices;
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            var m = info.Renderer.transform.localToWorldMatrix;
            var tris = mesh.GetTriangles(submesh);
            double world = 0, uv = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var a = m.MultiplyPoint3x4(vertices[tris[t]]);
                var b = m.MultiplyPoint3x4(vertices[tris[t + 1]]);
                var c = m.MultiplyPoint3x4(vertices[tris[t + 2]]);
                world += Vector3.Cross(b - a, c - a).magnitude * 0.5;
                if (uvs.Count == vertices.Length)
                {
                    var ua = uvs[tris[t]];
                    var ub = uvs[tris[t + 1]];
                    var uc = uvs[tris[t + 2]];
                    uv += Math.Abs((ub.x - ua.x) * (uc.y - ua.y) - (uc.x - ua.x) * (ub.y - ua.y)) * 0.5;
                }
            }
            return (world, uv);
        }

        public override void ApplyToContext(OperationPlan plan, OptimizationContext context)
        {
            var data = (AtlasPlanData)plan.Data;
            context.PlannedGeneratedTextureCount += data.Groups.Sum(g => g.Channels.Count);
        }

        public override void Execute(GenerationContext gen, OperationPlan plan) =>
            BackgroundJob.RunToCompletion(ExecuteRoutine(gen, plan));

        public IEnumerator ExecuteRoutine(GenerationContext gen, OperationPlan plan)
        {
            var data = (AtlasPlanData)plan.Data;
            var pixelCache = new Dictionary<Texture2D, (Color32[] px, int w, int h)>();

            foreach (var group in data.Groups)
            {
                gen.ThrowIfCancelled();
                yield return BuildAtlas(gen, group, pixelCache);
            }
            pixelCache.Clear();

            var meshCache = new Dictionary<string, Mesh>();
            int count = 0;
            foreach (var key in data.ProcessedRendererKeys)
            {
                gen.ThrowIfCancelled();
                var clone = gen.Copy.FindRenderer(key);
                if (clone == null) throw new InvalidOperationException("ワーキングコピー上の Renderer が見つかりません: " + key);
                ApplyToRenderer(gen, clone, data.Assignments[key], meshCache, key);
                if (++count % 4 == 0) yield return null;
            }
        }

        IEnumerator BuildAtlas(GenerationContext gen, AtlasGroup group, Dictionary<Texture2D, (Color32[] px, int w, int h)> cache)
        {
            var pack = group.Pack;
            var first = group.Materials[0];
            var atlasMaterial = new Material(first) { name = "Atlas_" + group.Index + "_" + first.name };
            bool linearSpace = UnityEditor.PlayerSettings.colorSpace == ColorSpace.Linear;

            foreach (var channel in group.Channels)
            {
                var sources = new List<AtlasComposer.Source>();
                bool hasAlpha = false;
                var refInfo = group.Materials.Select(m => group.Sources[m][channel.Property]).FirstOrDefault(x => !x.Solid)
                              ?? group.Sources[first][channel.Property];
                bool opaqueMain = group.MainAlphaUnused && channel.Property == group.Adapter.MainColorTexture;
                for (int i = 0; i < group.Materials.Count; i++)
                {
                    var src = group.Sources[group.Materials[i]][channel.Property];
                    (Color32[] px, int w, int h) px;
                    if (src.Solid)
                    {
                        // 単色の値は src.SRgb の空間で表されている。チャンネルのテクスチャと空間が違えば（Linear 色空間のみ）変換する
                        var c = (Color)src.SolidColor;
                        if (linearSpace && src.SRgb && !refInfo.SRgb) c = new Color(ColorBake.SrgbToLinear(c.r), ColorBake.SrgbToLinear(c.g), ColorBake.SrgbToLinear(c.b), c.a);
                        else if (linearSpace && !src.SRgb && refInfo.SRgb) c = new Color(ColorBake.LinearToSrgb(c.r), ColorBake.LinearToSrgb(c.g), ColorBake.LinearToSrgb(c.b), c.a);
                        px = (new[] { (Color32)c }, 1, 1);
                    }
                    else if (!cache.TryGetValue(src.Texture, out px))
                    {
                        var pixels = TextureReader.ReadPixels(src, out int w, out int h);
                        px = (pixels, w, h);
                        cache[src.Texture] = px;
                        yield return null;
                    }
                    // 焼き込む数値の倍率（Smoothness × _GlossMapScale 等）をアルファへ掛ける
                    if (group.AlphaScales.TryGetValue((group.Materials[i], channel.Property), out var alphaScale))
                    {
                        Color32[] scaled = null;
                        var source = px.px;
                        yield return Task.Run(() => scaled = source.Select(p => new Color32(p.r, p.g, p.b, (byte)Math.Max(0, Math.Min(255, Math.Round(p.a * alphaScale))))).ToArray());
                        px = (scaled, px.w, px.h);
                    }
                    // 発光色を発光マップへ乗算して焼き込む（HDR の強さはアトラス Material の発光色に移している）
                    if (group.Emission != null && channel.Property == group.Emission.Texture && group.EmissionFactors.TryGetValue(group.Materials[i], out var factor))
                    {
                        Color32[] baked = null;
                        var source = px.px;
                        bool srgb = src.SRgb;
                        yield return Task.Run(() => baked = ColorBake.Multiply(source, factor, srgb, linearSpace));
                        px = (baked, px.w, px.h);
                    }
                    // メインカラーをテクスチャへ乗算して焼き込む（キャッシュは変更しない）
                    if (channel.Property == group.ColorTexture && group.BakeColors.TryGetValue(group.Materials[i], out var bake) && bake.HasValue)
                    {
                        Color32[] baked = null;
                        var source = px.px;
                        var color = bake.Value;
                        bool srgb = src.SRgb;
                        yield return Task.Run(() => baked = ColorBake.Multiply(source, color, srgb, linearSpace));
                        px = (baked, px.w, px.h);
                    }
                    if (!hasAlpha && !opaqueMain) hasAlpha = px.px.Any(c => c.a < 255);
                    sources.Add(new AtlasComposer.Source { Pixels = px.px, Width = px.w, Height = px.h, Destination = pack.Rects[i] });
                }

                // チャンネルごとに必要十分な解像度にする: 元テクスチャの解像度を下回らず、パディングが 1px 以上残る範囲で 1/2 ずつ縮める
                // （単色の領域しかない発光マスク等は小さくなる。配置は同じ比率なので UV は共通）
                int shift = 0;
                int padding = gen.Settings.atlasPadding;
                var realSources = Enumerable.Range(0, group.Materials.Count)
                    .Select(i => (src: group.Sources[group.Materials[i]][channel.Property], rect: pack.Rects[i])).Where(x => !x.src.Solid).ToList();
                while (shift < 6 && (padding >> (shift + 1)) >= 1 && Math.Min(pack.Width, pack.Height) >> (shift + 1) >= 16 &&
                       realSources.All(x => x.rect.width >> (shift + 1) >= x.src.Width && x.rect.height >> (shift + 1) >= x.src.Height))
                    shift++;
                int width = pack.Width >> shift, height = pack.Height >> shift, channelPadding = padding >> shift;
                if (shift > 0)
                    for (int i = 0; i < sources.Count; i++)
                    {
                        var r = sources[i].Destination;
                        int x0 = r.x >> shift, y0 = r.y >> shift;
                        int x1 = (r.xMax + (1 << shift) - 1) >> shift, y1 = (r.yMax + (1 << shift) - 1) >> shift;
                        var scaled = sources[i];
                        scaled.Destination = new RectInt(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
                        sources[i] = scaled;
                    }

                var fill = channel.Kind == AtlasChannelKind.Normal ? new Color32(128, 128, 255, 255) : new Color32(0, 0, 0, 0);
                // ピクセルの合成は Unity API を使わないため別スレッドで行う
                Color32[] composed = null;
                yield return Task.Run(() =>
                {
                    composed = AtlasComposer.Compose(width, height, sources, channelPadding, fill);
                    // アルファがどの Material でも使われないなら不透明にする（JPEG・アルファなしの圧縮で軽くなる）
                    if (opaqueMain)
                        for (int p = 0; p < composed.Length; p++) composed[p].a = 255;
                });
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, !refInfo.SRgb)
                {
                    name = "Atlas_" + group.Index + channel.Property,
                };
                tex.SetPixels32(composed);
                tex.Apply(false, false);

                var record = new GeneratedTexture
                {
                    OperationId = Id,
                    SourceRef = string.Join(",", group.Materials.Select(MaterialEquivalence.StableId)),
                    HasAlpha = hasAlpha,
                };
                record.Import.NormalMap = channel.Kind == AtlasChannelKind.Normal;
                record.Import.SRgb = refInfo.SRgb;
                record.Import.AlphaFromInput = refInfo.AlphaFromInput;
                record.Import.FlipGreenChannel = refInfo.FlipGreen;
                record.Import.Filter = refInfo.Filter;
                record.Import.AnisoLevel = refInfo.AnisoLevel;
                record.Import.MaxSize = Math.Max(width, height);
                if (shift > 0) gen.Log.Info(Id, "グループ" + group.Index + " " + channel.Property + ": 必要十分な解像度 " + width + "x" + height + "（配置は " + pack.Width + "x" + pack.Height + " と共通）");

                var saved = gen.AddTexture(tex, record, tex.name);
                atlasMaterial.SetTexture(channel.Property, saved);
                atlasMaterial.SetTextureScale(channel.Property, Vector2.one);
                atlasMaterial.SetTextureOffset(channel.Property, Vector2.zero);
            }

            if (group.ColorProperty != null) atlasMaterial.SetColor(group.ColorProperty, Color.white);
            foreach (var keyword in group.Keywords) atlasMaterial.EnableKeyword(keyword);
            foreach (var bake in group.ParameterBakes) bake.Apply(atlasMaterial);
            if (group.Emission != null && group.EmissionReference != null) group.Emission.Apply(atlasMaterial, group.EmissionAtlasColor, group.EmissionReference);
            group.AtlasMaterial = gen.AddAsset(atlasMaterial, "Materials", atlasMaterial.name, Id, MaterialEquivalence.StableId(first));
            for (int i = 0; i < group.Materials.Count; i++)
            {
                var r = pack.Rects[i];
                group.Mappings[group.Materials[i]] = new SubmeshMapping
                {
                    TransformId = group.Index * 100000 + i + 1,
                    Offset = new Vector2((float)r.x / pack.Width, (float)r.y / pack.Height),
                    Scale = new Vector2((float)r.width / pack.Width, (float)r.height / pack.Height),
                };
            }
        }

        void ApplyToRenderer(GenerationContext gen, Renderer clone, Dictionary<int, AtlasGroup> assignments, Dictionary<string, Mesh> meshCache, string key)
        {
            Mesh srcMesh = clone is SkinnedMeshRenderer smr ? smr.sharedMesh : clone.GetComponent<MeshFilter>()?.sharedMesh;
            if (srcMesh == null) throw new InvalidOperationException("Mesh が見つかりません: " + key);
            var current = clone.sharedMaterials;
            if (current.Length != srcMesh.subMeshCount) throw new InvalidOperationException("スロット数が計画時と異なります: " + key);

            var mapping = new SubmeshMapping[srcMesh.subMeshCount];
            var slotKeys = new List<string>();
            var newMaterials = new List<Material>();
            for (int s = 0; s < current.Length; s++)
            {
                string slotKey;
                Material slotMaterial;
                if (assignments.TryGetValue(s, out var group))
                {
                    if (!group.Mappings.TryGetValue(current[s], out var m))
                        throw new InvalidOperationException("計画時と異なる Material が割り当てられています: " + key + " slot " + s);
                    mapping[s] = m;
                    slotKey = "group:" + group.Index;
                    slotMaterial = group.AtlasMaterial;
                }
                else
                {
                    mapping[s] = new SubmeshMapping { TransformId = 0 };
                    slotKey = "slot:" + s;
                    slotMaterial = current[s];
                }
                int slot = slotKeys.IndexOf(slotKey);
                if (slot < 0)
                {
                    slot = slotKeys.Count;
                    slotKeys.Add(slotKey);
                    newMaterials.Add(slotMaterial);
                }
                mapping[s].TargetSlot = slot;
            }

            var cacheKey = srcMesh.GetInstanceID() + "|" + string.Join(",", mapping.Select(m => m.TransformId + ">" + m.TargetSlot));
            if (!meshCache.TryGetValue(cacheKey, out var newMesh))
            {
                newMesh = AtlasMeshBuilder.Build(srcMesh, mapping, slotKeys.Count);
                newMesh = gen.AddAsset(newMesh, "Meshes", newMesh.name, Id, MaterialEquivalence.StableId(srcMesh));
                meshCache[cacheKey] = newMesh;
            }

            if (clone is SkinnedMeshRenderer skinned)
            {
                var weights = new float[srcMesh.blendShapeCount];
                for (int i = 0; i < weights.Length; i++) weights[i] = skinned.GetBlendShapeWeight(i);
                skinned.sharedMesh = newMesh;
                for (int i = 0; i < weights.Length && i < newMesh.blendShapeCount; i++) skinned.SetBlendShapeWeight(i, weights[i]);
            }
            else
            {
                clone.GetComponent<MeshFilter>().sharedMesh = newMesh;
            }
            clone.sharedMaterials = newMaterials.ToArray();
        }

        public override ValidationResult Validate(GenerationContext gen, OperationPlan plan)
        {
            var result = new ValidationResult();
            var data = (AtlasPlanData)plan.Data;
            foreach (var group in data.Groups)
            {
                if (group.AtlasMaterial == null) result.Fail("グループ" + group.Index + " のアトラス Material が生成されていません。");
                foreach (var channel in group.Channels)
                    if (group.AtlasMaterial != null && group.AtlasMaterial.GetTexture(channel.Property) == null)
                        result.Fail("グループ" + group.Index + " の " + channel.Property + " が未設定です。");
            }

            foreach (var key in data.ProcessedRendererKeys)
            {
                var clone = gen.Copy.FindRenderer(key);
                var original = data.Renderers[key];
                if (clone == null)
                {
                    result.Fail(key + ": Renderer が見つかりません。");
                    continue;
                }
                Mesh mesh = clone is SkinnedMeshRenderer smr ? smr.sharedMesh : clone.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null)
                {
                    result.Fail(key + ": Mesh が失われています。");
                    continue;
                }
                if (TriangleCount(mesh) != TriangleCount(original.Mesh))
                    result.Fail(key + ": 三角形数が変化しました（" + TriangleCount(original.Mesh) + " → " + TriangleCount(mesh) + "）。");
                if (mesh.blendShapeCount != original.Mesh.blendShapeCount)
                    result.Fail(key + ": BlendShape 数が変化しました。");
                if (clone.sharedMaterials.Length != mesh.subMeshCount || clone.sharedMaterials.Any(m => m == null))
                    result.Fail(key + ": Material スロットが不正です。");
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (AtlasMeshBuilder.TryGetSubmeshUvBounds(mesh, s, out var min, out var max) &&
                        (min.x < -UvEpsilon || min.y < -UvEpsilon || max.x > 1 + UvEpsilon || max.y > 1 + UvEpsilon))
                        result.Fail(key + ": 変換後 UV が範囲外です。");
            }
            return result;
        }

        /// <summary>メインカラーの乗算をテクスチャへ焼き込む。Unity API を使わない（別スレッド可）。</summary>
        public static class ColorBake
        {
            public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);
            public static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1 / 2.4f) - 0.055f;

            /// <summary>
            /// Linear 色空間では、シェーダーは sRGB テクスチャを線形化した値に線形化したカラーを掛けるため、線形空間で乗算して sRGB に戻す。
            /// Gamma 色空間（または非 sRGB テクスチャの Gamma 時）は値をそのまま乗算する。アルファは常に線形に乗算する。
            /// </summary>
            public static Color32[] Multiply(Color32[] source, Color color, bool srgbTexture, bool linearSpace)
            {
                var lut = new byte[3, 256];
                var factor = new[] { color.r, color.g, color.b };
                for (int ch = 0; ch < 3; ch++)
                {
                    float f = linearSpace ? SrgbToLinear(factor[ch]) : factor[ch];
                    for (int v = 0; v < 256; v++)
                    {
                        float t = v / 255f;
                        float r = linearSpace && srgbTexture ? LinearToSrgb(SrgbToLinear(t) * f) : t * f;
                        lut[ch, v] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(r * 255)));
                    }
                }
                var result = new Color32[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    var p = source[i];
                    result[i] = new Color32(lut[0, p.r], lut[1, p.g], lut[2, p.b], (byte)Math.Round(p.a * color.a));
                }
                return result;
            }
        }

        static long TriangleCount(Mesh mesh)
        {
            long total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) total += mesh.GetIndexCount(s) / 3;
            return total;
        }
    }
}
