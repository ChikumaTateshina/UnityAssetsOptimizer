using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>ワールドでよくある構成（Collider 付きのプリミティブ等）のメッシュ結合。</summary>
    public class MeshCombineColliderTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        GameObject Cube(string name, GameObject parent, Material mat, Vector3 position)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); // BoxCollider 付き
            cube.name = name;
            cube.transform.SetParent(parent.transform);
            cube.transform.localPosition = position;
            cube.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return cube;
        }

        static OperationPlan CombinePlan(DryRunResult dry) => dry.Plans.First(p => p.Decision.operationId == FeatureIds.MeshCombine);

        [Test]
        public void ObjectsWithCollidersAreCombinedAndCollidersKept()
        {
            var mat = _a.Material("M", "Standard", null);
            var root = _a.Root("Root");
            // 空間セル（既定 10m、原点が境界）の同じセルに置く
            Cube("A", root, mat, Vector3.one);
            Cube("B", root, mat, Vector3.one * 3);
            var mc = Cube("C", root, mat, Vector3.one * 5);
            Object.DestroyImmediate(mc.GetComponent<BoxCollider>());
            mc.AddComponent<MeshCollider>();

            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.AreEqual(1, generated.GetComponentsInChildren<MeshRenderer>().Length, "3 cubes → 1 renderer");
            Assert.AreEqual(2, generated.GetComponentsInChildren<BoxCollider>().Length, "box colliders kept");
            var meshCollider = generated.GetComponentInChildren<MeshCollider>();
            Assert.IsNotNull(meshCollider);
            Assert.AreEqual(24, meshCollider.sharedMesh.vertexCount, "mesh collider still uses the original cube mesh");
        }

        [Test]
        public void RigidbodyObjectsAreNotCombined()
        {
            var mat = _a.Material("M", "Standard", null);
            var root = _a.Root("Root");
            Cube("A", root, mat, Vector3.zero).AddComponent<Rigidbody>();
            Cube("B", root, mat, Vector3.right * 2);
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine)));
            var plan = CombinePlan(dry);
            Assert.IsFalse(plan.WillExecute);
            Assert.IsTrue(plan.Items.Any(i => !i.included && i.reason.Contains("Rigidbody")), string.Join(" / ", plan.Items.Select(i => i.reason)));
        }

        [Test]
        public void NoPartnerReasonNamesTheDifference()
        {
            var mat = _a.Material("M", "Standard", null);
            var root = _a.Root("Root");
            Cube("A", root, mat, Vector3.zero);
            Cube("B", root, mat, Vector3.right * 2).GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine)));
            var plan = CombinePlan(dry);
            Assert.IsFalse(plan.WillExecute);
            Assert.IsTrue(plan.Items.All(i => i.reason.Contains("Cast Shadows")), string.Join(" / ", plan.Items.Select(i => i.reason)));
        }
    }
}
