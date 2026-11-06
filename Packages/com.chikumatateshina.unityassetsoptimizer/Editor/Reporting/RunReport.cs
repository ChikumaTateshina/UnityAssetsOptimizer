using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    [Serializable]
    public class MetricComparison
    {
        public string key;
        public string label;
        public Metric before;
        public Metric predicted;
        public Metric after;
        public bool hasPredictionError;
        /// <summary>(実測/計算結果 - 予測) / 予測。予測または結果が算出不可なら hasPredictionError=false。</summary>
        public double predictionError;
    }

    [Serializable]
    public class OperationReport
    {
        public OperationDecision decision;
        public string expectedEffect;
        public List<PlanItem> items = new List<PlanItem>();
        public List<string> notes = new List<string>();
        public List<string> validation = new List<string>();
    }

    /// <summary>機械可読 JSON と人間向け Markdown の元になるレポート。</summary>
    [Serializable]
    public class RunReport
    {
        public int schemaVersion = 1;
        public string runId;
        public string result;
        public string toolVersion = UnityAssetsOptimizerInfo.ToolVersion;
        public string unityVersion;
        public string sdkVersions;
        public string preset;
        public string createdAtUtc;
        public bool manualOverrideEnabled;
        public List<TargetRef> targets = new List<TargetRef>();
        public OptimizationSettings settings;
        public List<OperationReport> operations = new List<OperationReport>();
        public List<ForceApproval> forceApprovals = new List<ForceApproval>();
        public MeasurementContext contextBefore;
        public MeasurementContext contextAfter;
        public List<string> contextWarnings = new List<string>();
        public List<MetricComparison> metrics = new List<MetricComparison>();
        public List<OutputAsset> outputs = new List<OutputAsset>();
        public List<string> validation = new List<string>();
        public IntegrityReport integrity;
        public string runKind = "Optimize";
        public List<string> outputKinds = new List<string>();
        public List<string> structuralChanges = new List<string>();
        public VisualDiffResult predictedVisualDiff;
        public VisualDiffResult visualDiff;
        public ConversionReport conversion;
        public string restoreState;
        public List<string> nonDeterministicNotes = new List<string>();
        public RunLog log = new RunLog();

        public static List<MetricComparison> Compare(MetricSet before, MetricSet predicted, MetricSet after)
        {
            var list = new List<MetricComparison>();
            foreach (var key in MetricKeys.Ordered)
            {
                var b = before?.Get(key);
                var p = predicted?.Get(key);
                var a = after?.Get(key);
                var label = (b ?? p ?? a)?.label ?? key;
                // JsonUtility は null を空オブジェクトとして書き出すため、未計測も明示的な値にする
                Metric NotMeasured(MetricSet s) => Metric.Unavailable(key, label, "", Metric.NotMeasuredReason, s?.contextId);
                var c = new MetricComparison { key = key, label = label, before = b ?? NotMeasured(before), predicted = p ?? NotMeasured(predicted), after = a ?? NotMeasured(after) };
                if (p != null && a != null && p.hasValue && a.hasValue && Math.Abs(p.value) > 1e-9)
                {
                    c.hasPredictionError = true;
                    c.predictionError = (a.value - p.value) / p.value;
                }
                list.Add(c);
            }
            return list;
        }
    }

    public static class ReportWriter
    {
        public static string ToJson(RunReport report) => JsonUtility.ToJson(report, true);

        public static string ToMarkdown(RunReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# UnityAssetsOptimizer レポート");
            sb.AppendLine();
            sb.AppendLine("| 項目 | 値 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine("| Run ID | " + r.runId + " |");
            sb.AppendLine("| 結果 | " + r.result + " |");
            sb.AppendLine("| 作成日時 (UTC) | " + r.createdAtUtc + " |");
            sb.AppendLine("| ツール版 | " + r.toolVersion + " |");
            sb.AppendLine("| Unity | " + r.unityVersion + " |");
            sb.AppendLine("| VRChat SDK | " + r.sdkVersions + " |");
            sb.AppendLine("| プリセット | " + r.preset + " |");
            sb.AppendLine("| Manual Override | " + (r.manualOverrideEnabled ? "有効" : "無効") + " |");
            sb.AppendLine("| 復元状態 | " + r.restoreState + " |");
            sb.AppendLine();

            sb.AppendLine("## 対象");
            foreach (var t in r.targets) sb.AppendLine("- " + Escape(t.displayPath) + "（" + t.prefabStatus + "）");
            sb.AppendLine();

            sb.AppendLine("## 指標（処理前 / 予測 / 処理後）");
            if (r.contextBefore != null)
                sb.AppendLine("測定条件: " + r.contextBefore.buildTarget + " / " + r.contextBefore.renderPipeline + " / " + r.contextBefore.colorSpace + " / Quality " + r.contextBefore.qualityLevel + " / " + r.contextBefore.measurement);
            foreach (var w in r.contextWarnings) sb.AppendLine("- ⚠ 測定条件の差異: " + w);
            sb.AppendLine();
            sb.AppendLine("| 指標 | 処理前 | 予測 | 処理後 | 予測誤差 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var m in r.metrics)
                sb.AppendLine("| " + m.label + " | " + Cell(m.before) + " | " + Cell(m.predicted) + " | " + Cell(m.after) + " | " +
                              (m.hasPredictionError ? (m.predictionError * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "—") + " |");
            sb.AppendLine();
            var unavailable = r.metrics.SelectMany(m => new[] { m.before, m.predicted, m.after }).Where(m => m != null && !m.hasValue && !m.IsNotMeasured)
                .GroupBy(m => m.key + m.unavailableReason).Select(g => g.First()).ToList();
            if (unavailable.Count > 0)
            {
                sb.AppendLine("算出不可の理由:");
                foreach (var m in unavailable) sb.AppendLine("- " + m.label + ": " + m.unavailableReason);
                sb.AppendLine();
            }

            sb.AppendLine("## 処理ごとの判定");
            foreach (var op in r.operations)
            {
                var d = op.decision;
                sb.AppendLine("### " + (FeatureCatalog.Get(d.operationId)?.Label ?? d.operationId) + " (`" + d.operationId + "`)");
                sb.AppendLine("- モード: " + d.mode.Label() + " / 対応判定: " + d.support.Label() + " / 実行: " + (d.willExecute ? "する" : "しない"));
                foreach (var reason in d.reasons) sb.AppendLine("- " + reason);
                if (!string.IsNullOrEmpty(op.expectedEffect)) sb.AppendLine("- 期待効果: " + op.expectedEffect);
                foreach (var n in op.notes) sb.AppendLine("- 注記: " + n);
                foreach (var v in op.validation) sb.AppendLine("- 検証: " + v);
                var skipped = op.items.Where(i => !i.included).ToList();
                var included = op.items.Where(i => i.included).ToList();
                if (included.Count > 0)
                {
                    sb.AppendLine("- 処理対象:");
                    foreach (var i in included) sb.AppendLine("  - " + Escape(i.target) + (string.IsNullOrEmpty(i.reason) ? string.Empty : "（" + Escape(i.reason) + "）"));
                }
                if (skipped.Count > 0)
                {
                    sb.AppendLine("- スキップ:");
                    foreach (var i in skipped) sb.AppendLine("  - [" + i.support.Label() + "] " + Escape(i.target) + ": " + Escape(i.reason));
                }
                sb.AppendLine();
            }

            if (r.forceApprovals.Count > 0)
            {
                sb.AppendLine("## FORCE 承認（監査ログ）");
                foreach (var f in r.forceApprovals)
                {
                    sb.AppendLine("- " + f.operationId + " by " + f.user + " at " + f.approvedAtUtc);
                    foreach (var risk in f.risks) sb.AppendLine("  - リスク: " + Escape(risk));
                }
                sb.AppendLine();
            }

            if (r.conversion != null && !string.IsNullOrEmpty(r.conversion.platform))
            {
                sb.AppendLine("## " + r.conversion.platform + " 変換結果");
                sb.AppendLine("- 状態: " + r.conversion.StateLabel + " / 実機検証: " + (r.conversion.deviceVerified ? "済" : "未実施"));
                foreach (var reason in r.conversion.reasons) sb.AppendLine("- " + reason);
                foreach (var c in r.conversion.removedBySdk) sb.AppendLine("  - SDK が削除: " + Escape(c));
                sb.AppendLine();
            }

            sb.AppendLine("## 外観の参考比較（画像差分）");
            sb.AppendLine("画像差分は参考値であり、機能同等の証明ではありません。");
            sb.AppendLine("- 予測（Dry Run）: " + DescribeDiff(r.predictedVisualDiff));
            sb.AppendLine("- 処理後: " + DescribeDiff(r.visualDiff));
            sb.AppendLine();

            if (r.outputKinds.Count > 0 || r.structuralChanges.Count > 0)
            {
                sb.AppendLine("## 出力形式と構造変更");
                foreach (var k in r.outputKinds) sb.AppendLine("- " + Escape(k));
                foreach (var c in r.structuralChanges) sb.AppendLine("- " + Escape(c));
                sb.AppendLine();
            }

            sb.AppendLine("## 生成物");
            foreach (var o in r.outputs) sb.AppendLine("- `" + o.path + "`（" + o.kind + ", " + o.operationId + "）");
            sb.AppendLine();

            sb.AppendLine("## 検証");
            foreach (var v in r.validation) sb.AppendLine("- " + v);
            if (r.integrity != null)
            {
                sb.AppendLine("- 原本整合性: " + (r.integrity.passed ? "OK" : "NG") + "（" + r.integrity.checkedAssets + " 件）");
                foreach (var e in r.integrity.errors) sb.AppendLine("  - エラー: " + e);
                foreach (var w in r.integrity.warnings) sb.AppendLine("  - 注意: " + w);
            }
            sb.AppendLine();

            if (r.nonDeterministicNotes.Count > 0)
            {
                sb.AppendLine("## 非決定的要素");
                foreach (var n in r.nonDeterministicNotes) sb.AppendLine("- " + n);
                sb.AppendLine();
            }

            if (r.log.entries.Count > 0)
            {
                sb.AppendLine("## ログ");
                foreach (var e in r.log.entries) sb.AppendLine("- [" + e.level + "] " + e.source + ": " + Escape(e.message));
            }
            return sb.ToString();
        }

        public static string DescribeDiff(VisualDiffResult d)
        {
            if (d == null || (!d.available && string.IsNullOrEmpty(d.reason))) return "未計測";
            if (!d.available) return "取得不可（" + d.reason + "）";
            return "平均差 " + (d.meanAbsDiff * 100).ToString("0.00", CultureInfo.InvariantCulture) + "% / 変化画素 " +
                   (d.changedPixelRatio * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%（" + d.conditions + "）";
        }

        static string Cell(Metric m)
        {
            if (m == null || m.IsNotMeasured) return "—";
            return m.FormatValue() + "（" + m.kind.Label() + "）";
        }

        static string Escape(string s) => (s ?? string.Empty).Replace("|", "\\|").Replace("\n", " ");

        public static void WriteFiles(RunReport report, string directoryFull, string baseName)
        {
            Directory.CreateDirectory(directoryFull);
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(directoryFull, baseName + ".json"), ToJson(report), utf8);
            File.WriteAllText(Path.Combine(directoryFull, baseName + ".md"), ToMarkdown(report), utf8);
        }
    }
}
