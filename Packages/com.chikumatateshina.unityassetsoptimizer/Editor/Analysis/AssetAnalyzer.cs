using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class AnalysisIssue
    {
        public bool Blocking;
        public string Source;
        public string Message;

        public override string ToString() => (Blocking ? "[実行不可] " : "[注意] ") + Source + ": " + Message;
    }

    public sealed class RendererInfo
    {
        public Renderer Renderer;
        public int RootIndex;
        public string Path;
        public int ComponentIndex;
        public Mesh Mesh;
        public Material[] Materials;
        public bool Skinned;
        public bool AnimatedMaterial;
        public bool ScriptReferenced;
        public bool UdonOnSameObject;
        /// <summary>安全リストを含む任意のコンポーネントから参照されている（結合・削除の判定用）。</summary>
        public bool ReferencedByAnyComponent;
        /// <summary>自身または祖先がアニメーションの対象（プロパティ種別を問わない）。</summary>
        public bool AnimatedAny;

        /// <summary>クローン上の Renderer を特定するためのキー。</summary>
        public string Key => MakeKey(RootIndex, Path, ComponentIndex);

        public static string MakeKey(int rootIndex, string path, int componentIndex) =>
            rootIndex + ":" + path + "#" + componentIndex;
    }

    public sealed class AnalysisReport
    {
        public DependencyGraph Graph;
        public readonly List<RendererInfo> Renderers = new List<RendererInfo>();
        public readonly HashSet<Material> ScriptReferencedMaterials = new HashSet<Material>();
        /// <summary>依存 AnimationClip の全バインディングパス。</summary>
        public readonly HashSet<string> AnimatedBindingPaths = new HashSet<string>();
        /// <summary>任意の MonoBehaviour から参照される GameObject/Component の Transform。</summary>
        public readonly HashSet<Transform> ReferencedTransforms = new HashSet<Transform>();
        /// <summary>
        /// 実際に使われている AnimationClip（Animator / Animation / 各コンポーネントが参照するコントローラー・クリップ）。
        /// FBX に内蔵されているだけで使われていないクリップは含めない。
        /// </summary>
        public readonly HashSet<AnimationClip> UsedAnimationClips = new HashSet<AnimationClip>();

        /// <summary>
        /// t またはその祖先（root まで）がアニメーションのバインディング対象になり得るか。
        /// パスは末尾一致で保守的に判定する（中間の Animator からの相対パスにも対応）。
        /// </summary>
        public bool IsAnimated(Transform t, Transform root)
        {
            if (AnimatedBindingPaths.Count == 0) return false;
            for (var cur = t; cur != null; cur = cur.parent)
            {
                var namePath = AssetPathUtility.NamePath(cur, root);
                foreach (var bp in AnimatedBindingPaths)
                {
                    if (bp.Length == 0)
                    {
                        if (cur == root || cur.GetComponent<Animator>() != null || cur.GetComponent<Animation>() != null) return true;
                    }
                    else if (namePath == bp || namePath.EndsWith("/" + bp, StringComparison.Ordinal)) return true;
                }
                if (cur == root) break;
            }
            return false;
        }
        public readonly List<AnalysisIssue> Issues = new List<AnalysisIssue>();
        public bool HasAvatarDescriptor;
        public bool HasUdon;
        public bool HasBuildTimeTools;
        public int MissingScriptCount;

        public bool HasBlockingIssues => Issues.Any(i => i.Blocking);
    }

    /// <summary>選択階層を読み取り専用で走査する。未知の依存は Unknown として扱う。</summary>
    public static class AssetAnalyzer
    {
        public const string AvatarDescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        public const string UdonBehaviourType = "VRC.Udon.UdonBehaviour";

        /// <summary>
        /// Renderer 参照を持っていてもマテリアル・スロットを操作しないことが分かっているコンポーネント。
        /// VRCAvatarDescriptor は BlendShape を名前/インデックスで参照するが、アトラス化は BlendShape 順序を保持する。
        /// </summary>
        static readonly HashSet<string> RendererReferenceSafeTypes = new HashSet<string>
        {
            AvatarDescriptorType,
        };

        static readonly string[] BuildTimeToolNamespaces =
        {
            "nadena.dev.modular_avatar",
            "VF.",
            "nadena.dev.ndmf",
        };

        public static AnalysisReport Analyze(DependencyGraph graph)
        {
            AnalysisReport result = null;
            BackgroundJob.RunToCompletion(AnalyzeRoutine(graph, r => result = r));
            return result;
        }

        public static IEnumerator AnalyzeRoutine(DependencyGraph graph, Action<AnalysisReport> done)
        {
            var report = new AnalysisReport { Graph = graph };

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                report.Issues.Add(new AnalysisIssue { Blocking = true, Source = "Editor", Message = "Play Mode 中は実行できません。" });
            if (graph.Roots.Count == 0)
                report.Issues.Add(new AnalysisIssue { Blocking = true, Source = "Selection", Message = "処理対象の GameObject が選択されていません。" });

            foreach (var ext in graph.ExternalReferences)
                report.Issues.Add(new AnalysisIssue { Blocking = true, Source = "外部参照", Message = ext + "（選択外への参照は生成Prefabで切断されるため処理しません）" });
            foreach (var scene in graph.UnsavedScenes)
                report.Issues.Add(new AnalysisIssue { Source = "Scene", Message = "未保存シーン '" + scene + "' のオブジェクトです。シーンファイルのスナップショットは取得できません（シーン自体は変更しません）。" });

            CollectRenderers(report);
            yield return null;
            yield return ScanComponents(report);
            CollectUsedClips(report);
            yield return null;
            ScanAnimations(report);
            done(report);
        }

        static void AddController(AnalysisReport report, RuntimeAnimatorController controller)
        {
            if (controller == null) return;
            foreach (var clip in controller.animationClips)
                if (clip != null) report.UsedAnimationClips.Add(clip);
        }

        static void CollectUsedClips(AnalysisReport report)
        {
            foreach (var root in report.Graph.Roots)
            {
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                    AddController(report, animator.runtimeAnimatorController);
                foreach (var animation in root.GetComponentsInChildren<Animation>(true))
                    foreach (var clip in AnimationUtility.GetAnimationClips(animation.gameObject))
                        if (clip != null) report.UsedAnimationClips.Add(clip);
            }
        }

        static void CollectRenderers(AnalysisReport report)
        {
            var roots = report.Graph.Roots;
            for (int r = 0; r < roots.Count; r++)
            {
                var root = roots[r];
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var siblings = renderer.GetComponents<Renderer>();
                    var info = new RendererInfo
                    {
                        Renderer = renderer,
                        RootIndex = r,
                        Path = AssetPathUtility.HierarchyPath(renderer.transform, root.transform),
                        ComponentIndex = Array.IndexOf(siblings, renderer),
                        Materials = renderer.sharedMaterials,
                    };
                    if (renderer is SkinnedMeshRenderer smr)
                    {
                        info.Skinned = true;
                        info.Mesh = smr.sharedMesh;
                    }
                    else if (renderer is MeshRenderer)
                    {
                        var mf = renderer.GetComponent<MeshFilter>();
                        info.Mesh = mf != null ? mf.sharedMesh : null;
                    }
                    report.Renderers.Add(info);
                }
            }
        }

        static IEnumerator ScanComponents(AnalysisReport report)
        {
            int count = 0;
            var rendererLookup = report.Renderers.ToDictionary(i => i.Renderer);
            foreach (var root in report.Graph.Roots)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                    if (missing > 0)
                    {
                        report.MissingScriptCount += missing;
                        report.Issues.Add(new AnalysisIssue
                        {
                            Blocking = true,
                            Source = "Component",
                            Message = AssetPathUtility.DisplayPath(t.gameObject) + " に Missing Script があります（内容を解析できないため処理しません）。",
                        });
                    }
                }

                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    if (++count % 16 == 0) yield return null;
                    var typeName = mb.GetType().FullName ?? string.Empty;
                    if (typeName == AvatarDescriptorType) report.HasAvatarDescriptor = true;
                    if (BuildTimeToolNamespaces.Any(ns => typeName.StartsWith(ns, StringComparison.Ordinal))) report.HasBuildTimeTools = true;

                    if (typeName == UdonBehaviourType)
                    {
                        report.HasUdon = true;
                        foreach (var r in mb.GetComponents<Renderer>())
                            if (rendererLookup.TryGetValue(r, out var info)) info.UdonOnSameObject = true;
                    }

                    RecordAllReferences(mb, report, rendererLookup);
                    if (RendererReferenceSafeTypes.Contains(typeName)) continue;
                    ScanScriptReferences(mb, report, rendererLookup);
                }
            }

            if (report.HasUdon)
                report.Issues.Add(new AnalysisIssue { Source = "Udon", Message = "UdonBehaviour が含まれます。実行時のマテリアル操作は静的解析できないため、同一オブジェクトの Renderer とスクリプト参照された Renderer/Material は通常モードで処理しません。" });
            if (report.HasBuildTimeTools)
                report.Issues.Add(new AnalysisIssue { Source = "Build-time tools", Message = "Modular Avatar / VRCFury 等のビルド時変換ツールが含まれます。ビルド後の構成は評価できないため、指標は「評価不完全」として扱います。" });
        }

        static void RecordAllReferences(MonoBehaviour mb, AnalysisReport report, Dictionary<Renderer, RendererInfo> lookup)
        {
            var so = new SerializedObject(mb);
            var prop = so.GetIterator();
            while (prop.Next(true))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                var value = prop.objectReferenceValue;
                // コンポーネント（VRCAvatarDescriptor の Playable Layer、Modular Avatar 等）が使うアニメーション
                if (value is RuntimeAnimatorController controller) AddController(report, controller);
                else if (value is AnimationClip clip) report.UsedAnimationClips.Add(clip);
                Transform t = value is GameObject g ? g.transform : value is Component c ? c.transform : null;
                if (t == null || EditorUtility.IsPersistent(t) || prop.propertyPath == "m_GameObject") continue;
                report.ReferencedTransforms.Add(t);
                if (value is Renderer r && lookup.TryGetValue(r, out var info)) info.ReferencedByAnyComponent = true;
                if (value is GameObject go)
                    foreach (var gr in go.GetComponents<Renderer>())
                        if (lookup.TryGetValue(gr, out var gi)) gi.ReferencedByAnyComponent = true;
            }
        }

        static void ScanScriptReferences(MonoBehaviour mb, AnalysisReport report, Dictionary<Renderer, RendererInfo> lookup)
        {
            var so = new SerializedObject(mb);
            var prop = so.GetIterator();
            while (prop.Next(true))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                var value = prop.objectReferenceValue;
                if (value is Material mat) report.ScriptReferencedMaterials.Add(mat);
                else if (value is Renderer r && lookup.TryGetValue(r, out var info)) info.ScriptReferenced = true;
                else if (value is MeshFilter mf)
                    foreach (var mr in mf.GetComponents<Renderer>())
                        if (lookup.TryGetValue(mr, out var mi)) mi.ScriptReferenced = true;
            }
        }

        /// <summary>
        /// 使われている AnimationClip から、マテリアルプロパティ/マテリアル差し替えを行うバインディングを検出する。
        /// パスの解決は保守的に行い、末尾一致した Renderer をすべて対象とする。
        /// </summary>
        static void ScanAnimations(AnalysisReport report)
        {
            var bindingPaths = new HashSet<string>();
            foreach (var clip in report.UsedAnimationClips)
            {
                foreach (var b in AnimationUtility.GetCurveBindings(clip)) report.AnimatedBindingPaths.Add(b.path);
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip)) report.AnimatedBindingPaths.Add(b.path);
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    if (typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName.StartsWith("material.", StringComparison.Ordinal))
                        bindingPaths.Add(b.path);
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    if (typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                        bindingPaths.Add(b.path);
            }
            foreach (var info in report.Renderers)
                info.AnimatedAny = report.IsAnimated(info.Renderer.transform, report.Graph.Roots[info.RootIndex].transform);
            if (bindingPaths.Count == 0) return;

            foreach (var info in report.Renderers)
            {
                var root = report.Graph.Roots[info.RootIndex].transform;
                var namePath = AssetPathUtility.NamePath(info.Renderer.transform, root);
                foreach (var bp in bindingPaths)
                {
                    bool match;
                    if (bp.Length == 0)
                        match = info.Renderer.transform == root || info.Renderer.GetComponent<Animator>() != null || info.Renderer.GetComponent<Animation>() != null;
                    else
                        match = namePath == bp || namePath.EndsWith("/" + bp, StringComparison.Ordinal);
                    if (match)
                    {
                        info.AnimatedMaterial = true;
                        break;
                    }
                }
            }
            report.Issues.Add(new AnalysisIssue { Source = "Animation", Message = "マテリアルプロパティ/マテリアル差し替えのアニメーションを検出しました。該当 Renderer は通常モードでアトラス化しません。" });
        }
    }
}
