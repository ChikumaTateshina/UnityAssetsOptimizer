using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// 結合等で Renderer/MeshFilter を外され Transform だけになった GameObject（と、子がすべて消えて空になったグループ）を
    /// 非表示 + EditorOnly にする（ビルドに含まれない）か、削除する。すべての処理・検証の後、Prefab の保存直前に行う。
    /// 削除は独立 Prefab（Unpacked）とビルド時（InPlace）のみ（Variant では元 Prefab の子を削除できないため非表示 + EditorOnly にする）。
    /// 他のコンポーネントからの参照・アニメーションのバインディング対象・元から空のものは対象外。
    /// </summary>
    public static class EmptyObjectCleaner
    {
        public sealed class Result
        {
            /// <summary>削除した GameObject の元のパス（ルートごと）。構造検証に渡す。</summary>
            public readonly List<KeyValuePair<int, string>> Removed = new List<KeyValuePair<int, string>>();
            public readonly List<string> Notes = new List<string>();
            /// <summary>非表示 + EditorOnly にした GameObject の数。</summary>
            public int Hidden;
        }

        public const string EditorOnlyTag = "EditorOnly";

        public static bool CanRemove(CopyKind kind) => kind == CopyKind.Unpacked || kind == CopyKind.InPlace;

        public static Result Run(GenerationContext gen, ICollection<string> animatedBindingPaths)
        {
            var result = new Result();
            var handling = gen.Settings.emptyObjectHandling;
            if (handling == EmptyObjectHandling.Keep) return result;
            for (int r = 0; r < gen.Copy.Roots.Count; r++)
            {
                var root = gen.Copy.Roots[r].transform;
                // 処理で Renderer/MeshFilter を外したオブジェクトだけを起点にする（元から空のオブジェクトは残す）
                var touched = new HashSet<string>(gen.StructuralChanges.Where(c => c.RootIndex == r && !c.Added && c.ComponentType != "GameObject").Select(c => c.Path));
                if (touched.Count == 0) continue;
                bool delete = gen.Copy.Kinds[r] == CopyKind.InPlace || (handling == EmptyObjectHandling.Delete && CanRemove(gen.Copy.Kinds[r]));
                if (handling == EmptyObjectHandling.Delete && !delete)
                    result.Notes.Add(root.name + ": Prefab Variant として出力するため、空になった GameObject は削除せず非表示 + EditorOnly にします（出力形式を Unpacked にすると削除できます）。");

                var referenced = ReferencedTransforms(root);
                var paths = root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => AssetPathUtility.HierarchyPath(t, root));
                bool Removable(Transform t, HashSet<Transform> removing) =>
                    t != root &&
                    t.GetComponents<Component>().All(c => c is Transform) &&
                    t.Cast<Transform>().All(removing.Contains) &&
                    !referenced.Contains(t) &&
                    !IsAnimated(AssetPathUtility.NamePath(t, root), animatedBindingPaths);

                var removing = new HashSet<Transform>();
                // 葉から順に判定する（子がすべて削除される空のグループも削除）
                foreach (var t in root.GetComponentsInChildren<Transform>(true).OrderByDescending(Depth))
                {
                    bool emptied = touched.Contains(paths[t]);
                    bool emptyGroup = t.childCount > 0 && t.Cast<Transform>().All(removing.Contains);
                    if ((emptied || emptyGroup) && Removable(t, removing)) removing.Add(t);
                }
                // 最上位の対象だけを処理する（子も一緒に非表示・除外・削除される）
                foreach (var t in removing.Where(t => !removing.Contains(t.parent)).OrderByDescending(Depth).ToList())
                {
                    if (!delete)
                    {
                        t.gameObject.SetActive(false);
                        t.gameObject.tag = EditorOnlyTag;
                        result.Hidden++;
                        continue;
                    }
                    foreach (var d in t.GetComponentsInChildren<Transform>(true)) result.Removed.Add(new KeyValuePair<int, string>(r, paths[d]));
                    gen.RegisterRemoved(r, paths[t], "GameObject", FeatureIds.MeshCombine);
                    Object.DestroyImmediate(t.gameObject);
                }
            }
            if (result.Removed.Count > 0) gen.Log.Info(FeatureIds.MeshCombine, "空になった GameObject を " + result.Removed.Count + " 個削除しました。");
            if (result.Hidden > 0) gen.Log.Info(FeatureIds.MeshCombine, "空になった GameObject " + result.Hidden + " 個（子を含むまとまり単位）を非表示にし、EditorOnly タグを付けました（ビルドには含まれません）。");
            return result;
        }

        static int Depth(Transform t)
        {
            int d = 0;
            for (var c = t; c.parent != null; c = c.parent) d++;
            return d;
        }

        /// <summary>
        /// アニメーションのバインディングパス（名前のパス）と末尾で一致すれば対象とみなす（中間の Animator からの相対パスも考慮した保守的な判定）。
        /// </summary>
        static bool IsAnimated(string namePath, ICollection<string> bindings) =>
            bindings != null && bindings.Any(b => b.Length > 0 &&
                (b == namePath || b.EndsWith("/" + namePath, StringComparison.Ordinal) || namePath.EndsWith("/" + b, StringComparison.Ordinal)));

        /// <summary>複製の中のコンポーネント（Transform 以外）から参照されている GameObject/Component の Transform。</summary>
        static HashSet<Transform> ReferencedTransforms(Transform root)
        {
            var set = new HashSet<Transform>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                var so = new SerializedObject(c);
                var p = so.GetIterator();
                while (p.Next(true))
                {
                    if (p.propertyType != SerializedPropertyType.ObjectReference || p.propertyPath == "m_GameObject") continue;
                    var value = p.objectReferenceValue;
                    var t = value is GameObject g ? g.transform : value is Component comp ? comp.transform : null;
                    if (t != null) set.Add(t);
                }
            }
            return set;
        }
    }
}
