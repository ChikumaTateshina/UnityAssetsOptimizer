using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer.Tests
{
    /// <summary>
    /// 原本不変・ロールバック・アトラス化の統合テスト。テスト用アセットは Assets/VAOTestTemp に作成し、終了時に削除する。
    /// </summary>
    public class PipelineIntegrationTests
    {
        const string TempFolder = "Assets/VAOTestTemp";
        readonly List<GameObject> _sceneObjects = new List<GameObject>();
        readonly List<string> _runFolders = new List<string>();
        string[] _originalPaths;

        [SetUp]
        public void SetUp()
        {
            AssetPathUtility.EnsureFolder(TempFolder);
            var texA = CreateTexture("TexA", Color.red);
            var texB = CreateTexture("TexB", Color.blue);
            var matA = CreateMaterial("MatA", texA);
            var matB = CreateMaterial("MatB", texB);

            var root = new GameObject("VAOTestRoot");
            _sceneObjects.Add(root);
            CreateQuad("QuadA", root, matA, Vector3.zero);
            CreateQuad("QuadB", root, matB, Vector3.right * 2);

            _originalPaths = new[] { TempFolder + "/TexA.png", TempFolder + "/TexB.png", TempFolder + "/MatA.mat", TempFolder + "/MatB.mat" };
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _sceneObjects) if (go != null) Object.DestroyImmediate(go);
            _sceneObjects.Clear();
            foreach (var run in _runFolders) RestoreService.DiscardRun(run, out _);
            _runFolders.Clear();
            AssetDatabase.DeleteAsset(TempFolder);
        }

        static Texture2D CreateTexture(string name, Color color)
        {
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            tex.SetPixels32(Enumerable.Repeat((Color32)color, 64 * 64).ToArray());
            var path = TempFolder + "/" + name + ".png";
            File.WriteAllBytes(Path.Combine(VaoInfo.ProjectRoot, path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material CreateMaterial(string name, Texture2D tex)
        {
            var mat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = tex };
            AssetDatabase.CreateAsset(mat, TempFolder + "/" + name + ".mat");
            return mat;
        }

        static void CreateQuad(string name, GameObject parent, Material mat, Vector3 position)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(parent.transform);
            quad.transform.localPosition = position;
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        Dictionary<string, string> HashOriginals() =>
            _originalPaths.ToDictionary(p => p, p =>
                HashUtility.Sha256File(AssetPathUtility.ToFullPath(p), false) + ":" + HashUtility.Sha256File(AssetPathUtility.ToFullPath(p) + ".meta", false) + ":" + AssetDatabase.AssetPathToGUID(p));

        static OptimizationSettings AtlasOnlySettings()
        {
            var settings = OptimizationSettings.CreateDefault(ContentPreset.World);
            foreach (var def in FeatureCatalog.All) settings.SetMode(def.Id, ExecutionMode.Off);
            settings.SetMode(FeatureIds.Atlas, ExecutionMode.Auto);
            settings.SetMode(FeatureIds.TextureCompression, ExecutionMode.Auto);
            return settings;
        }

        [Test]
        public void ExecuteAtlasesWithoutModifyingOriginals()
        {
            var before = HashOriginals();
            var session = OptimizerPipeline.Analyze(_sceneObjects, AtlasOnlySettings());
            Assert.IsFalse(session.Analysis.HasBlockingIssues, string.Join("\n", session.Analysis.Issues));
            Assert.AreEqual(2, session.Baseline.Get(MetricKeys.UniqueMaterials).value);

            var dry = OptimizerPipeline.DryRun(session);
            Assert.IsNull(dry.Error);
            Assert.IsTrue(dry.CanExecute, string.Join("\n", dry.Plans.SelectMany(p => p.Decision.reasons)));
            Assert.AreEqual(1, dry.Predicted.Get(MetricKeys.UniqueMaterials).value, "predicted unique materials");
            Assert.AreEqual(MetricKind.Unavailable, dry.Predicted.Get(MetricKeys.DrawCalls).kind, "draw calls must not be fabricated");

            var result = OptimizerPipeline.Execute(dry, new List<ForceApproval>());
            if (result.RunFolder != null) _runFolders.Add(result.RunFolder);
            Assert.IsTrue(result.Success, result.Error);
            Assert.IsTrue(result.Report.integrity.passed);
            CollectionAssert.AreEqual(before, HashOriginals(), "originals must be byte-identical");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            var materials = prefab.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToList();
            Assert.AreEqual(1, materials.Count);
            Assert.IsTrue(AssetPathUtility.IsUnder(AssetDatabase.GetAssetPath(materials[0]), result.RunFolder));
            Assert.AreEqual(1, result.After.Get(MetricKeys.UniqueMaterials).value);

            // 原本の Renderer はそのまま
            foreach (var r in _sceneObjects[0].GetComponentsInChildren<Renderer>())
                Assert.IsTrue(AssetPathUtility.IsUnder(AssetDatabase.GetAssetPath(r.sharedMaterial), TempFolder));

            var checks = RestoreService.CheckOriginals(RestoreService.FindRuns().First(r => r.RunFolder == result.RunFolder).Manifest);
            Assert.IsTrue(checks.All(c => c.State == OriginalState.Unchanged));
        }

        [Test]
        public void CancelRollsBackAndLeavesNoOutput()
        {
            var before = HashOriginals();
            var session = OptimizerPipeline.Analyze(_sceneObjects, AtlasOnlySettings());
            var dry = OptimizerPipeline.DryRun(session);
            Assert.IsTrue(dry.CanExecute);

            int calls = 0;
            var result = OptimizerPipeline.Execute(dry, new List<ForceApproval>(), (m, p) => ++calls > 3);
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Cancelled);
            Assert.IsNull(result.RunFolder);
            Assert.IsFalse(Directory.Exists(Path.Combine(VaoInfo.ProjectRoot, VaoInfo.RunFolder(result.RunId))), "rolled back");
            CollectionAssert.AreEqual(before, HashOriginals());
        }

        [Test]
        public void ChangedSettingsAfterDryRunAbort()
        {
            var settings = AtlasOnlySettings();
            var session = OptimizerPipeline.Analyze(_sceneObjects, settings);
            var dry = OptimizerPipeline.DryRun(session);
            dry.Session.Settings.atlasPadding += 1; // Dry Run 後の設定変更を模擬
            var result = OptimizerPipeline.Execute(dry, new List<ForceApproval>());
            Assert.IsFalse(result.Success);
        }

        [Test]
        public void CorruptedSnapshotIsDetected()
        {
            const string runId = "test-snapshot";
            var runFolder = VaoInfo.RunFolder(runId);
            _runFolders.Add(runFolder);
            AssetPathUtility.EnsureFolder(runFolder);
            var snap = SnapshotService.Create(runId, _originalPaths, _sceneObjects, null);
            Assert.IsTrue(snap.Success);
            Assert.IsEmpty(SnapshotService.VerifyCopies(runId, snap.Assets));

            File.WriteAllText(Path.Combine(VaoInfo.SnapshotFolderFull(runId), snap.Assets[0].snapshotFile), "corrupted");
            Assert.IsNotEmpty(SnapshotService.VerifyCopies(runId, snap.Assets));
        }

        [Test]
        public void UnsupportedShaderIsSkippedWithReason()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(TempFolder + "/MatB.mat");
            var custom = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(custom, TempFolder + "/Custom.mat");
            _sceneObjects[0].transform.GetChild(1).GetComponent<Renderer>().sharedMaterial = custom;

            var session = OptimizerPipeline.Analyze(_sceneObjects, AtlasOnlySettings());
            var dry = OptimizerPipeline.DryRun(session);
            var atlas = dry.Plans.First(p => p.Decision.operationId == FeatureIds.Atlas);
            Assert.IsFalse(atlas.WillExecute, "only one compatible material left");
            Assert.IsTrue(atlas.Items.Any(i => !i.included && i.reason.Contains("未対応シェーダー")));
            Assert.IsNotNull(mat);
        }
    }
}
