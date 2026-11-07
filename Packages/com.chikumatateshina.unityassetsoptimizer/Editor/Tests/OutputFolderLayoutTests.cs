using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>出力先には、変換したオブジェクトごとのフォルダを作り、Materials/ 等を独立させる。</summary>
    public class OutputFolderLayoutTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        const string Out = TestAssets.Folder + "/Out";

        GameObject Object(string name, Color a, Color b)
        {
            var root = _a.Root(name);
            _a.Quad("A", root, _a.Material(name + "MA", "Unlit/Texture", _a.Texture(name + "TA", a)), Vector3.zero);
            _a.Quad("B", root, _a.Material(name + "MB", "Unlit/Texture", _a.Texture(name + "TB", b)), Vector3.right * 2);
            return root;
        }

        static string MaterialPath(GameObject prefab) => AssetDatabase.GetAssetPath(prefab.GetComponentInChildren<Renderer>().sharedMaterial);

        [Test]
        public void EachObjectGetsItsOwnFolder()
        {
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.Atlas);
            settings.prefabOutputFolder = Out;
            var first = _a.Run(new[] { Object("Booth", Color.red, Color.blue) }, settings);
            var second = _a.Run(new[] { Object("Stage", Color.green, Color.yellow) }, settings);
            Assert.IsTrue(first.Success, first.Error);
            Assert.IsTrue(second.Success, second.Error);

            Assert.AreEqual(Out + "/Booth/Booth.prefab", first.GeneratedPrefabs[0]);
            Assert.AreEqual(Out + "/Stage/Stage.prefab", second.GeneratedPrefabs[0]);
            StringAssert.StartsWith(Out + "/Booth/Materials/", MaterialPath(AssetDatabase.LoadAssetAtPath<GameObject>(first.GeneratedPrefabs[0])));
            StringAssert.StartsWith(Out + "/Stage/Materials/", MaterialPath(AssetDatabase.LoadAssetAtPath<GameObject>(second.GeneratedPrefabs[0])));
            Assert.IsFalse(AssetDatabase.IsValidFolder(Out + "/Materials"), "no shared Materials folder");
        }

        [Test]
        public void AssetsSharedByObjectsInOneRunGoToSharedFolder()
        {
            // 2 つのオブジェクトを 1 回で変換すると、アトラスは両方で共有される
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.Atlas);
            settings.prefabOutputFolder = Out;
            var result = _a.Run(new[] { Object("Left", Color.red, Color.blue), Object("Right", Color.green, Color.yellow) }, settings);
            Assert.IsTrue(result.Success, result.Error);
            CollectionAssert.AreEquivalent(new[] { Out + "/Left/Left.prefab", Out + "/Right/Right.prefab" }, result.GeneratedPrefabs);
            var left = AssetDatabase.LoadAssetAtPath<GameObject>(Out + "/Left/Left.prefab");
            StringAssert.StartsWith(Out + "/" + OptimizerPipeline.SharedOutputFolderName + "/", MaterialPath(left));
            Assert.IsTrue(result.Report.outputs.All(o => o.path.StartsWith(Out + "/Left/") || o.path.StartsWith(Out + "/Right/") || o.path.StartsWith(Out + "/_Shared/")));
        }
    }
}
