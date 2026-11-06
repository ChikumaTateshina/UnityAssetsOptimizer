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
        public static ValidationResult Compare(GameObject original, GameObject generated, IEnumerable<StructuralChange> changes = null)
        {
            var result = new ValidationResult();
            if (generated == null)
            {
                result.Fail(original.name + ": 生成 Prefab がありません。");
                return result;
            }
            var changeList = (changes ?? Enumerable.Empty<StructuralChange>()).ToList();
            var addedObjects = changeList.Where(c => c.Added && c.ComponentType == "GameObject").Select(c => c.Path).ToList();
            bool IsAddedPath(string p) => addedObjects.Any(a => p == a || p.StartsWith(a + "/", StringComparison.Ordinal));

            var a = Describe(original);
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
                foreach (var removed in changeList.Where(c => !c.Added && c.Path == pair.Key)) expected.Remove(removed.ComponentType);
                expected.AddRange(changeList.Where(c => c.Added && c.Path == pair.Key && c.ComponentType != "GameObject").Select(c => c.ComponentType));
                if (!expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(types.OrderBy(x => x, StringComparer.Ordinal)))
                    result.Fail(original.name + ": '" + pair.Key + "' のコンポーネント構成が異なります（登録外の変更）。");
            }

            var renderersBefore = original.GetComponentsInChildren<Renderer>(true)
                .ToDictionary(r => Key(r, original.transform), r => r);
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
