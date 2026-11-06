using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    public sealed class ResolvedSelection
    {
        public readonly List<GameObject> Roots = new List<GameObject>();
        /// <summary>祖先が同時に選択されていたため除外した子選択。</summary>
        public readonly List<GameObject> RemovedDescendants = new List<GameObject>();
        public readonly List<TargetRef> Targets = new List<TargetRef>();
    }

    public static class SelectionResolver
    {
        /// <summary>
        /// 選択 GameObject 群から、祖先と子孫の重複を除いたルート一覧を決定的な順序で返す。
        /// </summary>
        public static ResolvedSelection Resolve(IEnumerable<GameObject> selection)
        {
            var result = new ResolvedSelection();
            var unique = selection.Where(g => g != null).Distinct().ToList();
            var set = new HashSet<GameObject>(unique);

            foreach (var go in unique)
            {
                if (HasSelectedAncestor(go, set)) result.RemovedDescendants.Add(go);
                else result.Roots.Add(go);
            }

            result.Roots.Sort((a, b) => string.CompareOrdinal(SortKey(a), SortKey(b)));
            foreach (var root in result.Roots) result.Targets.Add(CreateTargetRef(root));
            return result;
        }

        static bool HasSelectedAncestor(GameObject go, HashSet<GameObject> selected)
        {
            var t = go.transform.parent;
            while (t != null)
            {
                if (selected.Contains(t.gameObject)) return true;
                t = t.parent;
            }
            return false;
        }

        static string SortKey(GameObject go)
        {
            if (EditorUtility.IsPersistent(go)) return "A|" + AssetDatabase.GetAssetPath(go);
            var indices = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                indices.Insert(0, t.GetSiblingIndex().ToString("D6"));
                t = t.parent;
            }
            return "S|" + go.scene.path + "|" + string.Join("/", indices);
        }

        public static TargetRef CreateTargetRef(GameObject go)
        {
            var target = new TargetRef
            {
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                displayPath = AssetPathUtility.DisplayPath(go),
                scenePath = EditorUtility.IsPersistent(go) ? string.Empty : go.scene.path,
                prefabStatus = DescribePrefabStatus(go),
            };

            var source = EditorUtility.IsPersistent(go) ? go : PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (source != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId))
            {
                target.assetGuid = guid;
                target.localFileId = localId;
            }
            return target;
        }

        static string DescribePrefabStatus(GameObject go)
        {
            try
            {
                var assetType = PrefabUtility.GetPrefabAssetType(go);
                var instanceStatus = PrefabUtility.GetPrefabInstanceStatus(go);
                var asset = EditorUtility.IsPersistent(go) ? "asset" : "scene";
                return asset + "/" + assetType + "/" + instanceStatus;
            }
            catch (Exception e)
            {
                return "unknown: " + e.Message;
            }
        }
    }
}
