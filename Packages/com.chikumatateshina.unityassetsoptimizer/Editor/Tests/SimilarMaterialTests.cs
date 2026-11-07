using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>近似 Material は候補として表示するだけで、ユーザーが選んだものだけ統合する。</summary>
    public class SimilarMaterialTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        Material Colored(string name, Texture2D tex, Color color)
        {
            var m = _a.Material(name, "Standard", tex);
            m.color = color;
            EditorUtility.SetDirty(m);
            return m;
        }

        GameObject Scene(Material a, Material b, Material c = null)
        {
            var root = _a.Root("Root");
            _a.Quad("A", root, a, Vector3.zero);
            _a.Quad("A2", root, a, Vector3.up);
            _a.Quad("B", root, b, Vector3.right);
            if (c != null) _a.Quad("C", root, c, Vector3.left);
            return root;
        }

        static OptimizationSettings Settings() => TestAssets.Only(ContentPreset.World, FeatureIds.SimilarMaterialMerge);

        static int UniqueMaterials(GameObject go) =>
            go.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().Count();

        [Test]
        public void CandidatesAreListedWithDifferences()
        {
            var tex = _a.Texture("T", Color.white);
            var a = Colored("A", tex, new Color(1f, 0.5f, 0.5f));
            var b = Colored("B", tex, new Color(1f, 0.55f, 0.5f));
            var other = Colored("Other", _a.Texture("T2", Color.black), new Color(1f, 0.5f, 0.5f));
            var root = Scene(a, b, other);
            var session = OptimizerPipeline.Analyze(new[] { root }, Settings());
            var groups = MaterialSimilarity.FindGroups(session.Analysis, m => m, 8);
            Assert.AreEqual(1, groups.Count, "different textures are not similar");
            Assert.AreEqual(a, groups[0].Representative, "the most used material is the representative");
            Assert.AreEqual(b, groups[0].Members.Single().Material);
            Assert.AreEqual("_Color", groups[0].Members[0].Differences.Single().Property);
        }

        [Test]
        public void NothingIsMergedWithoutSelection()
        {
            var tex = _a.Texture("T", Color.white);
            var root = Scene(Colored("A", tex, Color.red), Colored("B", tex, new Color(0.95f, 0, 0)));
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, Settings()));
            Assert.IsFalse(dry.Plans.First(p => p.Decision.operationId == FeatureIds.SimilarMaterialMerge).WillExecute);
        }

        [Test]
        public void SelectedMaterialsAreMerged()
        {
            var tex = _a.Texture("T", Color.white);
            var a = Colored("A", tex, Color.red);
            var b = Colored("B", tex, new Color(0.95f, 0, 0));
            var root = Scene(a, b);
            var settings = Settings();
            settings.similarMaterialMerges.Add(new MaterialMergeChoice { member = MaterialEquivalence.StableId(b), representative = MaterialEquivalence.StableId(a) });
            var result = _a.Run(new[] { root }, settings);
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.AreEqual(1, UniqueMaterials(generated));
            Assert.AreEqual(a, generated.transform.Find("B").GetComponent<Renderer>().sharedMaterial);
            Assert.AreEqual(2, UniqueMaterials(root), "original unchanged");
            Assert.AreEqual(new Color(0.95f, 0, 0), b.color, "material asset unchanged");
        }

        [Test]
        public void StaleSelectionIsSkipped()
        {
            var tex = _a.Texture("T", Color.white);
            var a = Colored("A", tex, Color.red);
            var b = Colored("B", tex, Color.red);
            b.SetFloat("_Glossiness", 0.9f);
            b.mainTexture = _a.Texture("T3", Color.green); // テクスチャが変わったので近似ではなくなった
            var root = Scene(a, b);
            var settings = Settings();
            settings.similarMaterialMerges.Add(new MaterialMergeChoice { member = MaterialEquivalence.StableId(b), representative = MaterialEquivalence.StableId(a) });
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, settings));
            var plan = dry.Plans.First(p => p.Decision.operationId == FeatureIds.SimilarMaterialMerge);
            Assert.IsFalse(plan.WillExecute);
            Assert.AreEqual(SupportState.Unsupported, plan.Items.Single().support);
        }

        [Test]
        public void MergedMaterialsAreAtlasedTogether()
        {
            var a = Colored("A", _a.Texture("TA", Color.white), Color.white);
            var b = Colored("B", a.mainTexture as Texture2D, new Color(0.9f, 0.9f, 0.9f));
            var root = Scene(a, b);
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.SimilarMaterialMerge, FeatureIds.Atlas);
            settings.similarMaterialMerges.Add(new MaterialMergeChoice { member = MaterialEquivalence.StableId(b), representative = MaterialEquivalence.StableId(a) });
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, settings));
            var order = dry.Plans.Select(p => p.Decision.operationId).ToList();
            Assert.Less(order.IndexOf(FeatureIds.SimilarMaterialMerge), order.IndexOf(FeatureIds.Atlas));
            Assert.IsTrue(dry.Plans.First(p => p.Decision.operationId == FeatureIds.SimilarMaterialMerge).WillExecute);
        }
    }
}
