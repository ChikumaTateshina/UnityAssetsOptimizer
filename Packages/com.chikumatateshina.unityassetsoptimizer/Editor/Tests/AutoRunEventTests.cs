using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ChikumaTateshina.UnityAssetsOptimizer.Tests
{
    /// <summary>
    /// UI は変更イベント（ObjectChangeEvents / projectChanged）で解析と予測を再実行する。
    /// 裏で行う解析・予測そのものがイベントを発生させると再実行が止まらなくなるため、発生しないことを確認する。
    /// </summary>
    public class AutoRunEventTests
    {
        TestAssets _a;
        int _objectEvents;
        int _projectEvents;

        [SetUp]
        public void SetUp() => _a = new TestAssets();

        [TearDown]
        public void TearDown()
        {
            ObjectChangeEvents.changesPublished -= OnChanges;
            EditorApplication.projectChanged -= OnProject;
            _a.Cleanup();
        }

        void OnChanges(ref ObjectChangeEventStream stream) => _objectEvents += stream.length;
        void OnProject() => _projectEvents++;

        [UnityTest]
        public IEnumerator AnalysisAndDryRunDoNotRaiseChangeEvents()
        {
            var matA = _a.Material("MatA", "Standard", _a.Texture("TexA", Color.red));
            var matB = _a.Material("MatB", "Standard", _a.Texture("TexB", Color.blue));
            var root = _a.Root("Root");
            _a.Quad("QuadA", root, matA, Vector3.zero);
            _a.Quad("QuadB", root, matB, Vector3.right * 2);
            var settings = OptimizationSettings.CreateDefault(ContentPreset.World);
            settings.SetMode(FeatureIds.PolygonReduction, ExecutionMode.Auto);
            settings.SetMode(FeatureIds.LodGeneration, ExecutionMode.Auto);
            settings.visualDiff = true;

            // 準備で発生したイベントを流し切ってから数える
            for (int i = 0; i < 5; i++) yield return null;
            ObjectChangeEvents.changesPublished += OnChanges;
            EditorApplication.projectChanged += OnProject;

            AnalysisSession session = null;
            var dry = new DryRunResult();
            IEnumerator Routine()
            {
                yield return OptimizerPipeline.AnalyzeRoutine(new[] { root }, settings, RunKind.Optimize, s => session = s);
                dry.Session = session;
                yield return OptimizerPipeline.DryRunRoutine(session, dry);
            }
            var job = new BackgroundJob("events", Routine());
            job.Start();
            int frames = 0;
            while (!job.IsCompleted && frames++ < 2000) yield return null;
            for (int i = 0; i < 5; i++) yield return null;

            Assert.IsNull(job.Error, job.Error?.ToString());
            Assert.IsTrue(dry.Completed);
            Assert.AreEqual(0, _objectEvents, "background analysis must not publish object change events");
            Assert.AreEqual(0, _projectEvents, "background analysis must not change the project");
        }
    }
}
