using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    public sealed class AnalysisSession
    {
        public OptimizationSettings Settings;
        public ResolvedSelection Selection;
        public DependencyGraph Graph;
        public AnalysisReport Analysis;
        public MeasurementContext Context;
        public MetricSet Baseline;
        public string InputHash;
        public string SettingsHash;
        public List<RankResult> Ranks = new List<RankResult>();

        public IList<GameObject> Roots => Selection.Roots;
    }

    public sealed class DryRunResult
    {
        public AnalysisSession Session;
        public List<OperationPlan> Plans = new List<OperationPlan>();
        public MetricSet Predicted;
        public bool ValidationPassed = true;
        public List<string> ValidationMessages = new List<string>();
        public long SnapshotBytes;
        public List<RankResult> Ranks = new List<RankResult>();
        public string Error;
        public RunLog Log = new RunLog();

        public IEnumerable<OperationPlan> ForcePlans => Plans.Where(p => p.UsesForce);
        public bool CanExecute => Error == null && ValidationPassed && !Session.Analysis.HasBlockingIssues && Plans.Any(p => p.WillExecute);
    }

    public sealed class RunResult
    {
        public bool Success;
        public bool Cancelled;
        public string RunId;
        public string RunFolder;
        public string Error;
        public RunReport Report;
        public MetricSet After;
        public List<string> GeneratedPrefabs = new List<string>();
        public List<RankResult> Ranks = new List<RankResult>();
        public string ReportPath;
    }

    /// <summary>処理の中断（fail-closed）。</summary>
    public sealed class VaoAbortException : Exception
    {
        public VaoAbortException(string message) : base(message) { }
    }

    /// <summary>
    /// 非破壊パイプライン。原本は読み取りのみ。生成物はステージングに作り、検証後に専用フォルダへコミットする。
    /// </summary>
    public static class OptimizerPipeline
    {
        /// <param name="progress">(メッセージ, 進捗0-1) を受け取り、キャンセル要求時に true を返す。</param>
        public delegate bool ProgressCallback(string message, float progress);

        public static AnalysisSession Analyze(IEnumerable<GameObject> selection, OptimizationSettings settings)
        {
            var session = new AnalysisSession { Settings = settings.Clone() };
            session.Selection = SelectionResolver.Resolve(selection);
            session.Graph = DependencyGraph.Build(session.Selection.Roots);
            session.Analysis = AssetAnalyzer.Analyze(session.Graph);

            if (settings.preset == ContentPreset.Avatar && !session.Analysis.HasAvatarDescriptor)
                session.Analysis.Issues.Add(new AnalysisIssue { Source = "Preset", Message = "Avatar プリセットですが VRCAvatarDescriptor が見つかりません。" });
            if (settings.preset == ContentPreset.World && session.Analysis.HasAvatarDescriptor)
                session.Analysis.Issues.Add(new AnalysisIssue { Source = "Preset", Message = "World プリセットですが VRCAvatarDescriptor が含まれています。" });

            session.Context = MeasurementContext.Capture("構造値（処理前の階層・アセットから計算）");
            session.Baseline = MetricsCalculator.Compute(session.Roots, session.Context);
            session.InputHash = ComputeInputHash(session);
            session.SettingsHash = session.Settings.ComputeHash();
            if (settings.preset == ContentPreset.Avatar) session.Ranks = EvaluateRanks(session.Roots, session.Analysis.HasBuildTimeTools);
            return session;
        }

        /// <summary>選択・依存ファイル・コンポーネントのシリアライズ内容のハッシュ。Dry Run 後の変更検出に使う。</summary>
        static string ComputeInputHash(AnalysisSession session)
        {
            var sb = new StringBuilder();
            foreach (var t in session.Selection.Targets) sb.Append(t.globalObjectId).Append('\n');
            foreach (var p in session.Graph.AssetPaths)
            {
                var full = AssetPathUtility.ToFullPath(p);
                sb.Append(p).Append('=').Append(full != null ? HashUtility.Sha256File(full) : "unresolved").Append('\n');
            }
            foreach (var root in session.Roots)
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                    sb.Append(c == null ? "(missing)" : EditorJsonUtility.ToJson(c)).Append('\n');
            return HashUtility.Sha256(sb.ToString());
        }

        public static List<RankResult> EvaluateRanks(IList<GameObject> roots, bool buildTimeTools)
        {
            var list = new List<RankResult>();
            var official = new SdkOfficialRankProvider();
            var estimate = new VersionedEstimateRankProvider();
            foreach (VrcPlatform platform in Enum.GetValues(typeof(VrcPlatform)))
            {
                var r = official.Evaluate(roots, platform, buildTimeTools);
                if (r.status == RankStatus.Unavailable)
                {
                    var e = estimate.Evaluate(roots, platform, buildTimeTools);
                    e.reasons.InsertRange(0, r.reasons);
                    r = e;
                }
                list.Add(r);
            }
            return list;
        }

        static OptimizationContext CreateContext(AnalysisSession session) => new OptimizationContext
        {
            Graph = session.Graph,
            Analysis = session.Analysis,
            Settings = session.Settings,
        };

        /// <summary>一時領域（メモリ上）で同一アルゴリズムの生成を行い、予測値を算出する。AssetDatabase には書き込まない。</summary>
        public static DryRunResult DryRun(AnalysisSession session, ProgressCallback progress = null)
        {
            var result = new DryRunResult { Session = session };
            result.SnapshotBytes = SnapshotService.EstimateSize(session.Graph.AssetPaths);
            try
            {
                result.Plans = OptimizationPlanner.Plan(CreateContext(session), OperationRegistry.CreateAll());
                if (session.Analysis.HasBlockingIssues) return result;

                using (var copy = WorkingCopy.Create(session.Roots))
                using (var gen = new GenerationContext(false, null, session.Settings, copy, result.Log, () => progress != null && progress("Dry Run", 0.5f)))
                {
                    foreach (var plan in result.Plans.Where(p => p.WillExecute))
                    {
                        if (progress != null && progress("Dry Run: " + plan.Operation.DisplayName, 0.5f)) throw new OperationCanceledException();
                        plan.Operation.Execute(gen, plan);
                    }
                    foreach (var plan in result.Plans.Where(p => p.WillExecute))
                    {
                        var v = plan.Operation.Validate(gen, plan);
                        if (!v.Passed) result.ValidationPassed = false;
                        result.ValidationMessages.AddRange(v.Messages.Select(m => plan.Operation.Id + ": " + m));
                    }

                    var ctx = MeasurementContext.Capture("Dry Run 生成物（メモリ上）から計算");
                    result.Predicted = MetricsCalculator.Compute(copy.Roots, ctx, gen.Textures);
                    foreach (var m in result.Predicted.metrics)
                        if (m.hasValue) m.method = "Dry Run: " + m.method;
                    if (session.Settings.preset == ContentPreset.Avatar)
                        result.Ranks = EvaluateRanks(copy.Roots, session.Analysis.HasBuildTimeTools);
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = "Dry Run はキャンセルされました。";
            }
            catch (Exception e)
            {
                result.Error = "Dry Run に失敗しました: " + e.Message;
                Debug.LogException(e);
            }
            return result;
        }

        static string PlanSignature(IEnumerable<OperationPlan> plans)
        {
            var sb = new StringBuilder();
            foreach (var p in plans)
            {
                sb.Append(p.Decision.operationId).Append(':').Append(p.WillExecute).Append(':').Append(p.Decision.mode).Append('\n');
                foreach (var i in p.Items.Where(i => i.included)) sb.Append("  ").Append(i.target).Append('\n');
            }
            return HashUtility.Sha256(sb.ToString());
        }

        public static RunResult Execute(DryRunResult dryRun, List<ForceApproval> approvals, ProgressCallback progress = null)
        {
            var session = dryRun.Session;
            var result = new RunResult();
            var log = new RunLog();
            var report = new RunReport
            {
                unityVersion = Application.unityVersion,
                sdkVersions = SdkInfo.Describe(),
                preset = session.Settings.preset.ToString(),
                createdAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                manualOverrideEnabled = ManualOverride.IsEnabled,
                settings = session.Settings,
                contextBefore = session.Context,
                log = log,
            };
            report.targets.AddRange(session.Selection.Targets);
            report.forceApprovals.AddRange(approvals ?? new List<ForceApproval>());
            result.Report = report;

            bool Cancel() => progress != null && progress("処理中...", 0.5f);
            void Step(string message, float p)
            {
                if (progress != null && progress(message, p)) throw new OperationCanceledException("ユーザーによりキャンセルされました。");
                log.Info("pipeline", message);
            }

            string runFolder = null;
            bool committed = false;
            WorkingCopy copy = null;
            GenerationContext gen = null;
            SnapshotResult snapshot = null;
            List<SceneState> sceneStates = null;
            List<OperationPlan> plans = null;

            try
            {
                Step("入力の再検証", 0.02f);
                if (dryRun.Error != null || !dryRun.CanExecute) throw new VaoAbortException("Dry Run が完了していないか、実行可能な処理がありません。");
                var fresh = Analyze(session.Roots, session.Settings);
                if (fresh.InputHash != session.InputHash) throw new VaoAbortException("Dry Run 後に対象または依存アセットが変更されました。再度 Dry Run を行ってください。");
                if (fresh.SettingsHash != session.SettingsHash) throw new VaoAbortException("Dry Run 後に設定が変更されました。再度 Dry Run を行ってください。");
                if (fresh.Analysis.HasBlockingIssues) throw new VaoAbortException("実行不可の問題があります: " + string.Join(" / ", fresh.Analysis.Issues.Where(i => i.Blocking).Select(i => i.Message)));

                result.RunId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + HashUtility.Short(session.InputHash, 6);
                report.runId = result.RunId;
                runFolder = VaoInfo.RunFolder(result.RunId);
                result.RunFolder = runFolder;
                if (AssetDatabase.IsValidFolder(runFolder) || Directory.Exists(Path.Combine(VaoInfo.ProjectRoot, runFolder)))
                {
                    runFolder = null; // 既存フォルダは削除対象にしない
                    throw new VaoAbortException("出力フォルダが既に存在します（衝突）。時間をおいて再実行してください。");
                }

                // 必要容量の確認（スナップショット + 生成物の余裕）
                long needed = dryRun.SnapshotBytes * 2 + 64L * 1024 * 1024;
                var drive = new DriveInfo(Path.GetPathRoot(VaoInfo.ProjectRoot));
                if (drive.IsReady && drive.AvailableFreeSpace < needed)
                    throw new VaoAbortException("ディスク容量が不足しています（必要目安 " + Metric.FormatNumber(needed, "bytes") + "）。");

                sceneStates = SnapshotService.CaptureSceneStates();

                Step("スナップショット作成", 0.1f);
                AssetPathUtility.EnsureFolder(VaoInfo.GeneratedRoot);
                AssetPathUtility.EnsureFolder(runFolder);
                snapshot = SnapshotService.Create(result.RunId, session.Graph.AssetPaths, session.Roots, Cancel);
                if (!snapshot.Success) throw new VaoAbortException("復元用スナップショットを作成できないため開始しません: " + string.Join(" / ", snapshot.Errors));
                var copyErrors = SnapshotService.VerifyCopies(result.RunId, snapshot.Assets);
                if (copyErrors.Count > 0) throw new VaoAbortException("スナップショットの検証に失敗したため開始しません: " + string.Join(" / ", copyErrors));

                Step("計画の再作成", 0.2f);
                plans = OptimizationPlanner.Plan(CreateContext(fresh), OperationRegistry.CreateAll());
                if (PlanSignature(plans) != PlanSignature(dryRun.Plans)) throw new VaoAbortException("計画が Dry Run と一致しません。再度 Dry Run を行ってください。");
                foreach (var plan in plans.Where(p => p.UsesForce))
                    if (approvals == null || !approvals.Any(a => a.operationId == plan.Decision.operationId))
                        throw new VaoAbortException(plan.Decision.operationId + " の FORCE が承認されていません。");

                Step("生成（ステージング）", 0.3f);
                var staging = runFolder + "/" + VaoInfo.StagingFolderName;
                AssetPathUtility.EnsureFolder(staging);
                copy = WorkingCopy.Create(session.Roots);
                gen = new GenerationContext(true, staging, session.Settings, copy, log, Cancel);
                foreach (var plan in plans.Where(p => p.WillExecute))
                {
                    Step("生成: " + plan.Operation.DisplayName, 0.4f);
                    plan.Operation.Execute(gen, plan);
                }
                gen.FinalizeTextures();

                Step("検証", 0.6f);
                bool valid = true;
                foreach (var plan in plans.Where(p => p.WillExecute))
                {
                    var v = plan.Operation.Validate(gen, plan);
                    report.validation.AddRange(v.Messages.Select(m => plan.Decision.operationId + ": " + m));
                    if (!v.Passed) valid = false;
                }
                if (!valid) throw new VaoAbortException("生成物の検証に失敗しました: " + string.Join(" / ", report.validation));

                var prefabPaths = new List<string>();
                for (int i = 0; i < copy.Roots.Count; i++)
                {
                    var path = AssetDatabase.GenerateUniqueAssetPath(staging + "/" + AssetPathUtility.SanitizeFileName(copy.Roots[i].name) + ".prefab");
                    gen.CheckWrite(path);
                    PrefabUtility.SaveAsPrefabAsset(copy.Roots[i], path, out bool ok);
                    if (!ok) throw new VaoAbortException("Prefab の保存に失敗しました: " + path);
                    prefabPaths.Add(path);
                    gen.Outputs.Add(new OutputAsset { path = path, guid = AssetDatabase.AssetPathToGUID(path), kind = "Prefab", operationId = "pipeline", sourceRef = session.Selection.Targets[i].globalObjectId });
                }

                for (int i = 0; i < prefabPaths.Count; i++)
                {
                    var v = StructuralValidator.Compare(session.Roots[i], AssetDatabase.LoadAssetAtPath<GameObject>(prefabPaths[i]));
                    if (!v.Passed) throw new VaoAbortException("構造検証に失敗しました: " + string.Join(" / ", v.Messages));
                }
                report.validation.Add("構造検証: OK（階層・コンポーネント構成・Mesh/Material 参照・三角形数）");

                Step("原本の整合性確認", 0.7f);
                var integrity = IntegrityChecker.Check(snapshot.Assets, snapshot.Hierarchy, session.Roots, sceneStates);
                if (!integrity.passed) throw new VaoAbortException("原本の整合性検証に失敗しました: " + string.Join(" / ", integrity.errors));

                Step("コミット", 0.8f);
                var generatedFolder = runFolder + "/" + VaoInfo.GeneratedFolderName;
                var moveError = AssetDatabase.MoveAsset(staging, generatedFolder);
                if (!string.IsNullOrEmpty(moveError)) throw new VaoAbortException("コミット（移動）に失敗しました: " + moveError);
                foreach (var o in gen.Outputs) o.path = generatedFolder + o.path.Substring(staging.Length);
                result.GeneratedPrefabs = prefabPaths.Select(p => generatedFolder + p.Substring(staging.Length)).ToList();

                Step("事後計測", 0.9f);
                var prefabRoots = result.GeneratedPrefabs.Select(AssetDatabase.LoadAssetAtPath<GameObject>).ToList();
                var afterContext = MeasurementContext.Capture("構造値（コミット済み生成物から計算）");
                result.After = MetricsCalculator.Compute(prefabRoots, afterContext);
                if (session.Settings.preset == ContentPreset.Avatar) result.Ranks = EvaluateRanks(prefabRoots, session.Analysis.HasBuildTimeTools);
                report.contextAfter = afterContext;
                report.contextWarnings.AddRange(session.Context.CompareWith(afterContext));
                report.metrics = RunReport.Compare(session.Baseline, dryRun.Predicted, result.After);
                report.outputs.AddRange(gen.Outputs);
                report.integrity = IntegrityChecker.Check(snapshot.Assets, snapshot.Hierarchy, session.Roots, sceneStates);
                FillOperations(report, plans);
                AddDeterminismNotes(report, plans);
                report.result = "Committed";
                report.restoreState = "復元可能（生成物の破棄で元状態。原本スナップショット " + snapshot.Assets.Count + " 件を検証済み）";

                var manifest = new RunManifest
                {
                    runId = result.RunId,
                    toolVersion = VaoInfo.ToolVersion,
                    unityVersion = Application.unityVersion,
                    sdkVersion = SdkInfo.Describe(),
                    createdAtUtc = report.createdAtUtc,
                    inputHash = session.InputHash,
                    settingsHash = session.SettingsHash,
                    snapshotHash = snapshot.SnapshotHash,
                    state = RunState.Committed,
                };
                manifest.targets.AddRange(session.Selection.Targets);
                manifest.outputs.AddRange(gen.Outputs);
                manifest.outputPaths.AddRange(gen.Outputs.Select(o => o.path));
                manifest.decisions.AddRange(plans.Select(p => p.Decision));
                manifest.snapshotAssets.AddRange(snapshot.Assets);
                manifest.sceneStates.AddRange(sceneStates);
                manifest.forceApprovals.AddRange(report.forceApprovals);

                var runFull = Path.Combine(VaoInfo.ProjectRoot, runFolder);
                File.WriteAllText(Path.Combine(runFull, VaoInfo.ManifestFileName), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
                ReportWriter.WriteFiles(report, Path.Combine(runFull, VaoInfo.ReportsFolderName), "report");
                AssetDatabase.Refresh();
                result.ReportPath = runFolder + "/" + VaoInfo.ReportsFolderName + "/report.md";

                committed = true;
                result.Success = true;
                log.Info("pipeline", "コミット完了: " + runFolder);
            }
            catch (OperationCanceledException e)
            {
                result.Cancelled = true;
                result.Error = e.Message;
            }
            catch (VaoAbortException e)
            {
                result.Error = e.Message;
            }
            catch (Exception e)
            {
                result.Error = "予期しないエラー: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                gen?.Dispose();
                copy?.Dispose();
            }

            if (!committed) Rollback(result, report, plans, snapshot, session, sceneStates, runFolder, log);
            return result;
        }

        static void Rollback(RunResult result, RunReport report, List<OperationPlan> plans, SnapshotResult snapshot, AnalysisSession session,
            List<SceneState> sceneStates, string runFolder, RunLog log)
        {
            log.Error("pipeline", result.Error ?? "失敗");
            if (runFolder != null && !RestoreService.DiscardRun(runFolder, out var discardError))
                log.Error("rollback", discardError);
            else if (runFolder != null)
                log.Info("rollback", "生成途中のデータを破棄しました: " + runFolder);

            report.result = result.Cancelled ? "Cancelled (RolledBack)" : "Failed (RolledBack)";
            if (plans != null) FillOperations(report, plans);
            report.metrics = RunReport.Compare(session.Baseline, null, null);
            if (snapshot != null)
            {
                report.integrity = IntegrityChecker.Check(snapshot.Assets, snapshot.Hierarchy, session.Roots, sceneStates);
                report.restoreState = report.integrity.passed ? "原本は処理前と同一（ロールバック済み）" : "原本の差異を検出（要確認）";
            }
            else
            {
                report.restoreState = "スナップショット完了前に中断（原本への書き込みなし。作成途中のデータは破棄済み）";
            }

            var id = result.RunId ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-aborted";
            report.runId = id;
            ReportWriter.WriteFiles(report, VaoInfo.FailureLogFolderFull, id);
            result.ReportPath = Path.Combine(VaoInfo.FailureLogFolderFull, id + ".md");
            result.RunFolder = null;
        }

        static void FillOperations(RunReport report, List<OperationPlan> plans)
        {
            report.operations.Clear();
            foreach (var p in plans)
            {
                var op = new OperationReport { decision = p.Decision, expectedEffect = p.ExpectedEffect };
                op.items.AddRange(p.Items);
                op.notes.AddRange(p.Notes);
                report.operations.Add(op);
            }
        }

        static void AddDeterminismNotes(RunReport report, List<OperationPlan> plans)
        {
            report.nonDeterministicNotes.Add("PNG エンコード・テクスチャ圧縮の結果は Unity バージョン/エンコーダに依存する可能性があります。");
            var atlas = plans.FirstOrDefault(p => p.WillExecute && p.Data is TextureAtlasOperation.AtlasPlanData)?.Data as TextureAtlasOperation.AtlasPlanData;
            if (atlas != null && atlas.Groups.Any(g => g.Sources.Values.Any(d => d.Values.Any(s => !s.FromSourceFile))))
                report.nonDeterministicNotes.Add("一部テクスチャはインポート済みデータを GPU 経由で読み戻しました（TGA/PSD 等）。結果は GPU/ドライバに依存し、非可逆です。");
        }
    }
}
