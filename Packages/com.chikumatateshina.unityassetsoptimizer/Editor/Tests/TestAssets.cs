using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>テスト用アセットの作成と後始末。すべて Assets/UnityAssetsOptimizerTestTemp 配下に作る。</summary>
    public sealed class TestAssets
    {
        public const string Folder = "Assets/UnityAssetsOptimizerTestTemp";
        public readonly List<GameObject> SceneObjects = new List<GameObject>();
        public readonly List<string> RunFolders = new List<string>();

        public TestAssets()
        {
            AssetPathUtility.EnsureFolder(Folder);
        }

        public void Cleanup()
        {
            foreach (var go in SceneObjects) if (go != null) Object.DestroyImmediate(go);
            SceneObjects.Clear();
            foreach (var run in RunFolders) RestoreService.DiscardRun(run, out _);
            RunFolders.Clear();
            AssetDatabase.DeleteAsset(Folder);
        }

        public Texture2D Texture(string name, Color color, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(Enumerable.Repeat((Color32)color, size * size).ToArray());
            var path = Folder + "/" + name + ".png";
            File.WriteAllBytes(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public Material Material(string name, string shader, Texture2D tex)
        {
            var s = Shader.Find(shader);
            if (s == null) return null;
            var mat = new Material(s);
            if (tex != null) mat.mainTexture = tex;
            AssetDatabase.CreateAsset(mat, Folder + "/" + name + ".mat");
            return mat;
        }

        public GameObject Root(string name)
        {
            var root = new GameObject(name);
            SceneObjects.Add(root);
            return root;
        }

        public GameObject Quad(string name, GameObject parent, Material mat, Vector3 position)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(parent.transform);
            quad.transform.localPosition = position;
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return quad;
        }

        /// <summary>平面グリッド（n×n セル）。内部頂点は誤差 0 で縮約できる。</summary>
        public Mesh Grid(string name, int n)
        {
            var mesh = new Mesh { name = name };
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                {
                    v.Add(new Vector3((float)x / n, 0, (float)y / n));
                    uv.Add(new Vector2((float)x / n, (float)y / n));
                }
            var t = new List<int>();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * (n + 1) + x;
                    t.AddRange(new[] { i, i + n + 1, i + 1, i + 1, i + n + 1, i + n + 2 });
                }
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, Folder + "/" + name + ".asset");
            return mesh;
        }

        public GameObject MeshObject(string name, GameObject parent, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        public static OptimizationSettings Only(ContentPreset preset, params string[] features)
        {
            var settings = OptimizationSettings.CreateDefault(preset);
            foreach (var def in FeatureCatalog.All) settings.SetMode(def.Id, ExecutionMode.Off);
            foreach (var f in features) settings.SetMode(f, ExecutionMode.Auto);
            settings.visualDiff = false;
            return settings;
        }

        public RunResult Run(IEnumerable<GameObject> selection, OptimizationSettings settings, RunKind kind = RunKind.Optimize)
        {
            var session = OptimizerPipeline.Analyze(selection, settings, kind);
            var dry = OptimizerPipeline.DryRun(session);
            var result = OptimizerPipeline.Execute(dry, new List<ForceApproval>());
            if (result.RunFolder != null) RunFolders.Add(result.RunFolder);
            return result;
        }

        public Dictionary<string, string> Hash(IEnumerable<string> assetPaths) =>
            assetPaths.ToDictionary(p => p, p =>
                HashUtility.Sha256File(AssetPathUtility.ToFullPath(p), false) + ":" +
                HashUtility.Sha256File(AssetPathUtility.ToFullPath(p) + ".meta", false) + ":" + AssetDatabase.AssetPathToGUID(p));
    }
}
