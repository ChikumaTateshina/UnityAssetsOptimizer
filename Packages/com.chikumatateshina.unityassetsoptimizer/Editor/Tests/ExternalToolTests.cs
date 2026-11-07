using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>外部ツール（AAO・lilNDMFMeshSimplifier・NDMF）との連携。ツールが導入されていない環境では該当テストを Ignore する。</summary>
    public class ExternalToolTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        static Type FindType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        static void Require(ExternalTool tool)
        {
            if (!tool.IsAvailable(out var reason)) Assert.Ignore(reason);
        }

        GameObject Avatar(int gridCells)
        {
            var descriptorType = FindType(AssetAnalyzer.AvatarDescriptorType);
            if (descriptorType == null) Assert.Ignore("VRChat Avatars SDK が導入されていません");
            var root = _a.Root("Avatar");
            root.AddComponent<Animator>();
            root.AddComponent(descriptorType);
            var mat = _a.Material("M", "Standard", null);
            _a.MeshObject("Body", root, _a.Grid("BodyMesh", gridCells), mat);
            _a.MeshObject("Face", root, _a.Grid("FaceMesh", gridCells), mat);
            return root;
        }

        [Test]
        public void UnknownToolIsUnavailableWithInstallHint()
        {
            var tool = new ExternalTool
            {
                DisplayName = "Missing Tool", PackageName = "com.example.missing-tool", RepositoryUrl = "https://example.com/vpm.json",
                VerifiedRange = ">=1.0.0 <2.0.0", MainType = "Example.Missing",
            };
            Assert.IsFalse(tool.IsAvailable(out var reason));
            StringAssert.Contains("https://example.com/vpm.json", reason);
            StringAssert.StartsWith("vcc://vpm/addRepo?url=https%3A%2F%2Fexample.com%2Fvpm.json", tool.VccAddRepositoryUrl);
        }

        [Test]
        public void ToolsAreSkippedWithReasonWhenMissing()
        {
            var root = Avatar(4);
            var settings = TestAssets.Only(ContentPreset.Avatar, FeatureIds.AaoTraceAndOptimize, FeatureIds.BuildPolygonReduction);
            var dry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, settings));
            foreach (var tool in new[] { ExternalTools.AvatarOptimizer, ExternalTools.LilMeshSimplifier })
            {
                if (tool.IsAvailable(out _)) continue;
                var id = tool == ExternalTools.AvatarOptimizer ? FeatureIds.AaoTraceAndOptimize : FeatureIds.BuildPolygonReduction;
                var plan = dry.Plans.First(p => p.Decision.operationId == id);
                Assert.IsFalse(plan.WillExecute);
                Assert.IsTrue(plan.Items.All(i => !i.included && i.reason.Contains(tool.DisplayName)), string.Join(" / ", plan.Items.Select(i => i.reason)));
            }
        }

        [Test]
        public void AaoTraceAndOptimizeIsAddedToOutputOnly()
        {
            Require(ExternalTools.AvatarOptimizer);
            var root = Avatar(4);
            var type = FindType(ExternalTools.AvatarOptimizer.MainType);
            var result = _a.Run(new[] { root }, TestAssets.Only(ContentPreset.Avatar, FeatureIds.AaoTraceAndOptimize));
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.IsNotNull(generated.GetComponent(type), "added to the generated avatar root");
            Assert.IsNull(root.GetComponentInChildren(type, true), "original unchanged");
        }

        [Test]
        public void BuildReductionTargetsGoalAndProtectsFace()
        {
            Require(ExternalTools.LilMeshSimplifier);
            var root = Avatar(20); // 800 triangles per mesh
            var face = root.transform.Find("Face").GetComponent<Renderer>();
            var settings = TestAssets.Only(ContentPreset.Avatar, FeatureIds.BuildPolygonReduction);
            settings.buildReductionTargetTriangles = 1200; // face 800 fixed → body 400/800 = 0.5
            settings.buildReductionMinQuality = 0.1f;
            var descriptorType = FindType(AssetAnalyzer.AvatarDescriptorType);
            var so = new SerializedObject(root.GetComponent(descriptorType));
            var viseme = so.FindProperty("VisemeSkinnedMesh");
            if (viseme == null) Assert.Ignore("VRCAvatarDescriptor.VisemeSkinnedMesh がありません");
            // Viseme は SkinnedMeshRenderer 型のため、顔を SkinnedMeshRenderer にする
            var faceGo = face.gameObject;
            var mesh = faceGo.GetComponent<MeshFilter>().sharedMesh;
            var mat = face.sharedMaterial;
            UnityEngine.Object.DestroyImmediate(face);
            UnityEngine.Object.DestroyImmediate(faceGo.GetComponent<MeshFilter>());
            var smr = faceGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.sharedMaterial = mat;
            viseme.objectReferenceValue = smr;
            so.ApplyModifiedPropertiesWithoutUndo();

            var type = FindType(ExternalTools.LilMeshSimplifier.MainType);
            var result = _a.Run(new[] { root }, settings);
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var body = generated.transform.Find("Body").GetComponent(type);
            Assert.IsNotNull(body);
            Assert.AreEqual(0.5f, new SerializedObject(body).FindProperty("quality").floatValue, 0.01f);
            Assert.IsNull(generated.transform.Find("Face").GetComponent(type), "face mesh is protected");
            Assert.IsNull(root.GetComponentInChildren(type, true), "original unchanged");
        }

        [Test]
        public void NdmfBuildEvaluationDoesNotTouchOriginal()
        {
            Require(ExternalTools.Ndmf);
            var root = Avatar(4);
            var before = root.GetComponentsInChildren<Component>(true).Length;
            var eval = BuildEvaluator.Evaluate(root, "test", measureSize: true);
            Assert.IsNull(eval.Error, eval.Error);
            Assert.IsNotNull(eval.Size);
            Assert.IsNull(eval.Size.Error, eval.Size.Error);
            Assert.Greater(eval.Size.Compressed, 0);
            Assert.IsFalse(AssetDatabase.IsValidFolder(UnityAssetsOptimizerInfo.GeneratedRoot + "/_Measure"), "temporary NDMF assets are removed");
            Assert.AreEqual(3, eval.Ranks.Count);
            Assert.AreEqual(64, eval.Metrics.Get(MetricKeys.Triangles).value, "2 grids of 4x4 cells");
            Assert.AreEqual(before, root.GetComponentsInChildren<Component>(true).Length);
            Assert.IsTrue(eval.Ranks.All(r => r.status == RankStatus.Official), string.Join(" / ", eval.Ranks.SelectMany(r => r.reasons)));
        }

        [Test]
        public void BundleSizeIsMeasuredAndTemporaryFilesRemoved()
        {
            var root = _a.Root("Bundle");
            _a.Quad("Q", root, _a.Material("M", "Standard", _a.Texture("T", Color.red, 256)), Vector3.zero);
            var size = BuildEvaluator.MeasureBundle(root, "test");
            Assert.IsNull(size.Error, size.Error);
            Assert.Greater(size.Compressed, 0);
            Assert.GreaterOrEqual(size.Uncompressed, size.Compressed);
            Assert.IsFalse(AssetDatabase.IsValidFolder(UnityAssetsOptimizerInfo.GeneratedRoot + "/_Measure"));
        }
    }
}
