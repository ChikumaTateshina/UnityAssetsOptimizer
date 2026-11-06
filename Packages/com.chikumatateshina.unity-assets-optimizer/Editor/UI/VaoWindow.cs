using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    public sealed class VaoWindow : EditorWindow
    {
        const string SettingsPrefKey = "ChikumaTateshina.VAO.Settings";
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
        List<RunRecord> _runs;
        readonly Dictionary<string, List<RestoreCheckEntry>> _restoreChecks = new Dictionary<string, List<RestoreCheckEntry>>();
        readonly HashSet<string> _expandedPlans = new HashSet<string>();
        IOptimizationOperation[] _operations;

        [MenuItem(VaoInfo.MenuRoot + "Open", false, 0)]
        public static void Open()
        {
            var w = GetWindow<VaoWindow>();
            w.titleContent = new GUIContent("Asset Optimizer");
            w.minSize = new Vector2(520, 400);
            w.Show();
        }

        void OnEnable()
        {
            _operations = OperationRegistry.CreateAll();
            LoadSettings();
        }

        void LoadSettings()
        {
            var json = EditorPrefs.GetString(SettingsPrefKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try { _settings = JsonUtility.FromJson<OptimizationSettings>(json); }
                catch (Exception) { _settings = null; }
            }
            if (_settings == null || _settings.features.Count == 0) _settings = OptimizationSettings.CreateDefault(ContentPreset.World);
        }

        void SaveSettings() => EditorPrefs.SetString(SettingsPrefKey, JsonUtility.ToJson(_settings));

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
                bool applicable = _settings.IsApplicable(def.Id);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var label = new GUIContent(def.Label + (_settings.IsUserModified(def.Id) ? " *" : string.Empty), def.Note + "（Phase " + def.Phase + "）");
                    EditorGUILayout.LabelField(label, GUILayout.Width(190));
                    if (!applicable)
                    {
                        EditorGUILayout.LabelField("対象外", EditorStyles.miniLabel);
                    }
                    else if (!implemented)
                    {
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.LabelField((_settings.GetMode(def.Id) != ExecutionMode.Off ? "ON" : "OFF") + "（未実装 / Phase " + def.Phase + "・実行されません）", EditorStyles.miniLabel);
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
            if (EditorGUI.EndChangeCheck()) SettingsChanged();
            EditorGUI.indentLevel--;
        }

        void SettingsChanged()
        {
            SaveSettings();
            _dryRun = null;
        }

        void Invalidate()
        {
            _session = null;
            _dryRun = null;
        }

        void DrawActions()
        {
            EditorGUILayout.LabelField("3. 解析 → Dry Run → 実行", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_targets.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("解析（現在値）", GUILayout.Height(28))) RunAnalyze();
                }
                using (new EditorGUI.DisabledScope(_session == null))
                {
                    if (GUILayout.Button("Dry Run（予測値）", GUILayout.Height(28))) RunDryRun();
                }
                bool stale = _dryRun != null && _dryRun.Session.SettingsHash != _settings.ComputeHash();
                using (new EditorGUI.DisabledScope(_dryRun == null || stale || !_dryRun.CanExecute))
                {
                    if (GUILayout.Button("最適化を実行", GUILayout.Height(28))) RunExecute();
                }
            }
            if (_dryRun != null && _dryRun.Session.SettingsHash != _settings.ComputeHash())
                EditorGUILayout.HelpBox("Dry Run 後に設定が変更されました。Dry Run を再実行してください。", MessageType.Warning);
        }

        void RunAnalyze()
        {
            try
            {
                EditorUtility.DisplayProgressBar("VRChat Asset Optimizer", "解析中...", 0.3f);
                _session = OptimizerPipeline.Analyze(_targets.Where(t => t != null), _settings);
                _dryRun = null;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("解析エラー", e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void RunDryRun()
        {
            try
            {
                // 現在の設定で解析し直してから Dry Run する（設定ハッシュを一致させる）
                _session = OptimizerPipeline.Analyze(_targets.Where(t => t != null), _settings);
                _dryRun = OptimizerPipeline.DryRun(_session, (m, p) => EditorUtility.DisplayCancelableProgressBar("VRChat Asset Optimizer", m, p));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Dry Run エラー", e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void RunExecute()
        {
            var executing = _dryRun.Plans.Where(p => p.WillExecute).ToList();
            var message =
                "生成先: " + VaoInfo.GeneratedRoot + "/<run-id>/\n" +
                "原本スナップショット: " + Metric.FormatNumber(_dryRun.SnapshotBytes, "bytes") + "（" + _dryRun.Session.Graph.AssetPaths.Count + " ファイル）\n" +
                "実行する処理: " + string.Join(", ", executing.Select(p => p.Operation.DisplayName + "(" + p.Decision.mode.Label() + ")")) + "\n" +
                "スキップ項目: " + _dryRun.Plans.Sum(p => p.Items.Count(i => !i.included)) + " 件\n\n" +
                "原本（シーン・Prefab・Mesh・Material・Texture）は変更しません。生成物は新しい Prefab として出力されます。";
            if (!EditorUtility.DisplayDialog("最適化の実行確認", message, "実行", "キャンセル")) return;

            var approvals = new List<ForceApproval>();
            foreach (var plan in _dryRun.ForcePlans)
            {
                var risks = plan.ForceRisks.Take(15).ToList();
                var more = plan.ForceRisks.Count > risks.Count ? "\n…他 " + (plan.ForceRisks.Count - risks.Count) + " 件" : string.Empty;
                var ok = EditorUtility.DisplayDialog("FORCE 承認: " + plan.Operation.DisplayName,
                    "以下の項目を互換性/外観のリスクを受容して処理します。\n予測値の信頼性は低下します。\n\n" + string.Join("\n", risks) + more,
                    "リスクを受容して承認", "中止");
                if (!ok) return;
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

            try
            {
                _lastRun = OptimizerPipeline.Execute(_dryRun, approvals, (m, p) => EditorUtility.DisplayCancelableProgressBar("VRChat Asset Optimizer", m, p));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            _runs = null;

            if (_lastRun.Success)
            {
                var first = _lastRun.GeneratedPrefabs.Select(AssetDatabase.LoadAssetAtPath<GameObject>).FirstOrDefault();
                if (first != null) EditorGUIUtility.PingObject(first);
            }
            else
            {
                EditorUtility.DisplayDialog(_lastRun.Cancelled ? "キャンセルしました" : "最適化に失敗しました",
                    (_lastRun.Error ?? string.Empty) + "\n\n生成途中のデータは破棄し、原本は変更していません。\nレポート: " + _lastRun.ReportPath, "OK");
            }
            _dryRun = null;
        }

        void DrawAnalysisResults()
        {
            if (_session == null) return;
            EditorGUILayout.LabelField("4. 解析結果", EditorStyles.boldLabel);
            foreach (var issue in _session.Analysis.Issues)
                EditorGUILayout.HelpBox(issue.Source + ": " + issue.Message, issue.Blocking ? MessageType.Error : MessageType.Warning);

            MetricsTable.Draw(_session.Baseline, _dryRun?.Predicted, _lastRun != null && _lastRun.Success ? _lastRun.After : null, _lastRun?.Report);

            if (_dryRun != null)
            {
                if (_dryRun.Error != null) EditorGUILayout.HelpBox(_dryRun.Error, MessageType.Error);
                if (!_dryRun.ValidationPassed)
                    EditorGUILayout.HelpBox("Dry Run の検証に失敗しました:\n" + string.Join("\n", _dryRun.ValidationMessages), MessageType.Error);
                EditorGUILayout.LabelField("必要なスナップショット容量: " + Metric.FormatNumber(_dryRun.SnapshotBytes, "bytes"), EditorStyles.miniLabel);
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
                if (_lastRun.Report?.integrity != null)
                    EditorGUILayout.LabelField("原本整合性: " + (_lastRun.Report.integrity.passed ? "OK" : "NG") + "（" + _lastRun.Report.integrity.checkedAssets + " 件）", EditorStyles.miniLabel);
                foreach (var w in _lastRun.Report?.contextWarnings ?? new List<string>())
                    EditorGUILayout.HelpBox("測定条件の差異: " + w, MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(_lastRun.ReportPath) && GUILayout.Button("レポートを開く"))
                EditorUtility.OpenWithDefaultApp(System.IO.Path.GetFullPath(System.IO.Path.Combine(VaoInfo.ProjectRoot, _lastRun.ReportPath)));
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
                EditorGUILayout.HelpBox("「最適化」タブで解析を実行してください。", MessageType.Info);
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
            EditorGUILayout.HelpBox("Android/iOS 派生版の生成は Phase 5 の機能です。プラットフォーム能力 DB・Shader Adapter・SDK ビルド検証の確認が完了するまで有効化しません。", MessageType.Info);
            foreach (VrcPlatform platform in new[] { VrcPlatform.Android, VrcPlatform.iOS })
            {
                EditorGUILayout.LabelField(platform.ToString(), EditorStyles.boldLabel);
                foreach (ContentPreset content in Enum.GetValues(typeof(ContentPreset)))
                {
                    CapabilityDatabase.IsConversionEnabled(platform, content, out var reason);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(content.ToString(), GUILayout.Width(80));
                        EditorGUILayout.LabelField("変換不可: " + reason, EditorStyles.wordWrappedMiniLabel);
                    }
                }
            }
            using (new EditorGUI.DisabledScope(true))
                GUILayout.Button("派生版を生成（無効）");
        }

        // ------------------------------------------------------------------ 履歴タブ

        void DrawHistoryTab()
        {
            if (GUILayout.Button("更新") || _runs == null) _runs = RestoreService.FindRuns();
            if (_runs.Count == 0)
            {
                EditorGUILayout.HelpBox("生成履歴はありません（" + VaoInfo.GeneratedRoot + "）。", MessageType.Info);
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
                            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(run.RunFolder + "/" + VaoInfo.GeneratedFolderName));
                        if (GUILayout.Button("レポート"))
                            EditorUtility.OpenWithDefaultApp(System.IO.Path.Combine(VaoInfo.ProjectRoot, run.RunFolder, VaoInfo.ReportsFolderName, "report.md"));
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
            var refs = RestoreService.FindSceneReferences(run.RunFolder);
            var checks = RestoreService.CheckOriginals(run.Manifest);
            var changed = checks.Where(c => c.State != OriginalState.Unchanged).ToList();
            var msg = "生成物とこの実行のスナップショットを削除します。原本には触れません。";
            if (refs.Count > 0) msg += "\n\n⚠ 読み込み中のシーンが生成物を参照しています（削除すると Missing になります）:\n" + string.Join("\n", refs.Take(10));
            if (changed.Count > 0) msg += "\n\n⚠ 原本が処理後に変更されています。削除するとスナップショットからの復元ができなくなります（" + changed.Count + " 件）。";
            if (!EditorUtility.DisplayDialog("生成物の破棄", msg, "破棄", "キャンセル")) return;
            if (!RestoreService.DiscardRun(run.RunFolder, out var error)) EditorUtility.DisplayDialog("破棄に失敗", error, "OK");
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
            EditorGUILayout.LabelField("区分: 確定計算 / 推定 / 実測 / 取得不可。マウスオーバーで算出方法・算出不可の理由を表示します。", EditorStyles.wordWrappedMiniLabel);
        }

        static GUIContent Cell(Metric m)
        {
            if (m == null || m.IsNotMeasured) return new GUIContent("—");
            var tooltip = m.hasValue ? m.method : m.unavailableReason;
            return new GUIContent(m.FormatValue() + " (" + m.kind.Label() + ")", tooltip);
        }
    }
}
