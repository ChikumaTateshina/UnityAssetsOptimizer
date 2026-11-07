using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class SceneReplacement
    {
        public GameObject Original;
        public GameObject Prefab;
        /// <summary>置き換えできない理由（できる場合は null）。</summary>
        public string Problem;
        /// <summary>インスタンス側の上書き（ルートの位置・回転・拡大と名前を除く）。置き換えると失われる。</summary>
        public int LostOverrides;
        public string Label => Original != null ? Original.scene.name + ": " + AssetPathUtility.DisplayPath(Original) : "(不明)";
    }

    public sealed class SceneReplaceResult
    {
        public int Replaced;
        public int ReferencesRemapped;
        public readonly List<string> UnresolvedReferences = new List<string>();
    }

    /// <summary>
    /// シーン上の元オブジェクト（対象がシーンのオブジェクトの場合はそれ自体、FBX/Prefab の場合は開いているシーン内のそのインスタンス）を
    /// 生成 Prefab のインスタンスに置き換える。親・並び順・位置・回転・拡大・名前・アクティブ状態を引き継ぎ、
    /// ほかのオブジェクトからの参照は同じ位置のオブジェクトに付け替える。すべて Undo で元に戻せる（シーンは保存しない）。
    /// </summary>
    public static class SceneReplacer
    {
        static bool IsPrefab(OutputAsset o) => o.kind == "Prefab" || o.kind == "PrefabVariant";

        public static List<SceneReplacement> FindCandidates(IList<TargetRef> targets, IList<OutputAsset> outputs, List<string> notes)
        {
            var list = new List<SceneReplacement>();
            foreach (var target in targets)
            {
                var output = outputs.FirstOrDefault(o => IsPrefab(o) && o.sourceRef == target.globalObjectId);
                var prefab = output == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(output.guid));
                if (prefab == null)
                {
                    notes.Add(target.displayPath + ": 生成 Prefab が見つかりません。");
                    continue;
                }
                if (!GlobalObjectId.TryParse(target.globalObjectId, out var id) || !(GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is GameObject source))
                {
                    notes.Add(target.displayPath + ": 元のオブジェクトが見つかりません" + (string.IsNullOrEmpty(target.scenePath) ? string.Empty : "（シーン " + target.scenePath + " を開いてください）") + "。");
                    continue;
                }

                var originals = EditorUtility.IsPersistent(source) ? InstancesInLoadedScenes(source) : new List<GameObject> { source };
                if (originals.Count == 0) notes.Add(target.displayPath + ": 開いているシーンにインスタンスがありません。");
                foreach (var original in originals)
                    list.Add(new SceneReplacement { Original = original, Prefab = prefab, Problem = ProblemOf(original, prefab), LostOverrides = CountOverrides(original) });
            }
            return list;
        }

        static List<GameObject> InstancesInLoadedScenes(GameObject asset)
        {
            var list = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject) && PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) == asset)
                            list.Add(t.gameObject);
            }
            return list;
        }

        static string ProblemOf(GameObject original, GameObject prefab)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(original) && PrefabUtility.GetCorrespondingObjectFromSource(original) == prefab) return "既に生成 Prefab のインスタンスです";
            var parent = original.transform.parent;
            if (parent != null && PrefabUtility.IsPartOfPrefabInstance(parent) && !PrefabUtility.IsAddedGameObjectOverride(original))
                return "別の Prefab インスタンスの中にあるため置き換えできません（親の Prefab 側で置き換えるか、親を Unpack してください）";
            return null;
        }

        static int CountOverrides(GameObject original)
        {
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(original)) return 0;
            var rootTransform = PrefabUtility.GetCorrespondingObjectFromSource(original.transform);
            var rootObject = PrefabUtility.GetCorrespondingObjectFromSource(original);
            int mods = (PrefabUtility.GetPropertyModifications(original) ?? new PropertyModification[0])
                .Count(m => !((m.target == rootTransform && (m.propertyPath.StartsWith("m_LocalPosition", StringComparison.Ordinal) ||
                                                            m.propertyPath.StartsWith("m_LocalRotation", StringComparison.Ordinal) ||
                                                            m.propertyPath.StartsWith("m_LocalScale", StringComparison.Ordinal) ||
                                                            m.propertyPath.StartsWith("m_LocalEulerAnglesHint", StringComparison.Ordinal) ||
                                                            m.propertyPath == "m_RootOrder")) ||
                              (m.target == rootObject && (m.propertyPath == "m_Name" || m.propertyPath == "m_IsActive"))));
            return mods + PrefabUtility.GetAddedComponents(original).Count + PrefabUtility.GetAddedGameObjects(original).Count +
                   PrefabUtility.GetRemovedComponents(original).Count;
        }

        public static SceneReplaceResult Replace(IEnumerable<SceneReplacement> replacements)
        {
            var result = new SceneReplaceResult();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("UnityAssetsOptimizer: シーンの置き換え");
            int group = Undo.GetCurrentGroup();
            foreach (var r in replacements.Where(r => r.Problem == null && r.Original != null))
            {
                var old = r.Original;
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(r.Prefab, old.scene);
                Undo.RegisterCreatedObjectUndo(instance, "UnityAssetsOptimizer: シーンの置き換え");
                instance.transform.SetParent(old.transform.parent, false);
                instance.transform.SetSiblingIndex(old.transform.GetSiblingIndex());
                instance.transform.localPosition = old.transform.localPosition;
                instance.transform.localRotation = old.transform.localRotation;
                instance.transform.localScale = old.transform.localScale;
                instance.name = old.name;
                instance.SetActive(old.activeSelf);

                RemapReferences(old, instance, result);
                Undo.DestroyObjectImmediate(old);
                EditorSceneManager.MarkSceneDirty(instance.scene);
                result.Replaced++;
            }
            Undo.CollapseUndoOperations(group);
            return result;
        }

        /// <summary>置き換え前のオブジェクトを指す参照（置き換え対象の外から）を、新しいインスタンスの同じ位置のオブジェクトに付け替える。</summary>
        static void RemapReferences(GameObject oldRoot, GameObject newRoot, SceneReplaceResult result)
        {
            var oldSet = new HashSet<Transform>(oldRoot.GetComponentsInChildren<Transform>(true));
            var newByPath = new Dictionary<string, Transform>();
            var newByName = new Dictionary<string, List<Transform>>();
            foreach (var t in newRoot.GetComponentsInChildren<Transform>(true))
            {
                newByPath[AssetPathUtility.HierarchyPath(t, newRoot.transform)] = t;
                var np = AssetPathUtility.NamePath(t, newRoot.transform);
                if (!newByName.TryGetValue(np, out var l)) newByName[np] = l = new List<Transform>();
                l.Add(t);
            }
            Transform Corresponding(Transform t)
            {
                if (newByPath.TryGetValue(AssetPathUtility.HierarchyPath(t, oldRoot.transform), out var n)) return n;
                // 空になったオブジェクトの削除で並び順が変わった場合は、名前のパスが一意なら対応させる
                return newByName.TryGetValue(AssetPathUtility.NamePath(t, oldRoot.transform), out var l) && l.Count == 1 ? l[0] : null;
            }
            Object Map(Object value)
            {
                if (value is GameObject go) return Corresponding(go.transform)?.gameObject;
                if (value is Component c)
                {
                    var target = Corresponding(c.transform);
                    if (target == null) return null;
                    var index = Array.IndexOf(c.GetComponents(c.GetType()), c);
                    var candidates = target.GetComponents(c.GetType());
                    return index >= 0 && index < candidates.Length ? candidates[index] : null;
                }
                return null;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null || component is Transform || oldSet.Contains(component.transform)) continue;
                        if (component.transform == newRoot.transform || component.transform.IsChildOf(newRoot.transform)) continue;
                        var so = new SerializedObject(component);
                        var p = so.GetIterator();
                        bool changed = false;
                        while (p.Next(true))
                        {
                            if (p.propertyType != SerializedPropertyType.ObjectReference || p.propertyPath == "m_GameObject") continue;
                            var value = p.objectReferenceValue;
                            var t = value is GameObject g ? g.transform : value is Component vc ? vc.transform : null;
                            if (t == null || !oldSet.Contains(t)) continue;
                            var mapped = Map(value);
                            if (mapped == null)
                            {
                                result.UnresolvedReferences.Add(AssetPathUtility.DisplayPath(component.gameObject) + " (" + component.GetType().Name + "." + p.propertyPath + ") → " + value.name);
                                continue;
                            }
                            p.objectReferenceValue = mapped;
                            changed = true;
                            result.ReferencesRemapped++;
                        }
                        if (changed) so.ApplyModifiedProperties(); // Undo に記録される
                    }
            }
        }
    }
}
