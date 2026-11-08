using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>変換結果の FBX 保存（Unity の FBX Exporter がない環境では Ignore）。</summary>
    public class FbxExportTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp()
        {
            if (!FbxExport.IsAvailable(out var reason)) Assert.Ignore(reason);
            _a = new TestAssets();
        }

        [TearDown]
        public void TearDown() => _a?.Cleanup();

        GameObject Plates()
        {
            var mat = _a.Material("M", "Standard", _a.Texture("T", Color.white));
            var root = _a.Root("Booth");
            for (int i = 0; i < 3; i++)
            {
                var go = _a.MeshObject("P" + i, root, _a.Grid("G" + i, 4), mat);
                go.transform.localPosition = new Vector3(1 + i, 0, 1);
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            }
            return root;
        }

        static OptimizationSettings Settings(bool fbx)
        {
            var s = TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine);
            s.prefabOutputFolder = TestAssets.Folder + "/Out";
            s.exportFbx = fbx;
            return s;
        }

        [Test]
        public void CombinedMeshIsStoredInFbx()
        {
            var root = Plates();
            var result = _a.Run(new[] { root }, Settings(true));
            Assert.IsTrue(result.Success, result.Error);
            var fbxPath = TestAssets.Folder + "/Out/Booth/Booth.fbx";
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath), "FBX is saved next to the prefab");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var mesh = prefab.GetComponentsInChildren<MeshFilter>().Single().sharedMesh;
            Assert.AreEqual(fbxPath, AssetDatabase.GetAssetPath(mesh), "the prefab uses the mesh inside the FBX");
            Assert.AreEqual(3 * 32, mesh.triangles.Length / 3);
            Assert.IsTrue(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1), "lightmap UVs survive the round trip");
            Assert.IsFalse(result.Report.outputs.Any(o => o.kind == "Mesh" && AssetDatabase.LoadMainAssetAtPath(o.path) == null), "no dangling mesh outputs");
            Assert.IsFalse(Directory.Exists(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, TestAssets.Folder, "Out/Booth/Meshes")) &&
                           Directory.GetFiles(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, TestAssets.Folder, "Out/Booth/Meshes"), "*.asset").Length > 0,
                "relinked .asset meshes are removed");
        }

        [Test]
        public void RerunReplacesFbx()
        {
            // 上書きは ID が安定する対象（保存済みの Prefab 等）が条件。未保存のシーンのオブジェクトは毎回新しく作る
            var source = PrefabUtility.SaveAsPrefabAsset(Plates(), TestAssets.Folder + "/Booth.prefab");
            var first = _a.Run(new[] { source }, Settings(true));
            var second = _a.Run(new[] { source }, Settings(true));
            Assert.IsTrue(first.Success && second.Success, first.Error + second.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(second.GeneratedPrefabs[0]);
            Assert.AreEqual(TestAssets.Folder + "/Out/Booth/Booth.fbx", AssetDatabase.GetAssetPath(prefab.GetComponentsInChildren<MeshFilter>().Single().sharedMesh));
        }

        [Test]
        public void FbxCanBeDisabled()
        {
            var result = _a.Run(new[] { Plates() }, Settings(false));
            Assert.IsTrue(result.Success, result.Error);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(TestAssets.Folder + "/Out/Booth/Booth.fbx"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            StringAssert.EndsWith(".asset", AssetDatabase.GetAssetPath(prefab.GetComponentsInChildren<MeshFilter>().Single().sharedMesh));
        }
    }
}
