using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// 生成 Prefab が原本と同じ階層・コンポーネント構成を保ち、参照が欠落していないことを検証する。
    /// 画像差分による外観検証は行わない（機能同等の証明にもならない）。
    /// </summary>
    public static class StructuralValidator
    {
        public static ValidationResult Compare(GameObject original, GameObject generated)
        {
            var result = new ValidationResult();
            if (generated == null)
            {
                result.Fail(original.name + ": 生成 Prefab がありません。");
                return result;
            }

            var a = Describe(original);
            var b = Describe(generated);
            if (a.Count != b.Count) result.Fail(original.name + ": Transform 数が一致しません（" + a.Count + " → " + b.Count + "）。");
            foreach (var pair in a)
            {
                if (!b.TryGetValue(pair.Key, out var types))
                {
                    result.Fail(original.name + ": 階層 '" + pair.Key + "' が生成物にありません。");
                    continue;
                }
                if (types != pair.Value) result.Fail(original.name + ": '" + pair.Key + "' のコンポーネント構成が異なります。");
            }

            var ra = original.GetComponentsInChildren<Renderer>(true);
            var rb = generated.GetComponentsInChildren<Renderer>(true);
            if (ra.Length != rb.Length)
            {
                result.Fail(original.name + ": Renderer 数が一致しません。");
                return result;
            }
            for (int i = 0; i < ra.Length; i++)
            {
                var path = AssetPathUtility.HierarchyPath(ra[i].transform, original.transform);
                if (MeshOf(ra[i]) != null && MeshOf(rb[i]) == null) result.Fail(path + ": Mesh 参照が失われました。");
                if (ra[i].sharedMaterials.Count(m => m != null) > 0 && rb[i].sharedMaterials.Any(m => m == null) && !ra[i].sharedMaterials.Any(m => m == null))
                    result.Fail(path + ": Material 参照が失われました。");
                if (Triangles(ra[i]) != Triangles(rb[i])) result.Fail(path + ": 三角形数が変化しました。");
            }
            return result;
        }

        static Dictionary<string, string> Describe(GameObject root)
        {
            var map = new Dictionary<string, string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                map[AssetPathUtility.HierarchyPath(t, root.transform)] =
                    string.Join(",", t.GetComponents<Component>().Select(c => c == null ? "(missing)" : c.GetType().FullName));
            return map;
        }

        static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        static long Triangles(Renderer r)
        {
            var mesh = MeshOf(r);
            if (mesh == null) return 0;
            long total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) total += mesh.GetIndexCount(s) / 3;
            return total;
        }
    }
}
