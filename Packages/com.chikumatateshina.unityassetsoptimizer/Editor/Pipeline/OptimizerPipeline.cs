using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class AnalysisSession
    {
        public OptimizationSettings Settings;
        public RunKind Kind;
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
        public VisualDiffResult VisualDiff;
        public ConversionReport Conversion;
        public List<string> OutputKinds = new List<string>();
        /// <summary>Dry Run が最後まで終わったか（中断・エラー時は false）。</summary>
        public bool Completed;

        public IEnumerable<OperationPlan> ForcePlans => Plans.Where(p => p.UsesForce);
        public bool CanExecute => Error == null && (Completed || Session.Analysis.HasBlockingIssues) && ValidationPassed && !Session.Analysis.HasBlockingIssues && Plans.Any(p => p.WillExecute);
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
        /// <summary>出力先フォルダへ移した生成物と、新しく作ったフォルダ（失敗時の後始末用）。</summary>
        public List<string> MovedOutputs = new List<string>();
        public List<string> CreatedFolders = new List<string>();
        public List<RankResult> Ranks = new List<RankResult>();
        public string ReportPath;
        public ConversionReport Conversion;
        public VisualDiffResult VisualDiff;
        /// <summary>出力を上書きした前回の実行（なければ null）と、その内容。</summary>
        public string OverwrittenRunId;
        public string OverwriteSummary;
    }

    /// <summary>処理の中断（fail-closed）。</summary>
    public sealed class OptimizerAbortException : Exception
    {
        public OptimizerAbortException(string message) : base(message) { }
    }

    /// <summary>
    /// 非破壊パイプライン。原本は読み取りのみ。生成物はステージングに作り、検証後に専用フォルダへコミットする。
    /// </summary>
    public static class OptimizerPipeline
    {
        /// <summary>空きディスク容量の取得（テストで差し替え可能）。</summary>
        internal static Func<long> FreeSpaceProvider = () =>
        {
            var drive = new DriveInfo(Path.GetPathRoot(UnityAssetsOptimizerInfo.ProjectRoot));
            return drive.IsReady ? drive.AvailableFreeSpace : long.MaxValue;
        };

        /// <param name="progress">(メッセージ, 進捗0-1) を受け取り、キャンセル要求時に true を返す。</param>
        public delegate bool ProgressCallback(string message, float progress);

        public static AnalysisSession Analyze(IEnumerable<GameObject> selection, OptimizationSettings settings, RunKind kind = RunKind.Optimize)
        {
            AnalysisSession result = null;
            BackgroundJob.RunToCompletion(AnalyzeRoutine(selection, settings, kind, s => result = s));
            return result;
        }

        /// <summary>解析（現在値の算出）のコルーチン。BackgroundJob で実行するとエディタの操作を妨げない。</summary>
        public static IEnumerator AnalyzeRoutine(IEnumerable<GameObject> selection, OptimizationSettings settings, RunKind kind, Action<AnalysisSession> done)
        {
            var session = new AnalysisSession { Settings = settings.Clone(), Kind = kind };
            yield return new JobProgress("対象を解決しています", 0.02f);
            session.Selection = SelectionResolver.Resolve(selection);
            yield return new JobProgress("依存関係を解析しています", 0.05f);
            yield return DependencyGraph.BuildRoutine(session.Selection.Roots, g => session.Graph = g);
            yield return new JobProgress("コンポーネントを解析しています", 0.15f);
            yield return AssetAnalyzer.AnalyzeRoutine(session.Graph, r => session.Analysis = r);

            if (settings.preset == ContentPreset.Avatar && !session.Analysis.HasAvatarDescriptor)
                session.Analysis.Issues.Add(new AnalysisIssue { Source = "Preset", Message = "Avatar プリセットですが VRCAvatarDescriptor が見つかりません。" });
            if (settings.preset == ContentPreset.World && session.Analysis.HasAvatarDescriptor)
                session.Analysis.Issues.Add(new AnalysisIssue { Source = "Preset", Message = "World プリセットですが VRCAvatarDescriptor が含まれています。" });

            yield return new JobProgress("現在値を計算しています", 0.3f);
            session.Context = MeasurementContext.Capture("構造値（処理前の階層・アセットから計算）");
            session.Baseline = MetricsCalculator.Compute(session.Roots, session.Context);
            yield return new JobProgress("入力のハッシュを計算しています", 0.35f);
            yield return InputHashRoutine(session);
            session.SettingsHash = session.Settings.ComputeHash();
            if (settings.preset == ContentPreset.Avatar)
            {
                yield return new JobProgress("Performance Rank を評価しています", 0.4f);
                session.Ranks = EvaluateRanks(session.Roots, session.Analysis.HasBuildTimeTools);
            }
            done(session);
        }

        /// <summary>
        /// 選択・依存ファイル・コンポーネントのシリアライズ内容のハッシュ。Dry Run 後の変更検出に使う。
        /// ファイルのハッシュは Unity API を使わないため別スレッドで計算する。
        /// </summary>
        static IEnumerator InputHashRoutine(AnalysisSession session)
        {
            var files = session.Graph.AssetPaths.Select(p => (path: p, full: AssetPathUtility.ToFullPath(p))).ToList();
            var hashes = new string[files.Count];
            yield return Task.Run(() =>
            {
                for (int i = 0; i < files.Count; i++) hashes[i] = files[i].full != null ? HashUtility.Sha256File(files[i].full) : "unresolved";
            });

            var sb = new StringBuilder();
            foreach (var t in session.Selection.Targets) sb.Append(t.globalObjectId).Append('\n');
            for (int i = 0; i < files.Count; i++) sb.Append(files[i].path).Append('=').Append(hashes[i]).Append('\n');
            int count = 0;
            foreach (var root in session.Roots)
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    sb.Append(c == null ? "(missing)" : EditorJsonUtility.ToJson(c)).Append('\n');
                    if (++count % 64 == 0) yield return null;
                }
            session.InputHash = HashUtility.Sha256(sb.ToString());
        }

        /// <summary>
        /// Performance Rank を評価する。SDK の評価は対象の状態（VRC Constraint のグループ等）を更新するため、
        /// 原本や生成済み Prefab に触れないよう、常に一時的な複製（プレビューシーン）で評価する。
        /// </summary>
        public static List<RankResult> EvaluateRanks(IList<GameObject> roots, bool buildTimeTools)
        {
            var list = new List<RankResult>();
            var official = new SdkOfficialRankProvider();
            var estimate = new VersionedEstimateRankProvider();
            using (var copy = WorkingCopy.Create(roots))
            {
                foreach (VrcPlatform platform in Enum.GetValues(typeof(VrcPlatform)))
                {
                    var r = official.Evaluate(copy.Roots, platform, buildTimeTools);
                    if (r.status == RankStatus.Unavailable)
                    {
                        var e = estimate.Evaluate(copy.Roots, platform, buildTimeTools);
                        e.reasons.InsertRange(0, r.reasons);
                        r = e;
                    }
                    list.Add(r);
                }
            }
            return list;
        }

        static OptimizationContext CreateContext(AnalysisSession session)
        {
            var context = new OptimizationContext
            {
                Graph = session.Graph,
                Analysis = session.Analysis,
                Settings = session.Settings,
            };
            if (session.Kind != RunKind.Optimize)
                context.ModeOverrides = new Dictionary<string, ExecutionMode>
                {
                    { MobileShaderConversionOperation.OperationId, session.Settings.mobileShaderMode },
                    { MobileTextureOperation.OperationId, ExecutionMode.Auto },
                };
            return context;
        }

        static VrcPlatform PlatformOf(RunKind kind) => kind == RunKind.iOS ? VrcPlatform.iOS : VrcPlatform.Android;

        public static IOptimizationOperation[] Operations(RunKind kind)
        {
            if (kind == RunKind.Optimize) return OperationRegistry.CreateAll();
            var platform = PlatformOf(kind);
            return new IOptimizationOperation[] { new MobileShaderConversionOperation(platform), new MobileTextureOperation(platform) };
        }

        static string OutputFolderName(RunKind kind) => kind == RunKind.Optimize ? UnityAssetsOptimizerInfo.GeneratedFolderName : kind.ToString();

        static void CheckConversionEnabled(AnalysisSession session)
        {
            if (session.Kind == RunKind.Optimize) return;
            if (!CapabilityDatabase.IsConversionEnabled(PlatformOf(session.Kind), session.Settings.preset, out var reason))
                throw new OptimizerAbortException(session.Kind + " 変換は無効です: " + reason);
        }

        /// <summary>一時領域（メモリ上）で同一アルゴリズムの生成を行い、予測値を算出する。AssetDatabase には書き込まない。</summary>
        public static DryRunResult DryRun(AnalysisSession session, ProgressCallback progress = null)
        {
            var result = new DryRunResult { Session = session };
            try
            {
                BackgroundJob.RunToCompletion(DryRunRoutine(session, result, progress));
            }
            catch (Exception e)
            {
                SetDryRunError(result, e);
            }
            return result;
        }

        /// <summary>Dry Run の例外を結果に反映する（同期実行・バックグラウンド実行の共通処理）。</summary>
        public static void SetDryRunError(DryRunResult result, Exception e)
        {
            if (e is OperationCanceledException) result.Error = "Dry Run はキャンセルされました。";
            else if (e is OptimizerAbortException) result.Error = e.Message;
            else
            {
                result.Error = "Dry Run に失敗しました: " + e.Message;
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Dry Run（予測値の算出）のコルーチン。メモリ上でだけ生成し、AssetDatabase には書き込まない。
        /// 中断（Dispose）されるとワーキングコピーと一時オブジェクトを破棄する。
        /// </summary>
        public static IEnumerator DryRunRoutine(AnalysisSession session, DryRunResult result, ProgressCallback progress = null)
        {
            result.SnapshotBytes = SnapshotService.EstimateSize(session.Graph.AssetPaths);
            CheckConversionEnabled(session);
            yield return new JobProgress("処理を計画しています", 0.45f);
            result.Plans = OptimizationPlanner.Plan(CreateContext(session), Operations(session.Kind));
            if (session.Analysis.HasBlockingIssues) yield break;

            using (var copy = WorkingCopy.Create(session.Roots, session.Settings.outputMode))
            using (var gen = new GenerationContext(false, null, session.Settings, copy, result.Log, () => progress != null && progress("Dry Run", 0.5f)))
            {
                var executing = result.Plans.Where(p => p.WillExecute).ToList();
                for (int i = 0; i < executing.Count; i++)
                {
                    var plan = executing[i];
                    yield return new JobProgress("予測: " + plan.Operation.DisplayName, 0.5f + 0.35f * i / Math.Max(1, executing.Count));
                    if (progress != null && progress("Dry Run: " + plan.Operation.DisplayName, 0.5f)) throw new OperationCanceledException();
                    if (plan.Operation is IStepwiseOperation stepwise) yield return stepwise.ExecuteRoutine(gen, plan);
                    else plan.Operation.Execute(gen, plan);
                        // 後続の処理（結合等）が対象を変える前に、その処理の結果を検証する
                        var v = plan.Operation.Validate(gen, plan);
                        if (!v.Passed) result.ValidationPassed = false;
                        result.ValidationMessages.AddRange(v.Messages.Select(m => plan.Operation.Id + ": " + m));
                }

                result.OutputKinds.AddRange(copy.KindReasons);
                if (session.Settings.visualDiff)
                {
                    yield return new JobProgress("外観を比較しています", 0.88f);
                    result.VisualDiff = VisualDiff.Compare(session.Roots, copy.Roots);
                }
                if (session.Kind != RunKind.Optimize)
                    result.Conversion = MobileConversionValidator.Evaluate(copy.Roots, PlatformOf(session.Kind), session.Settings.preset, result.Plans);

                yield return new JobProgress("予測値を計算しています", 0.93f);
                var ctx = MeasurementContext.Capture("Dry Run 生成物（メモリ上）から計算");
                result.Predicted = MetricsCalculator.Compute(copy.Roots, ctx, gen.Textures);
                foreach (var m in result.Predicted.metrics)
                    if (m.hasValue) m.method = "Dry Run: " + m.method;
                if (session.Settings.preset == ContentPreset.Avatar)
                {
                    yield return new JobProgress("Performance Rank を予測しています", 0.97f);
                    result.Ranks = EvaluateRanks(copy.Roots, session.Analysis.HasBuildTimeTools);
                }
            }
            result.Completed = true;
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
            RunOverwrite overwrite = null;

            try
            {
                Step("入力の再検証", 0.02f);
                if (dryRun.Error != null || !dryRun.CanExecute) throw new OptimizerAbortException("Dry Run が完了していないか、実行可能な処理がありません。");
                CheckConversionEnabled(session);
                var fresh = Analyze(session.Roots, session.Settings, session.Kind);
                if (fresh.InputHash != session.InputHash) throw new OptimizerAbortException("Dry Run 後に対象または依存アセットが変更されました。再度 Dry Run を行ってください。");
                if (fresh.SettingsHash != session.SettingsHash) throw new OptimizerAbortException("Dry Run 後に設定が変更されました。再度 Dry Run を行ってください。");
                if (fresh.Analysis.HasBlockingIssues) throw new OptimizerAbortException("実行不可の問題があります: " + string.Join(" / ", fresh.Analysis.Issues.Where(i => i.Blocking).Select(i => i.Message)));

                var outputFolderError = OptimizationSettings.ValidateOutputFolder(session.Settings.prefabOutputFolder);
                if (outputFolderError != null) throw new OptimizerAbortException(outputFolderError);

                var baseId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + HashUtility.Short(session.InputHash, 6);
                result.RunId = baseId;
                // 同じ対象を同じ秒に再実行した場合は連番を付ける
                for (int n = 2; n < 100 && (AssetDatabase.IsValidFolder(UnityAssetsOptimizerInfo.RunFolder(result.RunId)) ||
                                            Directory.Exists(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, UnityAssetsOptimizerInfo.RunFolder(result.RunId)))); n++)
                    result.RunId = baseId + "-" + n;
                report.runId = result.RunId;
                runFolder = UnityAssetsOptimizerInfo.RunFolder(result.RunId);
                result.RunFolder = runFolder;
                if (AssetDatabase.IsValidFolder(runFolder) || Directory.Exists(Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, runFolder)))
                {
                    runFolder = null; // 既存フォルダは削除対象にしない
                    throw new OptimizerAbortException("出力フォルダが既に存在します（衝突）。時間をおいて再実行してください。");
                }

                // 必要容量の確認（スナップショット + 生成物の余裕）
                long needed = dryRun.SnapshotBytes * 2 + 64L * 1024 * 1024;
                if (FreeSpaceProvider() < needed)
                    throw new OptimizerAbortException("ディスク容量が不足しています（必要目安 " + Metric.FormatNumber(needed, "bytes") + "）。");

                sceneStates = SnapshotService.CaptureSceneStates();

                Step("スナップショット作成", 0.1f);
                AssetPathUtility.EnsureFolder(UnityAssetsOptimizerInfo.GeneratedRoot);
                AssetPathUtility.EnsureFolder(runFolder);
                snapshot = SnapshotService.Create(result.RunId, session.Graph.AssetPaths, session.Roots, Cancel);
                if (!snapshot.Success) throw new OptimizerAbortException("復元用スナップショットを作成できないため開始しません: " + string.Join(" / ", snapshot.Errors));
                var copyErrors = SnapshotService.VerifyCopies(result.RunId, snapshot.Assets);
                if (copyErrors.Count > 0) throw new OptimizerAbortException("スナップショットの検証に失敗したため開始しません: " + string.Join(" / ", copyErrors));

                Step("計画の再作成", 0.2f);
                plans = OptimizationPlanner.Plan(CreateContext(fresh), Operations(session.Kind));
                if (PlanSignature(plans) != PlanSignature(dryRun.Plans)) throw new OptimizerAbortException("計画が Dry Run と一致しません。再度 Dry Run を行ってください。");
                foreach (var plan in plans.Where(p => p.UsesForce))
                    if (approvals == null || !approvals.Any(a => a.operationId == plan.Decision.operationId))
                        throw new OptimizerAbortException(plan.Decision.operationId + " の FORCE が承認されていません。");

                Step("生成（ステージング）", 0.3f);
                var staging = runFolder + "/" + UnityAssetsOptimizerInfo.StagingFolderName;
                AssetPathUtility.EnsureFolder(staging);
                copy = WorkingCopy.Create(session.Roots, session.Settings.outputMode);
                gen = new GenerationContext(true, staging, session.Settings, copy, log, Cancel);
                bool valid = true;
                foreach (var plan in plans.Where(p => p.WillExecute))
                {
                    Step("生成: " + plan.Operation.DisplayName, 0.4f);
                    plan.Operation.Execute(gen, plan);
                    // 後続の処理（結合等）が対象を変える前に、その処理の結果を検証する
                    var v = plan.Operation.Validate(gen, plan);
                    report.validation.AddRange(v.Messages.Select(m => plan.Decision.operationId + ": " + m));
                    if (!v.Passed) valid = false;
                }
                if (!valid) throw new OptimizerAbortException("生成物の検証に失敗しました: " + string.Join(" / ", report.validation));
                Step("テクスチャの確定", 0.6f);
                gen.FinalizeTextures();
                var cleanup = EmptyObjectCleaner.Run(gen, session.Analysis.AnimatedBindingPaths);
                foreach (var note in cleanup.Notes) log.Info(FeatureIds.MeshCombine, note);

                var prefabPaths = new List<string>();
                for (int i = 0; i < copy.Roots.Count; i++)
                {
                    var suffix = session.Kind == RunKind.Optimize ? string.Empty : "_" + session.Kind;
                    var path = AssetDatabase.GenerateUniqueAssetPath(staging + "/" + AssetPathUtility.SanitizeFileName(copy.Roots[i].name + suffix) + ".prefab");
                    gen.CheckWrite(path);
                    PrefabUtility.SaveAsPrefabAsset(copy.Roots[i], path, out bool ok);
                    if (!ok) throw new OptimizerAbortException("Prefab の保存に失敗しました: " + path);
                    prefabPaths.Add(path);
                    var kind = copy.Kinds[i] == CopyKind.PrefabInstance ? "PrefabVariant" : "Prefab";
                    gen.Outputs.Add(new OutputAsset { path = path, guid = AssetDatabase.AssetPathToGUID(path), kind = kind, operationId = "pipeline", sourceRef = session.Selection.Targets[i].globalObjectId });
                    report.outputKinds.Add(copy.Roots[i].name + ": " + copy.KindReasons[i]);
                }

                for (int i = 0; i < prefabPaths.Count; i++)
                {
                    int rootIndex = i;
                    var changes = gen.StructuralChanges.Where(c => c.RootIndex == rootIndex);
                    var removedObjects = cleanup.Removed.Where(x => x.Key == rootIndex).Select(x => x.Value).ToList();
                    var v = StructuralValidator.Compare(session.Roots[i], AssetDatabase.LoadAssetAtPath<GameObject>(prefabPaths[i]), changes, removedObjects);
                    if (!v.Passed) throw new OptimizerAbortException("構造検証に失敗しました: " + string.Join(" / ", v.Messages));
                }
                report.validation.Add("構造検証: OK（階層・コンポーネント構成・Mesh/Material 参照・三角形数）");

                Step("原本の整合性確認", 0.7f);
                var integrity = IntegrityChecker.Check(snapshot.Assets, snapshot.Hierarchy, session.Roots, sceneStates);
                if (!integrity.passed) throw new OptimizerAbortException("原本の整合性検証に失敗しました: " + string.Join(" / ", integrity.errors));

                Step("コミット", 0.8f);
                var generatedFolder = runFolder + "/" + OutputFolderName(session.Kind);
                var moveError = AssetDatabase.MoveAsset(staging, generatedFolder);
                if (!string.IsNullOrEmpty(moveError)) throw new OptimizerAbortException("コミット（移動）に失敗しました: " + moveError);
                foreach (var o in gen.Outputs) o.path = generatedFolder + o.path.Substring(staging.Length);
                result.GeneratedPrefabs = prefabPaths.Select(p => generatedFolder + p.Substring(staging.Length)).ToList();

                // 同じ対象の前回の出力があれば退避する（新しい生成物が同じ名前を使えるように。失敗時は元に戻す）
                if (session.Settings.overwritePreviousOutput)
                {
                    var previous = RunOverwrite.FindPrevious(session.Selection.Targets, session.Kind);
                    if (previous != null)
                    {
                        Step("前回の出力を上書き: " + previous.Manifest.runId, 0.82f);
                        overwrite = new RunOverwrite { Previous = previous };
                        overwrite.Prepare(gen.Outputs.Select(o => o.path));
                    }
                }

                // 出力先が指定されていれば、生成物（Prefab・Material・Mesh・Texture 等）をすべてそこへ移す。
                // サブフォルダ構成（Materials/ 等）は保ち、名前が重なれば別名にする。GUID は維持されるため参照は切れない。
                // 復元用の Manifest・スナップショット・レポートは管理フォルダに残す。
                var outputFolder = (session.Settings.prefabOutputFolder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
                if (outputFolder.Length > 0)
                {
                    AssetPathUtility.EnsureFolderTracked(outputFolder, result.CreatedFolders);
                    foreach (var o in gen.Outputs)
                    {
                        var relative = o.path.Substring(generatedFolder.Length + 1);
                        var dir = Path.GetDirectoryName(relative)?.Replace('\\', '/');
                        var targetDir = string.IsNullOrEmpty(dir) ? outputFolder : outputFolder + "/" + dir;
                        AssetPathUtility.EnsureFolderTracked(targetDir, result.CreatedFolders);
                        var to = AssetDatabase.GenerateUniqueAssetPath(targetDir + "/" + Path.GetFileName(relative));
                        var err = AssetDatabase.MoveAsset(o.path, to);
                        if (!string.IsNullOrEmpty(err)) throw new OptimizerAbortException("生成物を出力先へ移動できません: " + err);
                        for (int i = 0; i < result.GeneratedPrefabs.Count; i++)
                            if (result.GeneratedPrefabs[i] == o.path) result.GeneratedPrefabs[i] = to;
                        o.path = to;
                        result.MovedOutputs.Add(to);
                    }
                    AssetPathUtility.DeleteEmptyFolders(AssetDatabase.GetSubFolders(generatedFolder).Concat(new[] { generatedFolder }).ToList());
                }

                if (overwrite != null)
                {
                    overwrite.ReplacePrefabs(gen.Outputs);
                    result.OverwrittenRunId = overwrite.Previous.Manifest.runId;
                    result.OverwriteSummary = "前回の実行 " + result.OverwrittenRunId + " の出力を上書きしました（Prefab " + overwrite.ReplacedPrefabs +
                                              " 件は GUID を維持、生成物 " + overwrite.RemovedAssets + " 件を削除" +
                                              (overwrite.Kept.Count > 0 ? "、ほかのアセットから参照されている " + overwrite.Kept.Count + " 件は残しました" : string.Empty) + "）";
                    log.Info("pipeline", result.OverwriteSummary);
                }

                Step("事後計測", 0.9f);
                var prefabRoots = result.GeneratedPrefabs.Select(AssetDatabase.LoadAssetAtPath<GameObject>).ToList();
                var afterContext = MeasurementContext.Capture("構造値（コミット済み生成物から計算）");
                result.After = MetricsCalculator.Compute(prefabRoots, afterContext);
                if (session.Settings.preset == ContentPreset.Avatar) result.Ranks = EvaluateRanks(prefabRoots, session.Analysis.HasBuildTimeTools);
                if (session.Settings.visualDiff) result.VisualDiff = VisualDiff.Compare(session.Roots, prefabRoots);
                report.predictedVisualDiff = dryRun.VisualDiff;
                report.visualDiff = result.VisualDiff;
                if (session.Kind != RunKind.Optimize)
                {
                    result.Conversion = MobileConversionValidator.Evaluate(prefabRoots, PlatformOf(session.Kind), session.Settings.preset, plans);
                    report.conversion = result.Conversion;
                }
                report.runKind = session.Kind.ToString();
                report.structuralChanges.AddRange(gen.StructuralChanges.Select(c => (c.Added ? "追加 " : "削除 ") + c.Path + " " + c.ComponentType + "（" + c.OperationId + "）"));
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
                    toolVersion = UnityAssetsOptimizerInfo.ToolVersion,
                    unityVersion = Application.unityVersion,
                    sdkVersion = SdkInfo.Describe(),
                    createdAtUtc = report.createdAtUtc,
                    inputHash = session.InputHash,
                    settingsHash = session.SettingsHash,
                    snapshotHash = snapshot.SnapshotHash,
                    state = RunState.Committed,
                    runKind = session.Kind.ToString(),
                };
                manifest.targets.AddRange(session.Selection.Targets);
                manifest.outputs.AddRange(gen.Outputs);
                manifest.outputPaths.AddRange(gen.Outputs.Select(o => o.path));
                manifest.createdFolders.AddRange(result.CreatedFolders);
                manifest.decisions.AddRange(plans.Select(p => p.Decision));
                manifest.snapshotAssets.AddRange(snapshot.Assets);
                manifest.sceneStates.AddRange(sceneStates);
                manifest.forceApprovals.AddRange(report.forceApprovals);

                var runFull = Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, runFolder);
                File.WriteAllText(Path.Combine(runFull, UnityAssetsOptimizerInfo.ManifestFileName), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
                ReportWriter.WriteFiles(report, Path.Combine(runFull, UnityAssetsOptimizerInfo.ReportsFolderName), "report");
                AssetDatabase.Refresh();
                result.ReportPath = runFolder + "/" + UnityAssetsOptimizerInfo.ReportsFolderName + "/report.md";

                committed = true;
                result.Success = true;
                log.Info("pipeline", "コミット完了: " + runFolder);
                try
                {
                    overwrite?.Finish(result.RunId);
                }
                catch (Exception e)
                {
                    // 新しい出力は確定済み。退避した前回の出力が残るだけなので警告にとどめる
                    Debug.LogWarning("[UnityAssetsOptimizer] 前回の出力の後片付けに失敗しました: " + e.Message);
                }
            }
            catch (OperationCanceledException e)
            {
                result.Cancelled = true;
                result.Error = e.Message;
            }
            catch (OptimizerAbortException e)
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

            if (!committed)
            {
                try
                {
                    overwrite?.Undo();
                }
                catch (Exception e)
                {
                    log.Error("rollback", "前回の出力を元に戻せませんでした（" + overwrite.Previous.RunFolder + "/_Overwritten を確認してください）: " + e.Message);
                }
                Rollback(result, report, plans, snapshot, session, sceneStates, runFolder, log);
            }
            return result;
        }

        /// <summary>
        /// NDMF ビルド時用: ビルド用クローン（原本ではない）に対して AUTO の処理だけをその場で適用する。
        /// 生成物はメモリ上に作り save（NDMF の AssetSaver）へ渡す。FORCE・原本テクスチャのコピー・スナップショットは使わない。
        /// 解析で実行不可の問題、または検証失敗時は例外を投げてビルドを止める（fail-closed）。
        /// </summary>
        public static RunReport ApplyInPlace(GameObject buildClone, OptimizationSettings settings, Action<UnityEngine.Object> save)
        {
            var log = new RunLog();
            var s = settings.Clone();
            foreach (var f in s.features) if (f.mode == ExecutionMode.Force) f.mode = ExecutionMode.Auto;
            // 外部ツールへの設定追加は、ビルド中（各ツールの処理順が決まった後）には行わない
            s.SetMode(FeatureIds.AaoTraceAndOptimize, ExecutionMode.Off);
            s.SetMode(FeatureIds.BuildPolygonReduction, ExecutionMode.Off);
            s.visualDiff = false;

            var session = Analyze(new[] { buildClone }, s);
            var report = new RunReport
            {
                runId = "ndmf-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                sdkVersions = SdkInfo.Describe(),
                preset = s.preset.ToString(),
                createdAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                settings = s,
                contextBefore = session.Context,
                log = log,
                runKind = "NDMF",
                restoreState = "ビルド用クローンに適用（原本・シーンは変更なし）",
            };
            report.targets.AddRange(session.Selection.Targets);
            if (session.Analysis.HasBlockingIssues)
                throw new OptimizerAbortException("UnityAssetsOptimizer: 実行不可の問題があります: " +
                                            string.Join(" / ", session.Analysis.Issues.Where(i => i.Blocking).Select(i => i.Message)));

            var plans = OptimizationPlanner.Plan(CreateContext(session), OperationRegistry.CreateAll());
            using (var copy = WorkingCopy.Wrap(new[] { buildClone }))
            using (var gen = new GenerationContext(false, null, s, copy, log, null) { InMemoryFinal = true, SaveTransient = save })
            {
                foreach (var plan in plans.Where(p => p.WillExecute))
                {
                    plan.Operation.Execute(gen, plan);
                    var v = plan.Operation.Validate(gen, plan);
                    report.validation.AddRange(v.Messages.Select(m => plan.Decision.operationId + ": " + m));
                    if (!v.Passed) throw new OptimizerAbortException("UnityAssetsOptimizer: 検証に失敗しました: " + string.Join(" / ", v.Messages));
                }
                gen.FinalizeTextures();
                EmptyObjectCleaner.Run(gen, session.Analysis.AnimatedBindingPaths);
                var after = MetricsCalculator.Compute(copy.Roots, MeasurementContext.Capture("NDMF ビルド後の構成から計算"));
                report.metrics = RunReport.Compare(session.Baseline, null, after);
                report.structuralChanges.AddRange(gen.StructuralChanges.Select(c => (c.Added ? "追加 " : "削除 ") + c.Path + " " + c.ComponentType));
            }
            FillOperations(report, plans);
            report.result = "Applied (NDMF)";
            ReportWriter.WriteFiles(report, UnityAssetsOptimizerInfo.FailureLogFolderFull, report.runId);
            return report;
        }

        static void Rollback(RunResult result, RunReport report, List<OperationPlan> plans, SnapshotResult snapshot, AnalysisSession session,
            List<SceneState> sceneStates, string runFolder, RunLog log)
        {
            log.Error("pipeline", result.Error ?? "失敗");
            // 出力先へ移動済みの生成物と、新しく作ったフォルダも破棄する
            foreach (var p in result.MovedOutputs)
                if (AssetDatabase.LoadMainAssetAtPath(p) != null) AssetDatabase.DeleteAsset(p);
            AssetPathUtility.DeleteEmptyFolders(result.CreatedFolders);
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
            ReportWriter.WriteFiles(report, UnityAssetsOptimizerInfo.FailureLogFolderFull, id);
            result.ReportPath = Path.Combine(UnityAssetsOptimizerInfo.FailureLogFolderFull, id + ".md");
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
