using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class UnityAssetsOptimizerWindow : EditorWindow
    {
        const string SettingsPrefKey = "ChikumaTateshina.UnityAssetsOptimizer.Settings";
        static readonly string[] Tabs = { "最適化", "Avatar Rank", "Mobile Conversion", "履歴 / 復元" };
        static readonly int[] AtlasSizes = { 512, 1024, 2048, 4096, 8192 };
        static readonly int[] AstcBlocks = { 4, 5, 6, 8 };

        [SerializeField] List<GameObject> _targets = new List<GameObject>();
        [SerializeField] int _tab;
        [SerializeField] int _rankPlatform;
        [SerializeField] bool _showParams;
        [SerializeField] bool _showPlans = true;
        Vector2 _scroll;

        OptimizationSettings _settings;
        AnalysisSession _session;
        DryRunResult _dryRun;
        RunResult _lastRun;
        DryRunResult _mobileDryRun;
        RunResult _mobileRun;
        [SerializeField] int _mobilePlatform;
        List<RunRecord> _runs;
        readonly Dictionary<string, List<RestoreCheckEntry>> _restoreChecks = new Dictionary<string, List<RestoreCheckEntry>>();
        readonly HashSet<string> _expandedPlans = new HashSet<string>();
        IOptimizationOperation[] _operations;
        AnalysisSession _mobileSession;

        // ---- 解析・予測の自動実行（バックグラウンド）
        const double RestartDelaySeconds = 0.6;
        static int _changeCounter;
        BackgroundJob _job;
        RunKind _jobKind;
        string _jobFingerprint;
        string _pendingFingerprint;
        double _pendingSince;
        string _jobError;
        readonly Dictionary<RunKind, string> _completedFingerprint = new Dictionary<RunKind, string>();

        [MenuItem(UnityAssetsOptimizerInfo.MenuRoot + "Open", false, 0)]
        public static void Open()
        {
            var w = GetWindow<UnityAssetsOptimizerWindow>();
            w.titleContent = new GUIContent("UnityAssetsOptimizer");
            w.minSize = new Vector2(520, 400);
            w.Show();
        }

        void OnEnable()
        {
            _operations = OperationRegistry.CreateAll();
            LoadSettings();
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            EditorApplication.projectChanged += OnAnyChange;
            Undo.undoRedoPerformed += OnAnyChange;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            EditorApplication.projectChanged -= OnAnyChange;
            Undo.undoRedoPerformed -= OnAnyChange;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            CancelJob();
        }

        // Undo/SerializedObject を通した変更とアセットの変更を検知する（裏で行う Dry Run 自身はこれらを発生させない）
        static void OnObjectChanges(ref ObjectChangeEventStream stream) => _changeCounter++;
        static void OnAnyChange() => _changeCounter++;

        void OnPlayModeChanged(PlayModeStateChange change) => CancelJob();

        /// <summary>設定はプロジェクトごとに保存する。</summary>
        static string ProjectSettingsKey => SettingsPrefKey + "." + HashUtility.Short(HashUtility.Sha256(Application.dataPath), 8);

        void LoadSettings()
        {
            var json = EditorPrefs.GetString(ProjectSettingsKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try { _settings = JsonUtility.FromJson<OptimizationSettings>(json); }
                catch (Exception) { _settings = null; }
            }
            if (_settings == null || _settings.features.Count == 0) _settings = OptimizationSettings.CreateDefault(ContentPreset.World);
        }

        void SaveSettings() => EditorPrefs.SetString(ProjectSettingsKey, JsonUtility.ToJson(_settings));

        RunKind AutoKind => _tab == 2 ? (_mobilePlatform == 0 ? RunKind.Android : RunKind.iOS) : RunKind.Optimize;

        string Fingerprint(RunKind kind) =>
            kind + "|" + string.Join(",", _targets.Where(t => t != null).Select(t => t.GetInstanceID())) + "|" + _settings.ComputeHash() + "|" + _changeCounter;

        bool IsUpToDate(RunKind kind) => _completedFingerprint.TryGetValue(kind, out var fp) && fp == Fingerprint(kind) && (_job == null || !_job.IsRunning);

        bool CanAutoRun(RunKind kind)
        {
            if (_targets.All(t => t == null) || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return false;
            if (kind == RunKind.Optimize) return true;
            var platform = kind == RunKind.iOS ? VrcPlatform.iOS : VrcPlatform.Android;
            return CapabilityDatabase.IsConversionEnabled(platform, _settings.preset, out _);
        }

        /// <summary>変更が落ち着いたら（RestartDelaySeconds 経過後）解析と予測を裏で実行する。</summary>
        void OnInspectorUpdate()
        {
            if (_settings == null) return;
            var kind = AutoKind;
            var fp = Fingerprint(kind);
            if (_job != null && _job.IsRunning)
            {
                if (_jobFingerprint != fp) CancelJob();
                else
                {
                    Repaint();
                    return;
                }
            }
            if (!CanAutoRun(kind)) return;
            if (_completedFingerprint.TryGetValue(kind, out var done) && done == fp) return;
            if (_pendingFingerprint != fp)
            {
                _pendingFingerprint = fp;
                _pendingSince = EditorApplication.timeSinceStartup;
                Repaint();
                return;
            }
            if (EditorApplication.timeSinceStartup - _pendingSince >= RestartDelaySeconds) StartJob(kind, fp);
        }

        void StartJob(RunKind kind, string fingerprint)
        {
            CancelJob();
            var targets = _targets.Where(t => t != null).ToList();
            var settings = _settings.Clone();
            AnalysisSession session = null;
            var dry = new DryRunResult();

            IEnumerator Routine()
            {
                yield return OptimizerPipeline.AnalyzeRoutine(targets, settings, kind, x => session = x);
                dry.Session = session;
                yield return OptimizerPipeline.DryRunRoutine(session, dry);
            }

            _jobKind = kind;
            _jobFingerprint = fingerprint;
            _jobError = null;
            _job = new BackgroundJob(kind == RunKind.Optimize ? "解析と予測" : kind + " の予測", Routine());
            _job.Finished += job =>
            {
                if (job.IsCancelled) return;
                if (session == null)
                {
                    _jobError = "解析に失敗しました: " + job.Error?.Message;
                    if (job.Error != null) Debug.LogException(job.Error);
                }
                else
                {
                    if (job.Error != null) OptimizerPipeline.SetDryRunError(dry, job.Error);
                    if (kind == RunKind.Optimize)
                    {
                        _session = session;
                        _dryRun = dry;
                    }
                    else
                    {
                        _mobileSession = session;
                        _mobileDryRun = dry;
                    }
                }
                _completedFingerprint[kind] = fingerprint;
                Repaint();
            };
            _job.Start();
        }

        void CancelJob()
        {
            if (_job != null && _job.IsRunning) _job.Cancel();
            _job = null;
        }

        /// <summary>解析・予測の状態表示。処理はエディタの操作を妨げずに裏で進む。</summary>
        void DrawAutoStatus(RunKind kind)
        {
            if (_targets.All(t => t == null)) return;
            if (_job != null && _job.IsRunning && _jobKind == kind)
            {
                var rect = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(rect, _job.Progress, "裏で実行中: " + _job.Stage);
            }
            else if (IsUpToDate(kind))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("解析と予測は最新です（変更すると自動で再計算します）", EditorStyles.miniLabel);
                    if (GUILayout.Button("再計算", EditorStyles.miniButton, GUILayout.Width(60))) _completedFingerprint.Remove(kind);
                }
            }
            else if (CanAutoRun(kind))
            {
                EditorGUILayout.LabelField("変更を検出しました。まもなく解析と予測を実行します…", EditorStyles.miniLabel);
            }
            if (_jobError != null) EditorGUILayout.HelpBox(_jobError, MessageType.Error);
        }

        void OnGUI()
        {
            _tab = GUILayout.Toolbar(_tab, Tabs);
            if (ManualOverride.IsEnabled)
                EditorGUILayout.HelpBox("Manual Override 有効: FORCE を選択できます。原本保全・復元保証・処理境界は解除されません。", MessageType.Warning);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case 0: DrawOptimizeTab(); break;
                case 1: DrawRankTab(); break;
                case 2: DrawMobileTab(); break;
                default: DrawHistoryTab(); break;
            }
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ 最適化タブ

        void DrawOptimizeTab()
        {
            DrawTargets();
            EditorGUILayout.Space();
            DrawPresetAndFeatures();
            EditorGUILayout.Space();
            DrawActions();
            EditorGUILayout.Space();
            DrawAnalysisResults();
        }

        void DrawTargets()
        {
            EditorGUILayout.LabelField("1. 処理対象", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("現在の選択を取り込む"))
                {
                    _targets = Selection.gameObjects.ToList();
                    Invalidate();
                }
                if (GUILayout.Button("クリア", GUILayout.Width(60)))
                {
                    _targets.Clear();
                    Invalidate();
                }
            }
            if (_targets.Count == 0)
            {
                EditorGUILayout.HelpBox("Hierarchy または Project で GameObject / Prefab を選択し「現在の選択を取り込む」を押してください。", MessageType.Info);
                return;
            }
            using (new EditorGUI.DisabledScope(true))
                foreach (var t in _targets) EditorGUILayout.ObjectField(t, typeof(GameObject), true);

            if (_session != null && _session.Selection.RemovedDescendants.Count > 0)
                EditorGUILayout.HelpBox("祖先が選択済みのため除外: " + string.Join(", ", _session.Selection.RemovedDescendants.Select(g => g.name)), MessageType.None);
        }

        void DrawPresetAndFeatures()
        {
            EditorGUILayout.LabelField("2. プリセットと機能", EditorStyles.boldLabel);
            var preset = (ContentPreset)EditorGUILayout.EnumPopup("プリセット", _settings.preset);
            if (preset != _settings.preset)
            {
                bool modified = _settings.features.Any(f => f.userModified);
                if (!modified || EditorUtility.DisplayDialog("プリセット変更", "変更済みの機能設定はプリセット初期値に戻ります。", "変更", "キャンセル"))
                {
                    _settings.ApplyPreset(preset);
                    SettingsChanged();
                }
            }

            foreach (var def in FeatureCatalog.All)
            {
                var op = _operations.FirstOrDefault(o => o.Id == def.Id);
                bool implemented = op != null && op.Capabilities.Implemented;
                if (!implemented) continue; // 未実装の機能は表示しない
                bool applicable = _settings.IsApplicable(def.Id);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var label = new GUIContent(def.Label + (_settings.IsUserModified(def.Id) ? " *" : string.Empty), def.Note);
                    EditorGUILayout.LabelField(label, GUILayout.Width(190));
                    if (!applicable)
                    {
                        EditorGUILayout.LabelField("対象外", EditorStyles.miniLabel);
                    }
                    else
                    {
                        var mode = _settings.GetMode(def.Id);
                        ExecutionMode newMode;
                        if (ManualOverride.IsEnabled && op.Capabilities.CanForce)
                            newMode = (ExecutionMode)EditorGUILayout.EnumPopup(mode, GUILayout.Width(80));
                        else
                        {
                            bool on = EditorGUILayout.Toggle(mode != ExecutionMode.Off, GUILayout.Width(20));
                            newMode = on ? (mode == ExecutionMode.Force ? ExecutionMode.Force : ExecutionMode.Auto) : ExecutionMode.Off;
                        }
                        if (newMode != mode)
                        {
                            _settings.SetMode(def.Id, newMode);
                            SettingsChanged();
                        }
                        EditorGUILayout.LabelField(def.Note, EditorStyles.miniLabel);
                    }
                }
            }

            _showParams = EditorGUILayout.Foldout(_showParams, "詳細パラメータ", true);
            if (!_showParams) return;
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            _settings.atlasMaxSize = EditorGUILayout.IntPopup("アトラス最大解像度", _settings.atlasMaxSize, AtlasSizes.Select(s => s.ToString()).ToArray(), AtlasSizes);
            _settings.atlasPadding = EditorGUILayout.IntSlider("アトラスパディング(px)", _settings.atlasPadding, 0, 32);
            _settings.compressionQuality = EditorGUILayout.IntSlider("圧縮品質", _settings.compressionQuality, 0, 100);
            _settings.standaloneCrunch = EditorGUILayout.Toggle("PC: Crunch 圧縮", _settings.standaloneCrunch);
            _settings.mobileAstcBlock = EditorGUILayout.IntPopup("Android/iOS: ASTC ブロック", _settings.mobileAstcBlock, AstcBlocks.Select(b => b + "x" + b).ToArray(), AstcBlocks);
            _settings.downscaleMaxSize = EditorGUILayout.IntPopup("自動解像度削減: 最大", _settings.downscaleMaxSize, AtlasSizes.Select(s => s.ToString()).ToArray(), AtlasSizes);
            _settings.meshCombineSkinned = EditorGUILayout.Toggle(new GUIContent("メッシュ結合: SkinnedMesh も対象", "要件上の初期値は OFF"), _settings.meshCombineSkinned);
            _settings.meshCombineCellSize = EditorGUILayout.FloatField(new GUIContent("メッシュ結合: 空間セル(m)", "セルを跨いで結合しない（カリング単位の保護）"), _settings.meshCombineCellSize);
            _settings.polygonReductionRatio = EditorGUILayout.Slider("ポリゴン削減: 目標比率", _settings.polygonReductionRatio, 0.05f, 0.95f);
            _settings.decimationMaxError = EditorGUILayout.Slider(new GUIContent("簡略化: 許容誤差", "境界ボックス対角長に対する比率。超える縮約は行わない"), _settings.decimationMaxError, 0.001f, 0.05f);
            _settings.outputMode = (PrefabOutputMode)EditorGUILayout.EnumPopup(new GUIContent("出力形式", "VariantWhenPossible: 元 Prefab の Variant として出力（構造変更のないインスタンス/Prefab Asset のみ）"), _settings.outputMode);
            _settings.visualDiff = EditorGUILayout.Toggle(new GUIContent("外観の参考比較", "隔離プレビューで前後をレンダリングし画像差分を記録（参考値）"), _settings.visualDiff);
            if (EditorGUI.EndChangeCheck()) SettingsChanged();
            EditorGUI.indentLevel--;
        }

        void SettingsChanged()
        {
            // 前回の結果は再計算が終わるまで表示したままにする（実行ボタンは最新になるまで無効）
            SaveSettings();
        }

        void Invalidate()
        {
            _session = null;
            _dryRun = null;
        }

        void DrawActions()
        {
            EditorGUILayout.LabelField("3. 出力と実行", EditorStyles.boldLabel);
            DrawOutputFolder();
            DrawAutoStatus(RunKind.Optimize);
            bool ready = _dryRun != null && IsUpToDate(RunKind.Optimize) && _dryRun.CanExecute;
            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button("最適化を実行", GUILayout.Height(30))) RunExecute();
            }
        }

        void DrawOutputFolder()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var folder = EditorGUILayout.TextField(new GUIContent("Prefab の出力先", "空欄なら " + UnityAssetsOptimizerInfo.GeneratedRoot + "/<run-id>/ 内。Mesh・Material・Texture 等の依存アセットとスナップショットは常に " + UnityAssetsOptimizerInfo.GeneratedRoot + " に置きます"), _settings.prefabOutputFolder);
                if (GUILayout.Button("選択…", GUILayout.Width(56)))
                {
                    var picked = EditorUtility.OpenFolderPanel("Prefab の出力先", Application.dataPath, string.Empty);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        var root = UnityAssetsOptimizerInfo.ProjectRoot.Replace('\\', '/');
                        picked = picked.Replace('\\', '/');
                        folder = picked.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? picked.Substring(root.Length + 1) : picked;
                        GUI.changed = true;
                    }
                }
                if (GUILayout.Button("既定", GUILayout.Width(44)))
                {
                    folder = string.Empty;
                    GUI.changed = true;
                }
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.prefabOutputFolder = folder;
                    SaveSettings();
                }
            }
            var error = OptimizationSettings.ValidateOutputFolder(_settings.prefabOutputFolder);
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
        }

        static string OutputDescription(OptimizationSettings settings) =>
            string.IsNullOrEmpty(settings.prefabOutputFolder)
                ? UnityAssetsOptimizerInfo.GeneratedRoot + "/<run-id>/"
                : settings.prefabOutputFolder + "/（依存アセットは " + UnityAssetsOptimizerInfo.GeneratedRoot + "/<run-id>/）";

        void RunExecute()
        {
            var executing = _dryRun.Plans.Where(p => p.WillExecute).ToList();
            var message =
                "Prefab の出力先: " + OutputDescription(_settings) + "\n" +
                "原本スナップショット: " + Metric.FormatNumber(_dryRun.SnapshotBytes, "bytes") + "（" + _dryRun.Session.Graph.AssetPaths.Count + " ファイル）\n" +
                "実行する処理: " + string.Join(", ", executing.Select(p => p.Operation.DisplayName + "(" + p.Decision.mode.Label() + ")")) + "\n" +
                "スキップ項目: " + _dryRun.Plans.Sum(p => p.Items.Count(i => !i.included)) + " 件\n\n" +
                "原本（シーン・Prefab・Mesh・Material・Texture）は変更しません。生成物は新しい Prefab として出力されます。";
            if (!EditorUtility.DisplayDialog("最適化の実行確認", message, "実行", "キャンセル")) return;

            _lastRun = ExecuteWithApprovals(_dryRun);
            _runs = null;
            if (_lastRun == null) return;

            if (_lastRun.Success)
            {
                var first = _lastRun.GeneratedPrefabs.Select(AssetDatabase.LoadAssetAtPath<GameObject>).FirstOrDefault();
                if (first != null) EditorGUIUtility.PingObject(first);
            }
            _dryRun = null;
        }

        /// <summary>FORCE の個別承認を取得して実行する。承認が拒否された場合は null。</summary>
        RunResult ExecuteWithApprovals(DryRunResult dryRun)
        {
            if (OptimizationSettings.ValidateOutputFolder(_settings.prefabOutputFolder) != null) return null;
            CancelJob();
            dryRun.Session.Settings.prefabOutputFolder = _settings.prefabOutputFolder;
            var approvals = new List<ForceApproval>();
            foreach (var plan in dryRun.ForcePlans)
            {
                var risks = plan.ForceRisks.Take(15).ToList();
                var more = plan.ForceRisks.Count > risks.Count ? "\n…他 " + (plan.ForceRisks.Count - risks.Count) + " 件" : string.Empty;
                var ok = EditorUtility.DisplayDialog("FORCE 承認: " + plan.Operation.DisplayName,
                    "以下の項目を互換性/外観のリスクを受容して処理します。\n予測値の信頼性は低下します。\n\n" + string.Join("\n", risks) + more,
                    "リスクを受容して承認", "中止");
                if (!ok) return null;
                var approval = new ForceApproval
                {
                    operationId = plan.Decision.operationId,
                    approvedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    user = Environment.UserName,
                };
                approval.risks.AddRange(plan.ForceRisks);
                approval.items.AddRange(plan.Items.Where(i => i.included && i.support != SupportState.Supported).Select(i => i.target));
                approvals.Add(approval);
            }

            RunResult result;
            try
            {
                result = OptimizerPipeline.Execute(dryRun, approvals, (m, p) => EditorUtility.DisplayCancelableProgressBar("UnityAssetsOptimizer", m, p));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (!result.Success)
                EditorUtility.DisplayDialog(result.Cancelled ? "キャンセルしました" : "処理に失敗しました",
                    (result.Error ?? string.Empty) + "\n\n生成途中のデータは破棄し、原本は変更していません。\nレポート: " + result.ReportPath, "OK");
            return result;
        }

        void DrawAnalysisResults()
        {
            if (_session == null || _targets.All(t => t == null)) return;
            EditorGUILayout.LabelField("4. 解析と予測の結果", EditorStyles.boldLabel);
            if (!IsUpToDate(RunKind.Optimize)) EditorGUILayout.LabelField("（前回の結果を表示しています。再計算が終わると更新されます）", EditorStyles.miniLabel);
            foreach (var issue in _session.Analysis.Issues)
                EditorGUILayout.HelpBox(issue.Source + ": " + issue.Message, issue.Blocking ? MessageType.Error : MessageType.Warning);

            MetricsTable.Draw(_session.Baseline, _dryRun?.Predicted, _lastRun != null && _lastRun.Success ? _lastRun.After : null, _lastRun?.Report);

            if (_dryRun != null)
            {
                if (_dryRun.Error != null) EditorGUILayout.HelpBox(_dryRun.Error, MessageType.Error);
                if (!_dryRun.ValidationPassed)
                    EditorGUILayout.HelpBox("Dry Run の検証に失敗しました:\n" + string.Join("\n", _dryRun.ValidationMessages), MessageType.Error);
                EditorGUILayout.LabelField("必要なスナップショット容量: " + Metric.FormatNumber(_dryRun.SnapshotBytes, "bytes"), EditorStyles.miniLabel);
                foreach (var k in _dryRun.OutputKinds) EditorGUILayout.LabelField("出力形式: " + k, EditorStyles.miniLabel);
                if (_dryRun.VisualDiff != null)
                    EditorGUILayout.LabelField("外観の参考比較（予測）: " + ReportWriter.DescribeDiff(_dryRun.VisualDiff), EditorStyles.wordWrappedMiniLabel);
                DrawPlans(_dryRun.Plans);
            }
            DrawLastRun();
        }

        void DrawPlans(List<OperationPlan> plans)
        {
            _showPlans = EditorGUILayout.Foldout(_showPlans, "処理ごとの判定（根拠・スキップ理由）", true);
            if (!_showPlans) return;
            foreach (var plan in plans)
            {
                var d = plan.Decision;
                if (d.mode == ExecutionMode.Off && !plan.Items.Any()) continue;
                var title = (d.willExecute ? "✔ " : "－ ") + plan.Operation.DisplayName + "  [" + d.mode.Label() + " / " + d.support.Label() + "]";
                bool expanded = _expandedPlans.Contains(d.operationId);
                bool now = EditorGUILayout.Foldout(expanded, title, true);
                if (now != expanded)
                {
                    if (now) _expandedPlans.Add(d.operationId);
                    else _expandedPlans.Remove(d.operationId);
                }
                if (!now) continue;
                EditorGUI.indentLevel++;
                foreach (var r in d.reasons) EditorGUILayout.LabelField(r, EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(plan.ExpectedEffect)) EditorGUILayout.LabelField("期待効果: " + plan.ExpectedEffect, EditorStyles.wordWrappedMiniLabel);
                foreach (var n in plan.Notes) EditorGUILayout.LabelField("注記: " + n, EditorStyles.wordWrappedMiniLabel);
                foreach (var risk in plan.ForceRisks) EditorGUILayout.LabelField("FORCE リスク: " + risk, EditorStyles.wordWrappedMiniLabel);
                foreach (var item in plan.Items.Take(200))
                {
                    var mark = item.included ? "●" : "○";
                    EditorGUILayout.LabelField(mark + " [" + item.support.Label() + "] " + item.target + (string.IsNullOrEmpty(item.reason) ? string.Empty : " — " + item.reason), EditorStyles.wordWrappedMiniLabel);
                }
                if (plan.Items.Count > 200) EditorGUILayout.LabelField("…他 " + (plan.Items.Count - 200) + " 件（レポート参照）", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
        }

        void DrawLastRun()
        {
            if (_lastRun == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("5. 実行結果", EditorStyles.boldLabel);
            if (!_lastRun.Success)
            {
                EditorGUILayout.HelpBox((_lastRun.Cancelled ? "キャンセル（ロールバック済み）: " : "失敗（ロールバック済み）: ") + _lastRun.Error, MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox("コミット完了: " + _lastRun.RunFolder, MessageType.Info);
                foreach (var p in _lastRun.GeneratedPrefabs)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(p, EditorStyles.miniLabel);
                        if (GUILayout.Button("Ping", GUILayout.Width(50))) EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(p));
                    }
                if (_lastRun.VisualDiff != null)
                    EditorGUILayout.LabelField("外観の参考比較（処理後）: " + ReportWriter.DescribeDiff(_lastRun.VisualDiff), EditorStyles.wordWrappedMiniLabel);
                if (_lastRun.Report?.integrity != null)
                    EditorGUILayout.LabelField("原本整合性: " + (_lastRun.Report.integrity.passed ? "OK" : "NG") + "（" + _lastRun.Report.integrity.checkedAssets + " 件）", EditorStyles.miniLabel);
                foreach (var w in _lastRun.Report?.contextWarnings ?? new List<string>())
                    EditorGUILayout.HelpBox("測定条件の差異: " + w, MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(_lastRun.ReportPath) && GUILayout.Button("レポートを開く"))
                EditorUtility.OpenWithDefaultApp(System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, _lastRun.ReportPath)));
        }

        // ------------------------------------------------------------------ Avatar Rank タブ

        void DrawRankTab()
        {
            if (_settings.preset != ContentPreset.Avatar)
            {
                EditorGUILayout.HelpBox("Avatar プリセットで解析すると表示されます。", MessageType.Info);
                return;
            }
            if (_session == null || _session.Ranks.Count == 0)
            {
                EditorGUILayout.HelpBox("「最適化」タブで対象を取り込むと、自動で解析されます。", MessageType.Info);
                return;
            }
            _rankPlatform = GUILayout.Toolbar(_rankPlatform, new[] { "PC", "Android", "iOS" });
            var before = _session.Ranks.ElementAtOrDefault(_rankPlatform);
            var predicted = _dryRun?.Ranks.ElementAtOrDefault(_rankPlatform);
            var after = _lastRun != null && _lastRun.Success ? _lastRun.Ranks.ElementAtOrDefault(_rankPlatform) : null;

            void Summary(string label, RankResult r)
            {
                if (r == null)
                {
                    EditorGUILayout.LabelField(label + ": 未評価");
                    return;
                }
                EditorGUILayout.LabelField(label + ": " + r.rank + "  [" + r.status.Label() + "]" + (string.IsNullOrEmpty(r.ruleSetId) ? string.Empty : "  DB: " + r.ruleSetId));
            }
            Summary("処理前", before);
            Summary("予測", predicted);
            Summary("処理後", after);
            foreach (var reason in before?.reasons ?? new List<string>())
                EditorGUILayout.HelpBox(reason, MessageType.Info);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("項目", GUILayout.Width(150));
                GUILayout.Label("処理前", GUILayout.Width(70));
                GUILayout.Label("予測", GUILayout.Width(70));
                GUILayout.Label("処理後", GUILayout.Width(70));
                GUILayout.Label("閾値", GUILayout.Width(120));
                GUILayout.Label("超過", GUILayout.Width(60));
                GUILayout.Label("ランク");
            }
            if (before == null) return;
            foreach (var row in before.rows)
            {
                var p = predicted?.rows.FirstOrDefault(r => r.key == row.key);
                var a = after?.rows.FirstOrDefault(r => r.key == row.key);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(row.label, GUILayout.Width(150));
                    GUILayout.Label(row.value.ToString("N0", CultureInfo.InvariantCulture), GUILayout.Width(70));
                    GUILayout.Label(p != null ? p.value.ToString("N0", CultureInfo.InvariantCulture) : "—", GUILayout.Width(70));
                    GUILayout.Label(a != null ? a.value.ToString("N0", CultureInfo.InvariantCulture) : "—", GUILayout.Width(70));
                    GUILayout.Label(row.threshold, GUILayout.Width(120));
                    GUILayout.Label(row.excess, GUILayout.Width(60));
                    GUILayout.Label(row.rank);
                }
            }
        }

        // ------------------------------------------------------------------ Mobile タブ

        void DrawMobileTab()
        {
            EditorGUILayout.HelpBox("PC 用の選択アセットから Android/iOS 用の派生 Prefab と依存アセットを別に生成します（PC 版は変更しません）。" +
                                    "アバターは VRChat SDK のシェーダー検証 API で判定し、外観が変わる変換は FORCE（Manual Override）でのみ実行します。自動アップロードは行いません。", MessageType.Info);
            _mobilePlatform = GUILayout.Toolbar(_mobilePlatform, new[] { "Android", "iOS" });
            var platform = _mobilePlatform == 0 ? VrcPlatform.Android : VrcPlatform.iOS;
            var kind = _mobilePlatform == 0 ? RunKind.Android : RunKind.iOS;
            bool enabled = CapabilityDatabase.IsConversionEnabled(platform, _settings.preset, out var reason);
            EditorGUILayout.HelpBox((enabled ? "有効: " : "無効: ") + reason, enabled ? MessageType.None : MessageType.Warning);

            if (ManualOverride.IsEnabled)
            {
                var mode = (ExecutionMode)EditorGUILayout.EnumPopup("シェーダー変換モード", _settings.mobileShaderMode);
                if (mode != _settings.mobileShaderMode)
                {
                    _settings.mobileShaderMode = mode;
                    SettingsChanged();
                }
            }

            if (!enabled) return;
            DrawOutputFolder();
            DrawAutoStatus(kind);
            bool ready = _mobileDryRun != null && _mobileDryRun.Session.Kind == kind && IsUpToDate(kind) && _mobileDryRun.CanExecute;
            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button(platform + " 派生版を生成", GUILayout.Height(30))) _mobileRun = ExecuteWithApprovals(_mobileDryRun);
            }

            if (_mobileDryRun != null && _mobileDryRun.Session.Kind == kind)
            {
                if (_mobileDryRun.Error != null) EditorGUILayout.HelpBox(_mobileDryRun.Error, MessageType.Error);
                if (_mobileDryRun.Conversion != null)
                {
                    EditorGUILayout.LabelField("予測: " + _mobileDryRun.Conversion.StateLabel + "（実機検証: 未実施）", EditorStyles.boldLabel);
                    foreach (var r in _mobileDryRun.Conversion.reasons) EditorGUILayout.LabelField(r, EditorStyles.wordWrappedMiniLabel);
                }
                DrawPlans(_mobileDryRun.Plans);
            }
            if (_mobileRun != null)
            {
                if (!_mobileRun.Success) EditorGUILayout.HelpBox("失敗（ロールバック済み）: " + _mobileRun.Error, MessageType.Error);
                else
                {
                    EditorGUILayout.HelpBox("生成完了: " + _mobileRun.RunFolder + "\n状態: " + (_mobileRun.Conversion?.StateLabel ?? "—") + " / 実機検証: 未実施", MessageType.Info);
                    foreach (var p in _mobileRun.GeneratedPrefabs)
                        if (GUILayout.Button("Ping: " + p)) EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(p));
                }
            }
        }

        // ------------------------------------------------------------------ 履歴タブ

        void DrawHistoryTab()
        {
            if (GUILayout.Button("更新") || _runs == null) _runs = RestoreService.FindRuns();
            if (_runs.Count == 0)
            {
                EditorGUILayout.HelpBox("生成履歴はありません（" + UnityAssetsOptimizerInfo.GeneratedRoot + "）。", MessageType.Info);
                return;
            }
            foreach (var run in _runs.ToList())
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(run.RunFolder, EditorStyles.boldLabel);
                    if (run.Error != null)
                    {
                        EditorGUILayout.HelpBox(run.Error + "（自動復元は行いません）", MessageType.Error);
                        continue;
                    }
                    var m = run.Manifest;
                    EditorGUILayout.LabelField("作成: " + m.createdAtUtc + " / 対象: " + string.Join(", ", m.targets.Select(t => t.displayPath)), EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.LabelField("生成物 " + m.outputs.Count + " 件 / スナップショット " + m.snapshotAssets.Count + " 件 / FORCE 承認 " + m.forceApprovals.Count + " 件", EditorStyles.miniLabel);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("生成物を表示"))
                            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(run.RunFolder));
                        if (GUILayout.Button("レポート"))
                            EditorUtility.OpenWithDefaultApp(System.IO.Path.Combine(UnityAssetsOptimizerInfo.ProjectRoot, run.RunFolder, UnityAssetsOptimizerInfo.ReportsFolderName, "report.md"));
                        if (GUILayout.Button("原本を検証")) _restoreChecks[run.RunFolder] = RestoreService.CheckOriginals(m);
                        if (GUILayout.Button("生成物を破棄")) Discard(run);
                    }

                    if (_restoreChecks.TryGetValue(run.RunFolder, out var checks))
                    {
                        int changed = checks.Count(c => c.State != OriginalState.Unchanged);
                        EditorGUILayout.LabelField(changed == 0 ? "原本はすべて処理前と同一です。" : "変更/欠落: " + changed + " 件", EditorStyles.miniBoldLabel);
                        foreach (var c in checks.Where(c => c.State != OriginalState.Unchanged))
                            EditorGUILayout.LabelField("  " + c.State + ": " + c.Asset.path + (c.Detail != null ? "（" + c.Detail + "）" : string.Empty), EditorStyles.wordWrappedMiniLabel);
                        bool restorable = checks.Any(c => c.State == OriginalState.ModifiedRestorable || c.State == OriginalState.Missing);
                        using (new EditorGUI.DisabledScope(!restorable))
                        {
                            if (GUILayout.Button("スナップショットから原本を書き戻す") &&
                                EditorUtility.DisplayDialog("原本の復元", "変更/欠落した原本ファイルをスナップショットで上書きします（.meta も戻し GUID を維持します）。", "復元", "キャンセル"))
                            {
                                var messages = RestoreService.RestoreOriginals(m, checks);
                                EditorUtility.DisplayDialog("復元結果", string.Join("\n", messages), "OK");
                                _restoreChecks[run.RunFolder] = RestoreService.CheckOriginals(m);
                            }
                        }
                    }
                }
            }
        }

        void Discard(RunRecord run)
        {
            var refs = RestoreService.FindSceneReferences(run.RunFolder, run.Manifest);
            var checks = RestoreService.CheckOriginals(run.Manifest);
            var changed = checks.Where(c => c.State != OriginalState.Unchanged).ToList();
            var msg = "生成物とこの実行のスナップショットを削除します。原本には触れません。";
            if (refs.Count > 0) msg += "\n\n⚠ 読み込み中のシーンが生成物を参照しています（削除すると Missing になります）:\n" + string.Join("\n", refs.Take(10));
            if (changed.Count > 0) msg += "\n\n⚠ 原本が処理後に変更されています。削除するとスナップショットからの復元ができなくなります（" + changed.Count + " 件）。";
            if (!EditorUtility.DisplayDialog("生成物の破棄", msg, "破棄", "キャンセル")) return;
            if (!RestoreService.DiscardRun(run.RunFolder, out var error, run.Manifest)) EditorUtility.DisplayDialog("破棄に失敗", error, "OK");
            _restoreChecks.Remove(run.RunFolder);
            _runs = null;
        }
    }

    static class MetricsTable
    {
        public static void Draw(MetricSet before, MetricSet predicted, MetricSet after, RunReport report)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("指標", GUILayout.Width(150));
                GUILayout.Label("処理前", GUILayout.Width(120));
                GUILayout.Label("予測", GUILayout.Width(120));
                GUILayout.Label("処理後", GUILayout.Width(120));
                GUILayout.Label("予測誤差");
            }
            foreach (var key in MetricKeys.Ordered)
            {
                // 算出していない指標（フレーム時間）は表示しない
                if (key == MetricKeys.CpuTime || key == MetricKeys.GpuTime) continue;
                var b = before?.Get(key);
                var p = predicted?.Get(key);
                var a = after?.Get(key);
                var label = (b ?? p ?? a)?.label ?? key;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(label, GUILayout.Width(150));
                    GUILayout.Label(Cell(b), GUILayout.Width(120));
                    GUILayout.Label(Cell(p), GUILayout.Width(120));
                    GUILayout.Label(Cell(a), GUILayout.Width(120));
                    var cmp = report?.metrics.FirstOrDefault(m => m.key == key);
                    GUILayout.Label(cmp != null && cmp.hasPredictionError ? (cmp.predictionError * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "—");
                }
            }
            EditorGUILayout.LabelField("区分: 確定計算 / 推定 / 取得不可。マウスオーバーで算出方法・算出不可の理由を表示します。", EditorStyles.wordWrappedMiniLabel);
        }

        static GUIContent Cell(Metric m)
        {
            if (m == null || m.IsNotMeasured) return new GUIContent("—");
            var tooltip = m.hasValue ? m.method : m.unavailableReason;
            return new GUIContent(m.FormatValue() + " (" + m.kind.Label() + ")", tooltip);
        }
    }
}
