using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.AssetsOptimizer
{
    public enum CopyKind
    {
        /// <summary>Prefab 接続を持たない独立コピー。</summary>
        Unpacked,
        /// <summary>元 Prefab のインスタンス（保存すると元 Prefab の Variant になる）。</summary>
        PrefabInstance,
        /// <summary>NDMF ビルド時のように、渡されたオブジェクト自体を直接処理する。</summary>
        InPlace,
    }

    /// <summary>
    /// 選択ルートをプレビューシーン上に複製した隔離作業コピー。
    /// 元シーンには何も追加しない（保持用オブジェクトは HideAndDontSave で生成し即座にプレビューシーンへ移動する）。
    /// </summary>
    public sealed class WorkingCopy : IDisposable
    {
        Scene _scene;
        bool _ownsScene;
        readonly Dictionary<string, Renderer> _renderers = new Dictionary<string, Renderer>();

        public readonly List<GameObject> Roots = new List<GameObject>();
        public readonly List<CopyKind> Kinds = new List<CopyKind>();
        public readonly List<string> KindReasons = new List<string>();

        public static WorkingCopy Create(IList<GameObject> originals, PrefabOutputMode mode = PrefabOutputMode.Unpacked)
        {
            var copy = new WorkingCopy { _scene = EditorSceneManager.NewPreviewScene(), _ownsScene = true };
            var holder = EditorUtility.CreateGameObjectWithHideFlags("VAO_WorkingCopy", HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(holder, copy._scene);

            for (int r = 0; r < originals.Count; r++)
            {
                var original = originals[r];
                GameObject clone = null;
                string reason;
                if (mode == PrefabOutputMode.VariantWhenPossible && TryInstantiateAsPrefab(original, copy._scene, out clone, out reason))
                {
                    copy.Kinds.Add(CopyKind.PrefabInstance);
                }
                else
                {
                    if (mode == PrefabOutputMode.Unpacked) reason = "設定: 独立 Prefab として出力";
                    else reason = "Variant 不可: " + VariantBlocker(original);
                    clone = Object.Instantiate(original, holder.transform, false);
                    copy.Kinds.Add(CopyKind.Unpacked);
                }
                copy.KindReasons.Add(reason);
                clone.name = original.name;
                copy.Roots.Add(clone);
            }
            copy.Reindex();
            return copy;
        }

        /// <summary>渡されたオブジェクトを直接処理対象にする（複製しない）。NDMF のビルド用クローンにのみ使う。</summary>
        public static WorkingCopy Wrap(IList<GameObject> roots)
        {
            var copy = new WorkingCopy();
            foreach (var r in roots)
            {
                copy.Roots.Add(r);
                copy.Kinds.Add(CopyKind.InPlace);
                copy.KindReasons.Add("ビルド時処理");
            }
            copy.Reindex();
            return copy;
        }

        void Reindex()
        {
            _renderers.Clear();
            for (int r = 0; r < Roots.Count; r++)
                foreach (var renderer in Roots[r].GetComponentsInChildren<Renderer>(true))
                {
                    var siblings = renderer.GetComponents<Renderer>();
                    var key = RendererInfo.MakeKey(r, AssetPathUtility.HierarchyPath(renderer.transform, Roots[r].transform), Array.IndexOf(siblings, renderer));
                    _renderers[key] = renderer;
                }
        }

        /// <summary>
        /// Prefab Asset の選択、または構造的な変更（追加/削除）のない Prefab インスタンスのルートであれば、
        /// 元 Prefab をインスタンス化してプロパティ上書きを再現する。それ以外は false。
        /// </summary>
        static bool TryInstantiateAsPrefab(GameObject original, Scene scene, out GameObject instance, out string reason)
        {
            instance = null;
            reason = null;
            if (VariantBlocker(original) != null) return false;

            if (EditorUtility.IsPersistent(original))
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(original, scene);
                reason = "元 Prefab の Variant として出力";
                return instance != null;
            }

            var source = PrefabUtility.GetCorrespondingObjectFromSource(original);
            var modifications = PrefabUtility.GetPropertyModifications(original) ?? new PropertyModification[0];
            instance = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
            if (instance == null) return false;
            PrefabUtility.SetPropertyModifications(instance, modifications);
            reason = "元 Prefab の Variant として出力（インスタンスの上書き " + modifications.Length + " 件を再現）";
            return true;
        }

        /// <summary>Variant として出力できない理由。可能なら null。</summary>
        public static string VariantBlocker(GameObject original)
        {
            if (EditorUtility.IsPersistent(original))
            {
                if (!PrefabUtility.IsPartOfPrefabAsset(original) || original.transform.parent != null) return "Prefab Asset のルートではありません";
                return null;
            }
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(original)) return "Prefab インスタンスのルートではありません";
            if (PrefabUtility.IsPrefabAssetMissing(original)) return "元 Prefab が見つかりません";
            if (PrefabUtility.GetAddedGameObjects(original).Count > 0) return "インスタンスで GameObject が追加されています";
            if (PrefabUtility.GetAddedComponents(original).Count > 0) return "インスタンスでコンポーネントが追加されています";
            if (PrefabUtility.GetRemovedComponents(original).Count > 0) return "インスタンスでコンポーネントが削除されています";
            if (PrefabUtility.GetRemovedGameObjects(original).Count > 0) return "インスタンスで GameObject が削除されています";
            var mods = PrefabUtility.GetPropertyModifications(original) ?? new PropertyModification[0];
            if (mods.Any(m => m.objectReference != null && !EditorUtility.IsPersistent(m.objectReference)))
                return "インスタンスの上書きにシーンオブジェクト参照が含まれます";
            return null;
        }

        public Renderer FindRenderer(string key) => _renderers.TryGetValue(key, out var r) && r != null ? r : null;

        /// <summary>現存する Renderer（結合等で削除されたものは除く）。</summary>
        public IEnumerable<KeyValuePair<string, Renderer>> AllRenderers => _renderers.Where(p => p.Value != null).ToList();

        public int RootIndexOf(Transform t)
        {
            for (int i = 0; i < Roots.Count; i++)
                if (t == Roots[i].transform || t.IsChildOf(Roots[i].transform)) return i;
            return -1;
        }

        public string PathOf(Transform t)
        {
            int i = RootIndexOf(t);
            return i < 0 ? null : AssetPathUtility.HierarchyPath(t, Roots[i].transform);
        }

        public void Dispose()
        {
            if (_ownsScene && _scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            Roots.Clear();
            _renderers.Clear();
        }
    }
}
