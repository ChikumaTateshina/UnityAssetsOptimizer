using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>同じ対象を再変換したときは、新しく作らずに前回の出力を上書きする。</summary>
    public class RunOverwriteTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        /// <summary>ID が安定するよう、対象は Prefab Asset にする（未保存シーンのオブジェクトは照合しない）。</summary>
        GameObject SourcePrefab()
        {
            var root = _a.Root("Src");
            _a.Quad("A", root, _a.Material("MA", "Unlit/Texture", _a.Texture("TA", Color.red)), Vector3.zero);
            _a.Quad("B", root, _a.Material("MB", "Unlit/Texture", _a.Texture("TB", Color.blue)), Vector3.right * 2);
            return PrefabUtility.SaveAsPrefabAsset(root, TestAssets.Folder + "/Src.prefab");
        }

        static OptimizationSettings Settings() => TestAssets.Only(ContentPreset.World, FeatureIds.Atlas);

        static string[] Generated(RunResult r) => r.Report.outputs.Where(o => o.kind != "Prefab" && o.kind != "PrefabVariant").Select(o => o.path).ToArray();

        [Test]
        public void RerunOverwritesPrefabKeepingGuid()
        {
            var src = SourcePrefab();
            var first = _a.Run(new[] { src }, Settings());
            Assert.IsTrue(first.Success, first.Error);
            var firstGuid = AssetDatabase.AssetPathToGUID(first.GeneratedPrefabs[0]);
            var firstGenerated = Generated(first);
            Assert.IsNotEmpty(firstGenerated);

            // シーンに置いた前回の Prefab は、上書き後も同じ Prefab を指す
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(first.GeneratedPrefabs[0]));
            _a.SceneObjects.Add(instance);

            var second = _a.Run(new[] { src }, Settings());
            Assert.IsTrue(second.Success, second.Error);
            Assert.AreEqual(first.RunId, second.OverwrittenRunId);
            Assert.AreEqual(firstGuid, AssetDatabase.AssetPathToGUID(second.GeneratedPrefabs[0]), "same GUID");
            Assert.IsTrue(firstGenerated.All(p => AssetDatabase.LoadMainAssetAtPath(p) == null), "previous materials/textures/meshes are removed");
            Assert.IsTrue(Generated(second).All(p => AssetDatabase.LoadMainAssetAtPath(p) != null));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(second.GeneratedPrefabs[0]);
            var material = prefab.GetComponentInChildren<Renderer>().sharedMaterial;
            Assert.AreEqual(Generated(second).First(p => p.EndsWith(".mat")), AssetDatabase.GetAssetPath(material), "overwritten prefab uses the new outputs");
            Assert.IsTrue(instance != null, "the placed instance is kept (internal IDs are preserved)");
            Assert.AreEqual(second.GeneratedPrefabs[0], PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance));
            Assert.AreEqual(material, instance.GetComponentInChildren<Renderer>().sharedMaterial, "instance reflects the new content");

            var previous = RestoreService.FindRuns().First(r => r.Manifest.runId == first.RunId).Manifest;
            Assert.AreEqual(RunState.Overwritten, previous.state);
            Assert.AreEqual(second.RunId, previous.overwrittenBy);
            Assert.IsEmpty(previous.outputs);
        }

        [Test]
        public void RerunIntoOutputFolderReusesNames()
        {
            var src = SourcePrefab();
            var settings = Settings();
            settings.prefabOutputFolder = TestAssets.Folder + "/Out";
            var first = _a.Run(new[] { src }, settings);
            Assert.IsTrue(first.Success, first.Error);
            var second = _a.Run(new[] { src }, settings);
            Assert.IsTrue(second.Success, second.Error);
            Assert.AreEqual(first.GeneratedPrefabs[0], second.GeneratedPrefabs[0]);
            CollectionAssert.AreEquivalent(Generated(first), Generated(second), "no ' 1' suffixed duplicates");
        }

        [Test]
        public void ReferencedPreviousOutputIsKept()
        {
            var src = SourcePrefab();
            var first = _a.Run(new[] { src }, Settings());
            Assert.IsTrue(first.Success, first.Error);
            var oldMaterialPath = Generated(first).First(p => p.EndsWith(".mat"));
            // 利用者が前回の Material を別の Prefab で使っている
            var user = _a.Root("User");
            _a.Quad("Q", user, AssetDatabase.LoadAssetAtPath<Material>(oldMaterialPath), Vector3.zero);
            PrefabUtility.SaveAsPrefabAsset(user, TestAssets.Folder + "/User.prefab");

            var second = _a.Run(new[] { src }, Settings());
            Assert.IsTrue(second.Success, second.Error);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(oldMaterialPath), "material used elsewhere is kept");
            var previous = RestoreService.FindRuns().First(r => r.Manifest.runId == first.RunId).Manifest;
            Assert.AreEqual(oldMaterialPath, previous.outputs.Single().path);
        }

        [Test]
        public void OverwriteCanBeDisabled()
        {
            var src = SourcePrefab();
            var settings = Settings();
            settings.overwritePreviousOutput = false;
            var first = _a.Run(new[] { src }, settings);
            var second = _a.Run(new[] { src }, settings);
            Assert.IsTrue(first.Success && second.Success);
            Assert.IsNull(second.OverwrittenRunId);
            Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(first.GeneratedPrefabs[0]), AssetDatabase.AssetPathToGUID(second.GeneratedPrefabs[0]));
            Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(first.GeneratedPrefabs[0]));
        }

        [Test]
        public void DifferentKindIsNotOverwritten()
        {
            var src = SourcePrefab();
            var first = _a.Run(new[] { src }, Settings());
            Assert.IsTrue(first.Success, first.Error);
            Assert.IsNull(RunOverwrite.FindPrevious(first.Report.targets, RunKind.Android));
            Assert.IsNotNull(RunOverwrite.FindPrevious(first.Report.targets, RunKind.Optimize));
        }
    }
}
