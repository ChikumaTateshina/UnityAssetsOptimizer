using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// テクスチャアトラス化 + Material 統合 + サブメッシュ統合。
    /// 対応 Shader Adapter・単位 ST・UV0 が [0,1] 内・読み取り可能なテクスチャの組み合わせのみを処理する。
    /// </summary>
    public sealed class TextureAtlasOperation : OptimizationOperationBase
    {
        const float UvEpsilon = 1e-4f;

        public override string Id => FeatureIds.Atlas;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            CanForce = true,
            Phase = 1,
            RunsAfter = new[] { FeatureIds.MaterialDedupe },
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
            public List<Vector2Int> Sizes = new List<Vector2Int>();
            public AtlasPackResult Pack;
            public bool MixedProperties;

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

            var importKeys = new List<string>();
            foreach (var channel in cand.Adapter.Channels)
            {
                if (!mat.HasProperty(channel.Property)) continue;
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
                importKeys.Add(channel.Property + "=" + src.ImportKey + ":" + src.Filter + ":" + src.AnisoLevel);
            }
            if (cand.Channels.Count == 0)
                return Reject(cand, SupportState.Unsupported, "アトラス化するテクスチャがありません");

            foreach (var shared in cand.Adapter.SharedTextures)
                if (mat.HasProperty(shared))
                    importKeys.Add(shared + "=shared:" + TextureIdentity.Key(mat.GetTexture(shared), contentDedupe) + ":" +
                                   mat.GetTextureScale(shared) + mat.GetTextureOffset(shared));
            var tail = cand.Adapter.Id + "#" + cand.Adapter.Version + "|" + string.Join(";", importKeys);
            cand.StrictKey = tail + "|" + MaterialEquivalence.Key(mat, contentDedupe, true, false);
            cand.LooseKey = tail + "|" + MaterialEquivalence.Key(mat, contentDedupe, false, false);

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
            var byKey = included
                .GroupBy(e => force ? e.Candidate.LooseKey : e.Candidate.StrictKey)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var g in byKey)
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
                var group = new AtlasGroup { Index = data.Groups.Count, Adapter = first.Adapter, Channels = first.Channels.ToList() };
                foreach (var m in materials)
                {
                    var cand = g.First(e => e.Candidate.Material == m).Candidate;
                    group.Materials.Add(m);
                    group.Sources[m] = cand.Sources;
                    group.Sizes.Add(cand.Size);
                }
                group.MixedProperties = g.Select(e => e.Candidate.StrictKey).Distinct().Count() > 1;
                try
                {
                    group.Pack = AtlasPacker.Pack(group.Sizes, context.Settings.atlasPadding, context.Settings.atlasMaxSize);
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
                    gr.Pack.Width + "x" + gr.Pack.Height + " × " + gr.Channels.Count + "ch" +
                    (gr.Pack.Scale < 1f ? "（縮小率 " + gr.Pack.Scale + "）" : string.Empty)));
            return plan;
        }

        static string AppendReason(string reason, string add) => string.IsNullOrEmpty(reason) ? add : reason + " / " + add;

        public override void ApplyToContext(OperationPlan plan, OptimizationContext context)
        {
            var data = (AtlasPlanData)plan.Data;
            context.PlannedGeneratedTextureCount += data.Groups.Sum(g => g.Channels.Count);
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var data = (AtlasPlanData)plan.Data;
            var pixelCache = new Dictionary<Texture2D, (Color32[] px, int w, int h)>();

            foreach (var group in data.Groups)
            {
                gen.ThrowIfCancelled();
                BuildAtlas(gen, group, pixelCache);
            }
            pixelCache.Clear();

            var meshCache = new Dictionary<string, Mesh>();
            foreach (var key in data.ProcessedRendererKeys)
            {
                gen.ThrowIfCancelled();
                var clone = gen.Copy.FindRenderer(key);
                if (clone == null) throw new InvalidOperationException("ワーキングコピー上の Renderer が見つかりません: " + key);
                ApplyToRenderer(gen, clone, data.Assignments[key], meshCache, key);
            }
        }

        void BuildAtlas(GenerationContext gen, AtlasGroup group, Dictionary<Texture2D, (Color32[] px, int w, int h)> cache)
        {
            var pack = group.Pack;
            var first = group.Materials[0];
            var atlasMaterial = new Material(first) { name = "Atlas_" + group.Index + "_" + first.name };

            foreach (var channel in group.Channels)
            {
                var sources = new List<AtlasComposer.Source>();
                bool hasAlpha = false;
                for (int i = 0; i < group.Materials.Count; i++)
                {
                    var src = group.Sources[group.Materials[i]][channel.Property];
                    if (!cache.TryGetValue(src.Texture, out var px))
                    {
                        var pixels = TextureReader.ReadPixels(src, out int w, out int h);
                        px = (pixels, w, h);
                        cache[src.Texture] = px;
                    }
                    if (!hasAlpha) hasAlpha = px.px.Any(c => c.a < 255);
                    sources.Add(new AtlasComposer.Source { Pixels = px.px, Width = px.w, Height = px.h, Destination = pack.Rects[i] });
                }

                var fill = channel.Kind == AtlasChannelKind.Normal ? new Color32(128, 128, 255, 255) : new Color32(0, 0, 0, 0);
                var composed = AtlasComposer.Compose(pack.Width, pack.Height, sources, gen.Settings.atlasPadding, fill);
                var refInfo = group.Sources[first][channel.Property];
                var tex = new Texture2D(pack.Width, pack.Height, TextureFormat.RGBA32, false, !refInfo.SRgb)
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
                record.Import.MaxSize = Math.Max(pack.Width, pack.Height);

                var saved = gen.AddTexture(tex, record, tex.name);
                atlasMaterial.SetTexture(channel.Property, saved);
                atlasMaterial.SetTextureScale(channel.Property, Vector2.one);
                atlasMaterial.SetTextureOffset(channel.Property, Vector2.zero);
            }

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

        static long TriangleCount(Mesh mesh)
        {
            long total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) total += mesh.GetIndexCount(s) / 3;
            return total;
        }
    }
}
