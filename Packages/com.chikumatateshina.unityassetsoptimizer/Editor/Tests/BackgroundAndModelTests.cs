using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    public class BackgroundAndModelTests
    {
        TestAssets _a;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown() => _a.Cleanup();

        GameObject TwoQuads()
        {
            var matA = _a.Material("MatA", "Unlit/Texture", _a.Texture("TexA", Color.red));
            var matB = _a.Material("MatB", "Unlit/Texture", _a.Texture("TexB", Color.blue));
            var root = _a.Root("Root");
            _a.Quad("QuadA", root, matA, Vector3.zero);
            _a.Quad("QuadB", root, matB, Vector3.right * 2);
            return root;
        }

        [UnityTest]
        public IEnumerator BackgroundJobProducesSameResultAsSynchronousRun()
        {
            var root = TwoQuads();
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.Atlas, FeatureIds.MeshCombine);
            var syncDry = OptimizerPipeline.DryRun(OptimizerPipeline.Analyze(new[] { root }, settings));

            AnalysisSession session = null;
            var dry = new DryRunResult();
            IEnumerator Routine()
            {
                yield return OptimizerPipeline.AnalyzeRoutine(new[] { root }, settings, RunKind.Optimize, s => session = s);
                dry.Session = session;
                yield return OptimizerPipeline.DryRunRoutine(session, dry);
            }

            var job = new BackgroundJob("test", Routine());
            job.Start();
            int frames = 0;
            while (!job.IsCompleted && frames++ < 2000) yield return null;

            Assert.IsTrue(job.IsCompleted, "job finished");
            Assert.IsNull(job.Error, job.Error?.ToString());
            Assert.IsTrue(dry.Completed);
            Assert.IsTrue(dry.CanExecute);
            Assert.AreEqual(session.InputHash, syncDry.Session.InputHash);
            foreach (var key in new[] { MetricKeys.UniqueMaterials, MetricKeys.RendererCount, MetricKeys.Triangles, MetricKeys.DrawCalls })
                Assert.AreEqual(syncDry.Predicted.Get(key).value, dry.Predicted.Get(key).value, key);

            // 裏で作った予測結果のまま本実行できる
            var result = OptimizerPipeline.Execute(dry, new List<ForceApproval>());
            if (result.RunFolder != null) _a.RunFolders.Add(result.RunFolder);
            Assert.IsTrue(result.Success, result.Error);
        }

        [UnityTest]
        public IEnumerator CancellingBackgroundJobDisposesPreviewScenes()
        {
            var root = TwoQuads();
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.Atlas);
            int before = EditorSceneManager.previewSceneCount;
            var dry = new DryRunResult();
            IEnumerator Routine()
            {
                AnalysisSession session = null;
                yield return OptimizerPipeline.AnalyzeRoutine(new[] { root }, settings, RunKind.Optimize, s => session = s);
                dry.Session = session;
                yield return OptimizerPipeline.DryRunRoutine(session, dry);
            }

            var job = new BackgroundJob("cancel", Routine());
            job.Start();
            // ワーキングコピー（プレビューシーン）が作られるまで進める
            int frames = 0;
            while (!job.IsCompleted && EditorSceneManager.previewSceneCount == before && frames++ < 2000) yield return null;
            job.Cancel();
            yield return null;

            Assert.IsTrue(job.IsCancelled || job.IsCompleted);
            Assert.AreEqual(before, EditorSceneManager.previewSceneCount, "preview scenes are closed on cancel");
            Assert.IsFalse(dry.Completed || !job.IsCancelled, "cancelled run is not marked completed");
        }

        [Test]
        public void PrefabIsWrittenToChosenOutputFolderAndDiscardedWithRun()
        {
            var root = TwoQuads();
            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.Atlas);
            settings.prefabOutputFolder = TestAssets.Folder + "/Out/Optimized";
            var result = _a.Run(new[] { root }, settings);
            Assert.IsTrue(result.Success, result.Error);
            Assert.IsTrue(AssetPathUtility.IsUnder(result.GeneratedPrefabs[0], settings.prefabOutputFolder), result.GeneratedPrefabs[0]);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.IsNotNull(prefab);
            var mat = prefab.GetComponentInChildren<Renderer>().sharedMaterial;
            Assert.IsTrue(AssetPathUtility.IsUnder(AssetDatabase.GetAssetPath(mat), settings.prefabOutputFolder), "materials go to the chosen folder too");
            Assert.IsTrue(AssetPathUtility.IsUnder(AssetDatabase.GetAssetPath(mat.mainTexture), settings.prefabOutputFolder), "textures go to the chosen folder too");
            Assert.IsTrue(File.Exists(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, result.RunFolder, UnityAssetsOptimizerInfo.ManifestFileName)), "manifest stays in the managed folder");
            Assert.IsFalse(AssetDatabase.IsValidFolder(result.RunFolder + "/" + UnityAssetsOptimizerInfo.GeneratedFolderName), "no leftover empty Generated folder");

            var run = RestoreService.FindRuns().First(r => r.RunFolder == result.RunFolder);
            Assert.IsTrue(RestoreService.DiscardRun(run.RunFolder, out var error, run.Manifest), error);
            _a.RunFolders.Remove(result.RunFolder);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]), "prefab in the chosen folder is removed too");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GetAssetPath(mat)), "material removed");
            Assert.IsFalse(AssetDatabase.IsValidFolder(TestAssets.Folder + "/Out"), "folders created for the output are removed when empty");
        }

        [Test]
        public void InvalidOutputFolderIsRejected()
        {
            Assert.IsNotNull(OptimizationSettings.ValidateOutputFolder("C:/Temp"));
            Assert.IsNotNull(OptimizationSettings.ValidateOutputFolder("Assets/../Library"));
            Assert.IsNull(OptimizationSettings.ValidateOutputFolder("Assets/MyAvatar/Optimized"));
            Assert.IsNull(OptimizationSettings.ValidateOutputFolder(""));
        }

        /// <summary>FBX と同じ ModelImporter で読み込まれるモデル（OBJ）。Read/Write は無効にする。</summary>
        GameObject ModelAsset()
        {
            var path = TestAssets.Folder + "/Model.obj";
            File.WriteAllText(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, path), string.Join("\n", new[]
            {
                "g PartA", "usemtl MatA", "v 0 0 0", "v 1 0 0", "v 0 1 0", "v 1 1 0",
                "vt 0 0", "vt 1 0", "vt 0 1", "vt 1 1", "vn 0 0 1",
                "f 1/1/1 2/2/1 3/3/1", "f 2/2/1 4/4/1 3/3/1",
                "g PartB", "usemtl MatA", "v 2 0 0", "v 3 0 0", "v 2 1 0", "v 3 1 0",
                "vt 0 0", "vt 1 0", "vt 0 1", "vt 1 1", "vn 0 0 1",
                "f 5/5/2 6/6/2 7/7/2", "f 6/6/2 8/8/2 7/7/2",
            }));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        [Test]
        public void ObjectsInsideModelPrefabAreCombined()
        {
            var model = ModelAsset();
            Assert.AreEqual(PrefabAssetType.Model, PrefabUtility.GetPrefabAssetType(model));
            Assert.AreEqual(2, model.GetComponentsInChildren<MeshRenderer>().Length,
                string.Join(", ", model.GetComponentsInChildren<Transform>().Select(t => t.name)));

            var settings = TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine);
            var session = OptimizerPipeline.Analyze(new[] { model }, settings);
            Assert.IsFalse(session.Analysis.HasBlockingIssues, string.Join("\n", session.Analysis.Issues));
            var dry = OptimizerPipeline.DryRun(session);
            var combine = dry.Plans.First(p => p.Decision.operationId == FeatureIds.MeshCombine);
            Assert.IsTrue(combine.WillExecute, string.Join(" / ", combine.Items.Select(i => i.target + ": " + i.reason)));

            var result = OptimizerPipeline.Execute(dry, new List<ForceApproval>());
            if (result.RunFolder != null) _a.RunFolders.Add(result.RunFolder);
            Assert.IsTrue(result.Success, result.Error);
            var generated = AssetDatabase.LoadAssetAtPath<GameObject>(result.GeneratedPrefabs[0]);
            Assert.AreEqual(1, generated.GetComponentsInChildren<MeshRenderer>().Length, "two parts merged");
            Assert.AreEqual(4, result.After.Get(MetricKeys.Triangles).value);
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(generated), "output stays linked to the model");
            Assert.AreEqual(2, model.GetComponentsInChildren<MeshRenderer>().Length, "model asset untouched");
        }

        [Test]
        public void UnusedClipsInDependenciesDoNotBlockCombine()
        {
            // 依存に含まれていても、どの Animator/Animation/コンポーネントも使っていないクリップは判定に使わない
            var root = TwoQuads();
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("QuadA", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 1));
            AssetDatabase.CreateAsset(clip, TestAssets.Folder + "/Unused.anim");
            var session = OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine));
            Assert.IsFalse(session.Analysis.Renderers.Any(r => r.AnimatedAny));

            // Animation コンポーネントで実際に使うと、対象から外れる
            clip.legacy = true;
            var animation = root.AddComponent<Animation>();
            animation.AddClip(clip, "move");
            animation.clip = clip;
            session = OptimizerPipeline.Analyze(new[] { root }, TestAssets.Only(ContentPreset.World, FeatureIds.MeshCombine));
            Assert.IsTrue(session.Analysis.Renderers.First(r => r.Renderer.name == "QuadA").AnimatedAny);
        }
    }
}
