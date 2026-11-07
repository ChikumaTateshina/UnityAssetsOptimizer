using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// メッシュ結合。同じ描画設定・頂点形式・静的状態・空間セル内の Renderer を1つにまとめる。
    /// アニメーション・スクリプト参照・Udon・他コンポーネントを持つものは対象外（参照が切れるため FORCE 不可）。
    /// 静的メッシュの Collider は Renderer と独立しているため、そのまま残して結合する。
    /// </summary>
    public sealed class MeshCombineOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.MeshCombine;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 2,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.MaterialDedupe, FeatureIds.LightmapBake },
            WritesAssetKinds = new[] { "Mesh" },
        };

        public sealed class CombineGroup
        {
            public bool Skinned;
            /// <summary>ライトマップを光テクスチャとして焼き込んだ Renderer の結合（UV1 は光テクスチャ用なので並べ直さない）。</summary>
            public bool LightBaked;
            public List<RendererInfo> Members = new List<RendererInfo>();
        }

        sealed class Candidate
        {
            public RendererInfo Info;
            public PlanItem Item;
            public string Key;
            public List<KeyValuePair<string, string>> KeyParts;
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var candidates = new List<Candidate>();
            var a = context.Analysis;
            foreach (var info in a.Renderers)
            {
                var label = info.Path.Length == 0 ? "(root)" : info.Path;
                var reason = Reject(info, context);
                if (reason != null)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, reason));
                    continue;
                }
                var item = PlanItem.Supported(label);
                analysis.Items.Add(item);
                var parts = GroupKeyParts(info, context);
                candidates.Add(new Candidate { Info = info, Item = item, KeyParts = parts, Key = string.Join("|", parts.Select(p => p.Value)) });
            }
            analysis.Data = candidates;
            analysis.Notes.Add("結合後の Prefab はライトマップ情報を持ちません（再ベイクが必要）。静的メッシュを結合した場合は、ライトマップ UV（UV1）が重ならないよう並べ直します（なければ生成）。");
            analysis.Notes.Add("結合された側の GameObject は残り、Renderer/MeshFilter のみ削除されます（Collider はそのまま残ります）。");
            return analysis;
        }

        static string Reject(RendererInfo info, OptimizationContext context)
        {
            var r = info.Renderer;
            var go = r.gameObject;
            bool skinned = r is SkinnedMeshRenderer;
            if (!(r is MeshRenderer) && !skinned) return "対象外の Renderer 種別";
            if (skinned && !context.Settings.meshCombineSkinned) return "SkinnedMesh の結合が無効（「メッシュ結合」の下の「SkinnedMesh も結合する」で有効にできます）";
            if (!r.enabled || !go.activeSelf) return "無効/非アクティブ";
            for (var t = r.transform.parent; t != null && t != context.Graph.Roots[info.RootIndex].transform; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return "親が非アクティブ";
                if (t.GetComponent<LODGroup>() != null) return "LODGroup 管理下";
            }
            if (r.GetComponent<LODGroup>() != null) return "LODGroup 管理下";
            if (info.Mesh == null) return "Mesh が未設定";
            if (info.Materials.Length != info.Mesh.subMeshCount || info.Materials.Any(m => m == null)) return "Material スロットが不正（未設定/マルチパス）";
            for (int s = 0; s < info.Mesh.subMeshCount; s++)
                if (info.Mesh.GetTopology(s) != MeshTopology.Triangles) return "三角形以外のトポロジー";
            if (info.AnimatedAny) return "自身または親がアニメーション対象（結合すると参照が切れる）";
            if (info.ScriptReferenced || info.ReferencedByAnyComponent || context.Analysis.ReferencedTransforms.Contains(r.transform))
                return "他のコンポーネントから参照されている";
            if (info.UdonOnSameObject) return "同一オブジェクトに UdonBehaviour";
            var allowed = skinned ? new[] { typeof(Transform), typeof(SkinnedMeshRenderer) } : new[] { typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer) };
            // 静的メッシュの Collider（MeshCollider は自身の sharedMesh を持つ）は Renderer を消しても動作が変わらない
            bool AllowedComponent(Component c) => c != null && (allowed.Contains(c.GetType()) || (!skinned && c is Collider));
            var other = go.GetComponents<Component>().FirstOrDefault(c => !AllowedComponent(c));
            if (other != null || go.GetComponents<Component>().Any(c => c == null))
                return "Renderer 以外のコンポーネントを持つ GameObject（" + (other == null ? "Missing Script" : other.GetType().Name) + "）";
            if (skinned)
            {
                var smr = (SkinnedMeshRenderer)r;
                if (smr.bones.Length == 0 || smr.bones.Any(b => b == null)) return "ボーン未設定（または欠落）の SkinnedMesh";
                if (info.Mesh.bindposes.Length != smr.bones.Length) return "ボーン数と bindpose 数が不一致";
                if (info.Mesh.GetBonesPerVertex().Length == 0) return "ボーンウェイトがありません";
            }
            return null;
        }

        /// <summary>結合条件（表示名と値）。すべて一致するものだけを同じグループにする。</summary>
        static List<KeyValuePair<string, string>> GroupKeyParts(RendererInfo info, OptimizationContext context)
        {
            var r = info.Renderer;
            var parts = new List<KeyValuePair<string, string>>();
            void Add(string label, object value) => parts.Add(new KeyValuePair<string, string>(label, value?.ToString() ?? "-"));
            Add("ルート", info.RootIndex);
            Add("種別", r is SkinnedMeshRenderer ? "SkinnedMesh" : "Mesh");
            Add("頂点形式", MeshMerger.AttributeSignature(info.Mesh));
            Add("レイヤー", LayerMask.LayerToName(r.gameObject.layer) is var layer && layer.Length > 0 ? layer : r.gameObject.layer.ToString());
            Add("Static 設定", GameObjectUtility.GetStaticEditorFlags(r.gameObject));
            Add("Cast Shadows", r.shadowCastingMode);
            Add("Receive Shadows", r.receiveShadows);
            Add("Light Probes", r.lightProbeUsage);
            Add("Reflection Probes", r.reflectionProbeUsage);
            Add("Anchor Override", r.probeAnchor != null ? r.probeAnchor.name + "#" + r.probeAnchor.GetInstanceID() : "-");
            Add("Motion Vectors", r.motionVectorGenerationMode);
            Add("Sorting Layer", r.sortingLayerID);
            Add("Order in Layer", r.sortingOrder);
            Add("Rendering Layer Mask", r.renderingLayerMask);
            Add("Dynamic Occlusion", r.allowOcclusionWhenDynamic);
            if (r is SkinnedMeshRenderer smr)
            {
                Add("Quality", smr.quality);
                Add("Update When Offscreen", smr.updateWhenOffscreen);
                Add("Skinned Motion Vectors", smr.skinnedMotionVectors);
            }
            else
            {
                var c = r.bounds.center / Math.Max(0.01f, context.Settings.meshCombineCellSize);
                Add("空間セル（" + context.Settings.meshCombineCellSize + "m）", Mathf.FloorToInt(c.x) + "," + Mathf.FloorToInt(c.y) + "," + Mathf.FloorToInt(c.z));
            }
            return parts;
        }

        /// <summary>相手が見つからない Renderer について、最も条件の近い候補との違いを説明する。</summary>
        static string NoPartnerReason(Candidate c, List<Candidate> all, bool blendShapeConflict)
        {
            if (blendShapeConflict) return "同じ条件の Renderer と BlendShape 名が重複するため結合できません";
            var others = all.Where(o => o != c && o.Info.RootIndex == c.Info.RootIndex).ToList();
            if (others.Count == 0) return "結合できる他の Renderer がありません（他はすべて対象外）";
            var nearest = others
                .Select(o => new { Other = o, Diff = c.KeyParts.Where((p, i) => p.Value != o.KeyParts[i].Value).Select(p => p.Key).ToList() })
                .OrderBy(x => x.Diff.Count).First();
            return "結合条件が一致する相手がありません（最も近い " + nearest.Other.Info.Renderer.name + " とは " + string.Join("・", nearest.Diff) + " が異なる）";
        }

        public override OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            var groups = new List<CombineGroup>();
            var included = ((List<Candidate>)analysis.Data).Where(c => c.Item.included).ToList();
            foreach (var g in included.GroupBy(c => c.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var ordered = g.OrderBy(c => c.Info.Key, StringComparer.Ordinal).ToList();
                var partitions = new List<List<Candidate>>();
                foreach (var c in ordered)
                {
                    // BlendShape 名が衝突しない最初のグループへ入れる
                    var names = BlendShapeNames(c.Info.Mesh);
                    var target = partitions.FirstOrDefault(p => !p.Any(x => BlendShapeNames(x.Info.Mesh).Overlaps(names)));
                    if (target == null) partitions.Add(target = new List<Candidate>());
                    target.Add(c);
                }
                foreach (var p in partitions)
                {
                    if (p.Count < 2)
                    {
                        p[0].Item.included = false;
                        p[0].Item.reason = NoPartnerReason(p[0], included, partitions.Count > 1);
                        continue;
                    }
                    var group = new CombineGroup
                    {
                        Skinned = p[0].Info.Renderer is SkinnedMeshRenderer,
                        LightBaked = context.LightBakedRendererKeys != null && p.Any(c => context.LightBakedRendererKeys.Contains(c.Info.Key)),
                    };
                    group.Members.AddRange(p.Select(c => c.Info));
                    foreach (var c in p) c.Item.reason = "グループ" + groups.Count + (c == p[0] ? "（結合先）" : string.Empty);
                    groups.Add(group);
                }
            }
            plan.Data = groups;
            plan.ExpectedEffect = groups.Count == 0 ? "結合可能なグループなし" :
                "Renderer " + groups.Sum(g => g.Members.Count) + " 個 → " + groups.Count + " 個";
            return plan;
        }

        static HashSet<string> BlendShapeNames(Mesh m)
        {
            var set = new HashSet<string>();
            for (int i = 0; i < m.blendShapeCount; i++) set.Add(m.GetBlendShapeName(i));
            return set;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            foreach (var group in (List<CombineGroup>)plan.Data)
            {
                gen.ThrowIfCancelled();
                var renderers = group.Members.Select(m => gen.Copy.FindRenderer(m.Key)).ToList();
                if (renderers.Any(r => r == null)) throw new InvalidOperationException("結合対象の Renderer が見つかりません。");
                var target = renderers[0];

                var sources = new List<MergeSource>();
                foreach (var r in renderers)
                {
                    var source = new MergeSource { Materials = r.sharedMaterials };
                    if (r is SkinnedMeshRenderer smr)
                    {
                        source.Mesh = smr.sharedMesh;
                        source.Bones = smr.bones;
                        source.BlendShapeWeights = Enumerable.Range(0, smr.sharedMesh.blendShapeCount).Select(smr.GetBlendShapeWeight).ToArray();
                    }
                    else
                    {
                        source.Mesh = r.GetComponent<MeshFilter>().sharedMesh;
                        source.ToTarget = target.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    }
                    sources.Add(source);
                }

                // 結合前にワールド境界を取得（スキンの localBounds 再計算用）
                var worldBounds = renderers.Select(r => r.bounds).ToList();
                var result = MeshMerger.Merge(sources, group.Skinned, target.name + "_combined");
                // 各ソースのライトマップ UV が重ならないよう並べ直す（重なるとベイクした光が混ざる）。
                // 変換後にシーンで Static にしてベイクする使い方もあるため、Contribute GI の有無にかかわらず行う
                if (!group.Skinned && !group.LightBaked)
                    MeshMerger.PrepareLightmapUvs(result.Mesh, result.VertexOffsets);
                var mesh = gen.AddAsset(result.Mesh, "Meshes", result.Mesh.name, Id, string.Join(",", group.Members.Select(m => m.Key)));

                if (target is SkinnedMeshRenderer targetSmr)
                {
                    targetSmr.sharedMesh = mesh;
                    targetSmr.bones = result.Bones;
                    for (int i = 0; i < result.BlendShapeWeights.Length; i++) targetSmr.SetBlendShapeWeight(i, result.BlendShapeWeights[i]);
                    var space = targetSmr.rootBone != null ? targetSmr.rootBone : targetSmr.transform;
                    var local = new Bounds();
                    bool init = false;
                    foreach (var b in worldBounds)
                        foreach (var corner in Corners(b))
                        {
                            var p = space.InverseTransformPoint(corner);
                            if (!init)
                            {
                                local = new Bounds(p, Vector3.zero);
                                init = true;
                            }
                            else local.Encapsulate(p);
                        }
                    targetSmr.localBounds = local;
                }
                else
                {
                    target.GetComponent<MeshFilter>().sharedMesh = mesh;
                }
                target.sharedMaterials = result.Materials;

                foreach (var r in renderers.Skip(1))
                {
                    int rootIndex = gen.Copy.RootIndexOf(r.transform);
                    var path = gen.Copy.PathOf(r.transform);
                    var go = r.gameObject;
                    var typeName = r.GetType().FullName;
                    Object.DestroyImmediate(r);
                    gen.RegisterRemoved(rootIndex, path, typeName, Id);
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        Object.DestroyImmediate(mf);
                        gen.RegisterRemoved(rootIndex, path, typeof(MeshFilter).FullName, Id);
                    }
                }
            }
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }

        public override ValidationResult Validate(GenerationContext gen, OperationPlan plan)
        {
            var result = new ValidationResult();
            foreach (var group in (List<CombineGroup>)plan.Data)
            {
                var target = gen.Copy.FindRenderer(group.Members[0].Key);
                var mesh = target is SkinnedMeshRenderer smr ? smr.sharedMesh : target?.GetComponent<MeshFilter>()?.sharedMesh;
                long expected = group.Members.Sum(m => Triangles(m.Mesh));
                if (mesh == null) result.Fail(group.Members[0].Key + ": 結合メッシュがありません。");
                else if (Triangles(mesh) != expected) result.Fail(group.Members[0].Key + ": 結合後の三角形数が一致しません（" + expected + " → " + Triangles(mesh) + "）。");
                foreach (var m in group.Members.Skip(1))
                    if (gen.Copy.FindRenderer(m.Key) != null) result.Fail(m.Key + ": 結合元の Renderer が残っています。");
            }
            return result;
        }

        public static long Triangles(Mesh mesh)
        {
            long total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) total += mesh.GetIndexCount(s) / 3;
            return total;
        }
    }

    /// <summary>Static Batching 支援（World）。動かない MeshRenderer に BatchingStatic を設定する。</summary>
    public sealed class StaticBatchingOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.StaticBatching;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Avatar = false,
            Phase = 3,
            RunsAfter = new[] { FeatureIds.MeshCombine },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var keys = new List<string>();
            foreach (var info in context.Analysis.Renderers)
            {
                var label = info.Path.Length == 0 ? "(root)" : info.Path;
                var reason = Reject(info, context);
                if (reason != null)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, reason));
                    continue;
                }
                analysis.Items.Add(PlanItem.Supported(label));
                keys.Add(info.Key);
            }
            analysis.Data = keys;
            analysis.Notes.Add("Static Batching はビルド時にメッシュを結合するため、メモリ使用量が増える場合があります。Draw Call 減少は保証せず測定比較してください。");
            return analysis;
        }

        static string Reject(RendererInfo info, OptimizationContext context)
        {
            var r = info.Renderer;
            if (!(r is MeshRenderer)) return "MeshRenderer 以外";
            if ((GameObjectUtility.GetStaticEditorFlags(r.gameObject) & StaticEditorFlags.BatchingStatic) != 0) return "既に Batching Static";
            if (info.AnimatedAny) return "自身または親がアニメーション対象（移動・切替の可能性）";
            if (info.ScriptReferenced || info.UdonOnSameObject || context.Analysis.ReferencedTransforms.Contains(r.transform))
                return "スクリプトから参照されている（移動の可能性を解析不能）";
            if (r.GetComponentInParent<Rigidbody>() != null) return "Rigidbody 配下";
            return null;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var keys = (List<string>)plan.Data;
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            foreach (var key in keys)
            {
                var r = gen.Copy.FindRenderer(key);
                if (r == null) continue; // 結合で削除済み
                var label = gen.Copy.PathOf(r.transform);
                if (!included.Contains(string.IsNullOrEmpty(label) ? "(root)" : label)) continue;
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags | StaticEditorFlags.BatchingStatic);
            }
        }
    }

    /// <summary>
    /// GPU Instancing 支援。生成後の構成で、Static Batching でない2つ以上の MeshRenderer が共有する Material の
    /// Instancing を有効化する（Material はコピーして変更）。シェーダーの対応可否を判定できない場合はスキップ。
    /// </summary>
    public sealed class GpuInstancingOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.GpuInstancing;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 3,
            RunsAfter = new[] { FeatureIds.StaticBatching, FeatureIds.MeshCombine, FeatureIds.Atlas },
        };

        static MethodInfo _hasInstancing;

        /// <summary>シェーダーが GPU Instancing に対応するか。判定できない場合は null。</summary>
        public static bool? SupportsInstancing(Shader shader)
        {
            if (shader == null) return null;
            if (_hasInstancing == null)
                _hasInstancing = typeof(ShaderUtil).GetMethod("HasInstancing", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Shader) }, null);
            if (_hasInstancing == null) return null;
            try
            {
                return (bool)_hasInstancing.Invoke(null, new object[] { shader });
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            var byMaterial = context.Analysis.Renderers
                .Where(r => r.Renderer is MeshRenderer)
                .SelectMany(r => r.Materials.Select(context.Canonical).Where(m => m != null).Distinct().Select(m => (m, r)))
                .GroupBy(x => x.m);
            foreach (var g in byMaterial.OrderBy(g => g.Key.name, StringComparer.Ordinal))
            {
                var m = g.Key;
                var support = SupportsInstancing(m.shader);
                if (m.enableInstancing) analysis.Items.Add(PlanItem.Skip(m.name, SupportState.Unsupported, "既に有効"));
                else if (support == null) analysis.Items.Add(PlanItem.Skip(m.name, SupportState.Unknown, "シェーダーの Instancing 対応を判定できません"));
                else if (support == false) analysis.Items.Add(PlanItem.Skip(m.name, SupportState.Unsupported, "シェーダーが Instancing 非対応"));
                else if (g.Count() < 2) analysis.Items.Add(PlanItem.Skip(m.name, SupportState.Unsupported, "共有する Renderer が1つ以下"));
                else analysis.Items.Add(PlanItem.Supported(m.name, "Renderer " + g.Count() + " 個で共有"));
            }
            analysis.Notes.Add("実行時は生成後の構成（アトラス・結合・Static Batching 適用後）で再判定します。Static Batching 対象の Renderer は Instancing されません。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var renderers = gen.Copy.AllRenderers.Select(p => p.Value)
                .Where(r => r is MeshRenderer && (GameObjectUtility.GetStaticEditorFlags(r.gameObject) & StaticEditorFlags.BatchingStatic) == 0)
                .ToList();
            var counts = renderers.SelectMany(r => r.sharedMaterials.Where(m => m != null).Distinct()).GroupBy(m => m).ToDictionary(g => g.Key, g => g.Count());
            var copies = new Dictionary<Material, Material>();
            foreach (var pair in counts.Where(p => p.Value >= 2 && !p.Key.enableInstancing && SupportsInstancing(p.Key.shader) == true).OrderBy(p => p.Key.name, StringComparer.Ordinal))
            {
                var m = pair.Key;
                var target = gen.IsGenerated(m) ? m : gen.AddAsset(new Material(m) { name = m.name }, "Materials", m.name + "_instanced", Id, MaterialEquivalence.StableId(m));
                target.enableInstancing = true;
                copies[m] = target;
            }
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && copies.TryGetValue(mats[i], out var c))
                    {
                        mats[i] = c;
                        changed = true;
                    }
                if (changed) r.sharedMaterials = mats;
            }
        }
    }

    /// <summary>Lightmap UV（UV1）の再生成。World・MeshRenderer のみ。メッシュはコピーして変更する。</summary>
    public sealed class LightmapUvOperation : OptimizationOperationBase
    {
        public override string Id => FeatureIds.LightmapUv;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Avatar = false,
            Phase = 3,
            RunsAfter = new[] { FeatureIds.MeshCombine, FeatureIds.Atlas, FeatureIds.PolygonReduction, FeatureIds.LightmapBake },
            WritesAssetKinds = new[] { "Mesh" },
        };

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            foreach (var info in context.Analysis.Renderers)
            {
                var label = info.Path.Length == 0 ? "(root)" : info.Path;
                if (!(info.Renderer is MeshRenderer) || info.Mesh == null) analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "MeshRenderer 以外/Mesh なし"));
                else if (context.LightBakedRendererKeys != null && context.LightBakedRendererKeys.Contains(info.Key))
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "ライトマップを焼き込んだ（UV1 は光テクスチャ用）"));
                else if ((GameObjectUtility.GetStaticEditorFlags(info.Renderer.gameObject) & StaticEditorFlags.ContributeGI) == 0)
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "Contribute GI が無効（ライトマップ対象外）"));
                else analysis.Items.Add(PlanItem.Supported(label, "UV1 を再生成（既存 UV1 は上書き）"));
            }
            analysis.Notes.Add("UV1 の再生成後はライトマップの再ベイクが必要です。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            var cache = new Dictionary<Mesh, Mesh>();
            foreach (var pair in gen.Copy.AllRenderers)
            {
                if (!(pair.Value is MeshRenderer r)) continue;
                var label = gen.Copy.PathOf(r.transform);
                if (!included.Contains(string.IsNullOrEmpty(label) ? "(root)" : label)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var src = mf.sharedMesh;
                if (!cache.TryGetValue(src, out var mesh))
                {
                    mesh = gen.IsGenerated(src) ? src : Object.Instantiate(src);
                    mesh.name = src.name + (gen.IsGenerated(src) ? string.Empty : "_lmuv");
                    Unwrapping.GenerateSecondaryUVSet(mesh);
                    if (!gen.IsGenerated(src)) mesh = gen.AddAsset(mesh, "Meshes", mesh.name, Id, MaterialEquivalence.StableId(src));
                    cache[src] = mesh;
                }
                mf.sharedMesh = mesh;
            }
        }
    }

    /// <summary>
    /// 不要頂点属性の削除。メッシュを使う全 Material のシェーダーアダプターが使用属性を宣言しており、
    /// 未使用が証明できた属性（頂点カラー・未使用 UV 等）のみを削除する。不明な場合はスキップ。
    /// </summary>
    public sealed class VertexAttributeStripOperation : OptimizationOperationBase
    {
        static readonly VertexAttribute[] Strippable =
        {
            VertexAttribute.Normal, VertexAttribute.Tangent, VertexAttribute.Color,
            VertexAttribute.TexCoord1, VertexAttribute.TexCoord2, VertexAttribute.TexCoord3,
            VertexAttribute.TexCoord4, VertexAttribute.TexCoord5, VertexAttribute.TexCoord6, VertexAttribute.TexCoord7,
        };

        public override string Id => FeatureIds.VertexAttributeStrip;

        public override OperationCapabilities Capabilities { get; } = new OperationCapabilities
        {
            Phase = 2,
            RunsAfter = new[] { FeatureIds.Atlas, FeatureIds.MeshCombine, FeatureIds.LightmapUv, FeatureIds.PolygonReduction, FeatureIds.LodGeneration },
            WritesAssetKinds = new[] { "Mesh" },
        };

        /// <summary>削除可能な属性。判定不能なら null と理由。</summary>
        public static List<VertexAttribute> Removable(Mesh mesh, Material[] materials, bool skinned, bool hasBlendShapes, ContentPreset preset, out string reason)
        {
            reason = null;
            var used = new HashSet<VertexAttribute>();
            foreach (var m in materials)
            {
                if (m == null) continue;
                var adapter = ShaderAdapterRegistry.Find(m.shader);
                var attrs = adapter?.UsedAttributes(m);
                if (attrs == null)
                {
                    reason = "使用属性を宣言していないシェーダー: " + (m.shader != null ? m.shader.name : "(none)");
                    return null;
                }
                used.UnionWith(attrs);
            }
            if (preset == ContentPreset.World) used.Add(VertexAttribute.TexCoord1); // ライトマップ用 UV は保持
            if (hasBlendShapes)
            {
                used.Add(VertexAttribute.Normal);
                used.Add(VertexAttribute.Tangent);
            }
            var result = Strippable.Where(a => mesh.HasVertexAttribute(a) && !used.Contains(a)).ToList();
            if (result.Count == 0) reason = "削除できる属性がありません";
            return result;
        }

        public override OperationAnalysis Analyze(OptimizationContext context)
        {
            var analysis = new OperationAnalysis();
            foreach (var info in context.Analysis.Renderers)
            {
                var label = info.Path.Length == 0 ? "(root)" : info.Path;
                if (info.Mesh == null || !(info.Renderer is MeshRenderer || info.Renderer is SkinnedMeshRenderer))
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unsupported, "対象外の Renderer/Mesh なし"));
                    continue;
                }
                if (info.ScriptReferenced || info.UdonOnSameObject)
                {
                    analysis.Items.Add(PlanItem.Skip(label, SupportState.Unknown, "スクリプトから Mesh を参照・操作する可能性"));
                    continue;
                }
                var removable = Removable(info.Mesh, info.Materials.Select(context.Canonical).ToArray(), info.Skinned, info.Mesh.blendShapeCount > 0, context.Settings.preset, out var reason);
                if (removable == null || removable.Count == 0) analysis.Items.Add(PlanItem.Skip(label, removable == null ? SupportState.Unknown : SupportState.Unsupported, reason));
                else analysis.Items.Add(PlanItem.Supported(label, "削除候補: " + string.Join(", ", removable)));
            }
            analysis.Notes.Add("実行時は生成後の Material 構成で再判定します。");
            return analysis;
        }

        public override void Execute(GenerationContext gen, OperationPlan plan)
        {
            var included = new HashSet<string>(plan.Items.Where(i => i.included).Select(i => i.target));
            var cache = new Dictionary<string, Mesh>();
            foreach (var pair in gen.Copy.AllRenderers)
            {
                var r = pair.Value;
                var label = gen.Copy.PathOf(r.transform);
                if (!included.Contains(string.IsNullOrEmpty(label) ? "(root)" : label)) continue;
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var removable = Removable(mesh, r.sharedMaterials, r is SkinnedMeshRenderer, mesh.blendShapeCount > 0, gen.Settings.preset, out _);
                if (removable == null || removable.Count == 0) continue;
                var key = mesh.GetInstanceID() + ":" + string.Join(",", removable);
                if (!cache.TryGetValue(key, out var stripped))
                {
                    stripped = AtlasMeshBuilder.Strip(mesh, removable);
                    stripped.name = mesh.name + "_stripped";
                    stripped = gen.AddAsset(stripped, "Meshes", stripped.name, Id, MaterialEquivalence.StableId(mesh));
                    cache[key] = stripped;
                }
                if (r is SkinnedMeshRenderer s)
                {
                    var weights = Enumerable.Range(0, mesh.blendShapeCount).Select(s.GetBlendShapeWeight).ToArray();
                    s.sharedMesh = stripped;
                    for (int i = 0; i < weights.Length; i++) s.SetBlendShapeWeight(i, weights[i]);
                }
                else r.GetComponent<MeshFilter>().sharedMesh = stripped;
            }
        }
    }
}
