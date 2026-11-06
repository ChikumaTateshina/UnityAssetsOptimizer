using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>選択範囲外のシーンオブジェクトへの参照。保存時に切断されるため処理を拒否する。</summary>
    public sealed class ExternalReference
    {
        public string FromObject;
        public string Component;
        public string Property;
        public string Target;

        public override string ToString() => FromObject + " (" + Component + "." + Property + ") → " + Target;
    }

    /// <summary>選択ルートの読み取り専用依存グラフ。</summary>
    public sealed class DependencyGraph
    {
        public readonly List<GameObject> Roots;
        /// <summary>ファイル実体を持つ依存アセット（シーンファイル/Prefab含む）。</summary>
        public readonly SortedSet<string> AssetPaths = new SortedSet<string>(System.StringComparer.Ordinal);
        /// <summary>組み込みリソース等、ファイル実体を持たない依存。</summary>
        public readonly SortedSet<string> BuiltinDependencies = new SortedSet<string>(System.StringComparer.Ordinal);
        public readonly List<Object> DependencyObjects = new List<Object>();
        public readonly List<ExternalReference> ExternalReferences = new List<ExternalReference>();
        public readonly List<string> UnsavedScenes = new List<string>();

        DependencyGraph(List<GameObject> roots) { Roots = roots; }

        public static DependencyGraph Build(IList<GameObject> roots)
        {
            var graph = new DependencyGraph(roots.ToList());
            var deps = EditorUtility.CollectDependencies(roots.Cast<Object>().ToArray());
            foreach (var obj in deps)
            {
                if (obj == null) continue;
                graph.DependencyObjects.Add(obj);
                if (!EditorUtility.IsPersistent(obj)) continue;
                var path = AssetDatabase.GetAssetPath(obj);
                if (AssetPathUtility.IsBuiltin(path)) graph.BuiltinDependencies.Add(string.IsNullOrEmpty(path) ? obj.name : path + ":" + obj.name);
                else graph.AssetPaths.Add(path);
            }

            foreach (var root in roots)
            {
                if (EditorUtility.IsPersistent(root)) continue;
                if (string.IsNullOrEmpty(root.scene.path)) graph.UnsavedScenes.Add(root.scene.name);
                else graph.AssetPaths.Add(root.scene.path);
            }

            graph.CollectExternalReferences();
            return graph;
        }

        void CollectExternalReferences()
        {
            var inside = new HashSet<Transform>();
            foreach (var root in Roots)
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) inside.Add(t);

            foreach (var root in Roots)
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform) continue;
                    var so = new SerializedObject(component);
                    var prop = so.GetIterator();
                    while (prop.Next(true))
                    {
                        if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var value = prop.objectReferenceValue;
                        if (value == null || EditorUtility.IsPersistent(value)) continue;

                        Transform target = null;
                        if (value is GameObject go) target = go.transform;
                        else if (value is Component c) target = c.transform;
                        if (target == null || inside.Contains(target)) continue;

                        ExternalReferences.Add(new ExternalReference
                        {
                            FromObject = AssetPathUtility.DisplayPath(component.gameObject),
                            Component = component.GetType().Name,
                            Property = prop.propertyPath,
                            Target = AssetPathUtility.DisplayPath(target.gameObject),
                        });
                    }
                }
            }
        }
    }
}
