using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// 選択ルートをプレビューシーン上に複製した隔離作業コピー。
    /// 元シーンには何も追加しない（保持用オブジェクトは HideAndDontSave で生成し即座にプレビューシーンへ移動する）。
    /// </summary>
    public sealed class WorkingCopy : IDisposable
    {
        Scene _scene;
        readonly Dictionary<string, Renderer> _renderers = new Dictionary<string, Renderer>();

        public readonly List<GameObject> Roots = new List<GameObject>();

        public static WorkingCopy Create(IList<GameObject> originals)
        {
            var copy = new WorkingCopy();
            copy._scene = EditorSceneManager.NewPreviewScene();
            var holder = EditorUtility.CreateGameObjectWithHideFlags("VAO_WorkingCopy", HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(holder, copy._scene);

            for (int r = 0; r < originals.Count; r++)
            {
                var clone = Object.Instantiate(originals[r], holder.transform, false);
                clone.name = originals[r].name;
                copy.Roots.Add(clone);

                foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
                {
                    var siblings = renderer.GetComponents<Renderer>();
                    var key = RendererInfo.MakeKey(r, AssetPathUtility.HierarchyPath(renderer.transform, clone.transform), Array.IndexOf(siblings, renderer));
                    copy._renderers[key] = renderer;
                }
            }
            return copy;
        }

        public Renderer FindRenderer(string key) => _renderers.TryGetValue(key, out var r) ? r : null;

        public IEnumerable<KeyValuePair<string, Renderer>> AllRenderers => _renderers;

        public void Dispose()
        {
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            Roots.Clear();
            _renderers.Clear();
        }
    }
}
