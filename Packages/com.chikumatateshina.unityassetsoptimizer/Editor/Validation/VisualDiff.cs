using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    [Serializable]
    public class VisualDiffResult
    {
        public bool available;
        public string reason;
        /// <summary>全画素・全視点の RGB 平均絶対差（0〜1）。</summary>
        public double meanAbsDiff;
        /// <summary>RGB のいずれかが閾値（0.1）を超えて変化した画素の割合（0〜1）。</summary>
        public double changedPixelRatio;
        public int views;
        public int resolution;
        public string conditions;
    }

    /// <summary>
    /// 隔離プレビューシーンで処理前後を同条件（固定カメラ3方向・平行光源1灯・アンビエント固定）でレンダリングし、画像差分を求める。
    /// 参考値であり、機能同等の証明にはならない。グラフィックスデバイスがない環境（-nographics）では取得不可。
    /// </summary>
    public static class VisualDiff
    {
        const int Resolution = 256;
        static readonly Vector3[] Directions = { new Vector3(0, 0.2f, 1), new Vector3(1, 0.2f, 0), new Vector3(0, 0.2f, -1) };

        public static VisualDiffResult Compare(IList<GameObject> before, IList<GameObject> after)
        {
            var result = new VisualDiffResult { resolution = Resolution };
            result.conditions = "隔離プレビュー / 固定カメラ " + Directions.Length + " 方向 / 平行光源1灯 / " + Resolution + "px（参考値）";
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                result.reason = "グラフィックスデバイスがないため取得不可（batchmode -nographics 等）";
                return result;
            }

            var boundsBefore = Bounds(before);
            if (boundsBefore == null)
            {
                result.reason = "描画対象の Renderer がありません";
                return result;
            }
            var b = boundsBefore.Value;

            using (var sceneA = RenderScene.Create(before))
            using (var sceneB = RenderScene.Create(after))
            {
                double sum = 0;
                long changed = 0, total = 0;
                foreach (var dir in Directions)
                {
                    var a = sceneA.Render(b, dir.normalized);
                    var c = sceneB.Render(b, dir.normalized);
                    for (int i = 0; i < a.Length; i++)
                    {
                        float dr = Math.Abs(a[i].r - c[i].r) / 255f, dg = Math.Abs(a[i].g - c[i].g) / 255f, db = Math.Abs(a[i].b - c[i].b) / 255f;
                        sum += (dr + dg + db) / 3;
                        if (dr > 0.1f || dg > 0.1f || db > 0.1f) changed++;
                        total++;
                    }
                }
                result.available = true;
                result.views = Directions.Length;
                result.meanAbsDiff = sum / total;
                result.changedPixelRatio = (double)changed / total;
            }
            return result;
        }

        static Bounds? Bounds(IList<GameObject> roots)
        {
            Bounds? b = null;
            foreach (var r in roots.SelectMany(x => x.GetComponentsInChildren<Renderer>()))
            {
                if (!r.enabled) continue;
                if (b == null) b = r.bounds;
                else
                {
                    var tmp = b.Value;
                    tmp.Encapsulate(r.bounds);
                    b = tmp;
                }
            }
            return b;
        }

        sealed class RenderScene : IDisposable
        {
            Scene _scene;
            Camera _camera;
            RenderTexture _rt;

            public static RenderScene Create(IList<GameObject> roots)
            {
                var s = new RenderScene { _scene = EditorSceneManager.NewPreviewScene() };
                // 元シーンに何も追加しないよう、HideAndDontSave の保持オブジェクトをプレビューシーンへ移してから複製する
                var holder = Create(s._scene, "UnityAssetsOptimizer_Holder");
                foreach (var root in roots) Object.Instantiate(root, holder.transform, true);
                var lightGo = Create(s._scene, "UnityAssetsOptimizer_Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;
                light.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);

                var camGo = Create(s._scene, "UnityAssetsOptimizer_Camera");
                s._camera = camGo.AddComponent<Camera>();
                s._camera.enabled = false;
                s._camera.scene = s._scene;
                s._camera.clearFlags = CameraClearFlags.SolidColor;
                s._camera.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
                s._camera.fieldOfView = 30f;
                s._rt = new RenderTexture(Resolution, Resolution, 24, RenderTextureFormat.ARGB32);
                s._camera.targetTexture = s._rt;
                return s;
            }

            static GameObject Create(Scene scene, string name)
            {
                var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
                SceneManager.MoveGameObjectToScene(go, scene);
                return go;
            }

            public Color32[] Render(Bounds b, Vector3 dir)
            {
                float radius = Math.Max(0.01f, b.extents.magnitude);
                float distance = radius / Mathf.Sin(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                _camera.transform.position = b.center + dir * distance;
                _camera.transform.LookAt(b.center);
                _camera.nearClipPlane = Math.Max(0.001f, distance - radius * 1.5f);
                _camera.farClipPlane = distance + radius * 1.5f;
                _camera.Render();

                var previous = RenderTexture.active;
                RenderTexture.active = _rt;
                var tex = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
                tex.Apply();
                RenderTexture.active = previous;
                var px = tex.GetPixels32();
                Object.DestroyImmediate(tex);
                return px;
            }

            public void Dispose()
            {
                if (_camera != null) _camera.targetTexture = null;
                if (_rt != null) Object.DestroyImmediate(_rt);
                if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            }
        }
    }
}
