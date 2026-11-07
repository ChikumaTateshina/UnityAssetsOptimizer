using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// 生成 Prefab が原本と同じ階層・コンポーネント構成を保ち、参照が欠落していないことを検証する。
    /// Operation が登録した意図的な構造変更（結合による Renderer 削除、LOD の追加等）のみ差分として許容する。
    /// 画像差分による外観検証は別途参考値として扱う（機能同等の証明にはならない）。
    /// </summary>
    public static class StructuralValidator
    {
        /// <param name="removedObjects">削除した GameObject の元のパス（空になったオブジェクトの削除）。それ以外の階層は残った兄弟の中での順番で照合する。</param>
        public static ValidationResult Compare(GameObject original, GameObject generated, IEnumerable<StructuralChange> changes = null, ICollection<string> removedObjects = null)
        {
            var result = new ValidationResult();
            if (generated == null)
            {
                result.Fail(original.name + ": 生成 Prefab がありません。");
                return result;
            }
            var removed = new HashSet<string>(removedObjects ?? new string[0]);
            // 元のパス → 削除後のパス（削除したものは含まない）
            var remap = Remap(original.transform, removed);
            var changeList = (changes ?? Enumerable.Empty<StructuralChange>())
                .Where(c => !(c.Added == false && c.ComponentType == "GameObject"))
                .Select(c => RemapPath(c.Path, remap) is string np && np != c.Path ? new StructuralChange { RootIndex = c.RootIndex, Path = np, ComponentType = c.ComponentType, Added = c.Added, OperationId = c.OperationId } : c)
                .Where(c => c.Added || !removed.Contains(c.Path))
                .ToList();
            var addedObjects = changeList.Where(c => c.Added && c.ComponentType == "GameObject").Select(c => c.Path).ToList();
            bool IsAddedPath(string p) => addedObjects.Any(a => p == a || p.StartsWith(a + "/", StringComparison.Ordinal));

            var a = Describe(original).Where(p => remap.ContainsKey(p.Key)).ToDictionary(p => remap[p.Key], p => p.Value);
            var b = Describe(generated).Where(p => !IsAddedPath(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            if (a.Count != b.Count) result.Fail(original.name + ": Transform 数が一致しません（" + a.Count + " → " + b.Count + "、追加登録分を除く）。");
            foreach (var pair in a)
            {
                if (!b.TryGetValue(pair.Key, out var types))
                {
                    result.Fail(original.name + ": 階層 '" + pair.Key + "' が生成物にありません。");
                    continue;
                }
                var expected = pair.Value.ToList();
                foreach (var change in changeList.Where(c => !c.Added && c.Path == pair.Key)) expected.Remove(change.ComponentType);
                expected.AddRange(changeList.Where(c => c.Added && c.Path == pair.Key && c.ComponentType != "GameObject").Select(c => c.ComponentType));
                if (!expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(types.OrderBy(x => x, StringComparer.Ordinal)))
                    result.Fail(original.name + ": '" + pair.Key + "' のコンポーネント構成が異なります（登録外の変更）。");
            }

            var renderersBefore = original.GetComponentsInChildren<Renderer>(true)
                .Where(r => remap.ContainsKey(AssetPathUtility.HierarchyPath(r.transform, original.transform)))
                .ToDictionary(r => remap[AssetPathUtility.HierarchyPath(r.transform, original.transform)] + "#" + Array.IndexOf(r.GetComponents<Renderer>(), r), r => r);
            foreach (var r in generated.GetComponentsInChildren<Renderer>(true))
            {
                var key = Key(r, generated.transform);
                if (IsAddedPath(AssetPathUtility.HierarchyPath(r.transform, generated.transform))) continue;
                if (MeshOf(r) == null && renderersBefore.TryGetValue(key, out var before) && MeshOf(before) != null)
                    result.Fail(key + ": Mesh 参照が失われました。");
                if (r.sharedMaterials.Any(m => m == null) && renderersBefore.TryGetValue(key, out var b2) && !b2.sharedMaterials.Any(m => m == null))
                    result.Fail(key + ": Material 参照が失われました。");
            }
            return result;
        }

        /// <summary>元のパスを削除後のパスに直す。追加されたオブジェクト（元にないパス）は、元にある最も深い祖先の部分だけを直す。</summary>
        static string RemapPath(string path, Dictionary<string, string> remap)
        {
            if (remap.TryGetValue(path, out var direct)) return direct;
            for (int i = path.LastIndexOf('/'); i > 0; i = path.LastIndexOf('/', i - 1))
                if (remap.TryGetValue(path.Substring(0, i), out var prefix)) return prefix + path.Substring(i);
            return path;
        }

        /// <summary>削除されたオブジェクトを除いたときの各 Transform のパス（兄弟の番号を詰め直す）。</summary>
        static Dictionary<string, string> Remap(Transform root, HashSet<string> removed)
        {
            var map = new Dictionary<string, string> { [string.Empty] = string.Empty };
            void Walk(Transform t, string originalPath, string newPath)
            {
                int index = 0;
                for (int i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    var op = AssetPathUtility.HierarchyPath(child, root);
                    if (removed.Contains(op)) continue;
                    var segment = child.name + "[" + index++ + "]";
                    var np = newPath.Length == 0 ? segment : newPath + "/" + segment;
                    map[op] = np;
                    Walk(child, op, np);
                }
            }
            Walk(root, string.Empty, string.Empty);
            return map;
        }

        static string Key(Renderer r, Transform root) =>
            AssetPathUtility.HierarchyPath(r.transform, root) + "#" + Array.IndexOf(r.GetComponents<Renderer>(), r);

        static Dictionary<string, List<string>> Describe(GameObject root)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                map[AssetPathUtility.HierarchyPath(t, root.transform)] =
                    t.GetComponents<Component>().Select(c => c == null ? "(missing)" : c.GetType().FullName).ToList();
            return map;
        }

        static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }
    }
}
