using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    public class EmptyObjectCleanupTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        GameObject Grouped(Material mat)
        {
            var root = _a.Root("Root");
            var g1 = new GameObject("G1");
            g1.transform.SetParent(root.transform, false);
            var g2 = new GameObject("G2");
            g2.transform.SetParent(root.transform, false);
            _a.Quad("A", g1, mat, Vector3.one);
            _a.Quad("B", g1, mat, Vector3.one * 2);
            _a.Quad("C", g2, mat, Vector3.one * 3);
            _a.Quad("D", g2, mat, Vector3.one * 4);
            var keep = new GameObject("Empty"); // 元から空のオブジェクトは残す
            keep.transform.SetParent(root.transform, false);
            return root;
        }

        static OptimizationSettings Settings(PrefabOutputMode mode, EmptyObjectHandling handling = EmptyObjectHandling.Delete)
        {
            var s = TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine);
            s.outputMode = mode;
            s.emptyObjectHandling = handling;
            return s;
        }

        static string[] Hidden(GameObject go) =>
            go.GetComponentsInChildren<Transform>(true).Where(t => !t.gameObject.activeSelf && t.CompareTag(EmptyObjectCleaner.EditorOnlyTag)).Select(t => t.name).ToArray();

        static string[] Names(GameObject go) => go.GetComponentsInChildren<Transform>(true).Select(t => t.name).ToArray();

        [Test]
        public void EmptiedObjectsAndGroupsAreRemovedForUnpackedOutput()
        {
            var root = Grouped(_a.Material("M", "Standard", null));
            var result = _a.Run(new[] { root }, Settings(PrefabOutputMode.Unpacked));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            CollectionAssert.AreEquivalent(new[] { "Root", "G1", "A", "Empty" }, Names(generated), "B, C, D and the emptied group G2 are removed");
            Assert.AreEqual(1, generated.GetComponentsInChildren<MeshRenderer>().Length);
            Assert.AreEqual(8, Names(root).Length, "original unchanged");
        }

        [Test]
        public void NothingIsRemovedWhenDisabled()
        {
            var root = Grouped(_a.Material("M", "Standard", null));
            var result = _a.Run(new[] { root }, Settings(PrefabOutputMode.Unpacked, EmptyObjectHandling.Keep));
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(8, Names(AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0])).Length);
        }

        [Test]
        public void VariantOutputHidesInsteadOfDeleting()
        {
            var src = PrefabUtility.SaveAsPrefabAsset(Grouped(_a.Material("M", "Standard", null)), TestAssets.Folder + "/Grouped.prefab");
            var result = _a.Run(new[] { src }, Settings(PrefabOutputMode.VariantWhenPossible));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(generated));
            Assert.AreEqual(8, Names(generated).Length);
            CollectionAssert.AreEquivalent(new[] { "B", "G2" }, Hidden(generated), "top-level emptied objects are hidden and EditorOnly");
            Assert.IsTrue(result.Report.log.entries.Any(e => e.message.Contains("Variant")), "explains why nothing was deleted");
        }

        [Test]
        public void DefaultHidesEmptiedObjectsAsEditorOnly()
        {
            var root = Grouped(_a.Material("M", "Standard", null));
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine);
            Assert.AreEqual(EmptyObjectHandling.HideEditorOnly, settings.emptyObjectHandling);
            var result = _a.Run(new[] { root }, settings);
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.AreEqual(8, Names(generated).Length);
            CollectionAssert.AreEquivalent(new[] { "B", "G2" }, Hidden(generated));
            Assert.IsTrue(generated.transform.Find("Empty").gameObject.activeSelf, "originally empty objects are untouched");
            Assert.IsTrue(root.GetComponentsInChildren<Transform>(true).All(t => t.gameObject.activeSelf && t.CompareTag("Untagged")), "original unchanged");
        }
    }

    public class SceneReplaceTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        [Test]
        public void SceneInstancesAreReplacedWithReferencesRemappedAndUndoable()
        {
            var root = _a.Root("Src");
            _a.Quad("A", root, _a.Material("MA", "Unlit/Texture", _a.Texture("TA", Color.red)), Vector3.zero);
            _a.Quad("B", root, _a.Material("MB", "Unlit/Texture", _a.Texture("TB", Color.blue)), Vector3.right * 2);
            var src = PrefabUtility.SaveAsPrefabAsset(root, TestAssets.Folder + "/Src.prefab");

            var parent = _a.Root("Parent");
            var placed = (GameObject)PrefabUtility.InstantiatePrefab(src);
            placed.transform.SetParent(parent.transform, false);
            placed.transform.localPosition = new Vector3(5, 0, 0);
            placed.name = "Placed";
            var looker = _a.Root("Looker");
            var aim = looker.AddComponent<AimConstraint>();
            aim.AddSource(new ConstraintSource { sourceTransform = placed.transform.Find("B"), weight = 1 });

            var result = _a.Run(new[] { src }, TestAssets.Only(ContentPreset.World, FeatureIds.Atlas));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);

            var notes = new List<string>();
            var candidates = SceneReplacer.FindCandidates(result.Report.targets, result.Report.outputs, notes);
            var candidate = candidates.Single(c => c.Original == placed);
            Assert.IsNull(candidate.Problem);

            var replaced = SceneReplacer.Replace(new[] { candidate });
            Assert.AreEqual(1, replaced.Replaced);
            Assert.AreEqual(1, replaced.ReferencesRemapped);
            Assert.IsTrue(placed == null, "old instance removed");
            var instance = parent.transform.Find("Placed").gameObject;
            Assert.AreEqual(generated, PrefabUtility.GetCorrespondingObjectFromSource(instance));
            Assert.AreEqual(new Vector3(5, 0, 0), instance.transform.localPosition);
            Assert.AreEqual(instance.transform.Find("B"), aim.GetSource(0).sourceTransform, "reference remapped to the new instance");

            Undo.PerformUndo();
            var restored = parent.transform.Find("Placed").gameObject;
            Assert.AreEqual(src, PrefabUtility.GetCorrespondingObjectFromSource(restored), "undo restores the original instance");
            Assert.AreEqual(restored.transform.Find("B"), aim.GetSource(0).sourceTransform, "undo restores the reference");
            _a.SceneObjects.Add(restored);
        }
    }
}
