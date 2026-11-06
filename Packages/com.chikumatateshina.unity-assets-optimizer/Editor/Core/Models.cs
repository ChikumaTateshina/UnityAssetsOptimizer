using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    /// <summary>
    /// 処理対象の識別子。GlobalObjectId と GUID/localFileID を併用し、
    /// シーンインスタンス・Prefab Asset・Prefab Variant を区別する。
    /// </summary>
    [Serializable]
    public class TargetRef
    {
        public string globalObjectId;
        public string assetGuid;
        public long localFileId;
        public string displayPath;
        public string scenePath;
        public string prefabStatus;
    }

    /// <summary>
    /// 指標値。値を持たない場合（算出不可）は hasValue=false と unavailableReason を必ず設定する。
    /// JsonUtility は Nullable を扱えないため hasValue で表現する。
    /// </summary>
    [Serializable]
    public class Metric : ISerializationCallbackReceiver
    {
        public const string NotMeasuredReason = "この段階では計測していません";

        public string key;
        public string label;
        public bool hasValue;
        public double value;
        public string unit;
        public MetricKind kind;
        [SerializeField] string kindName;
        public string method;
        public string contextId;
        public string unavailableReason;

        public static Metric Of(string key, string label, double value, string unit, MetricKind kind, string method, string contextId)
        {
            return new Metric { key = key, label = label, hasValue = true, value = value, unit = unit, kind = kind, method = method, contextId = contextId };
        }

        public static Metric Unavailable(string key, string label, string unit, string reason, string contextId)
        {
            return new Metric { key = key, label = label, hasValue = false, unit = unit, kind = MetricKind.Unavailable, unavailableReason = reason, contextId = contextId };
        }

        public bool IsNotMeasured => !hasValue && unavailableReason == NotMeasuredReason;

        public string FormatValue()
        {
            if (!hasValue) return "算出不可";
            return FormatNumber(value, unit);
        }

        public static string FormatNumber(double v, string unit)
        {
            if (unit == "bytes")
            {
                double abs = Math.Abs(v);
                if (abs >= 1024 * 1024) return (v / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";
                if (abs >= 1024) return (v / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KiB";
                return v.ToString("0", CultureInfo.InvariantCulture) + " B";
            }
            if (Math.Abs(v - Math.Round(v)) < 1e-9) return v.ToString("N0", CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
        }

        public void OnBeforeSerialize() { kindName = kind.ToString(); }
        public void OnAfterDeserialize() { }
    }

    [Serializable]
    public class MetricSet
    {
        public string contextId;
        public List<Metric> metrics = new List<Metric>();

        public Metric Get(string key)
        {
            foreach (var m in metrics) if (m.key == key) return m;
            return null;
        }

        public void Add(Metric m) { metrics.Add(m); }
    }

    [Serializable]
    public class OperationDecision : ISerializationCallbackReceiver
    {
        public string operationId;
        public SupportState support;
        [SerializeField] string supportName;
        public ExecutionMode mode;
        [SerializeField] string modeName;
        public bool willExecute;
        public List<string> reasons = new List<string>();
        public List<string> dependencies = new List<string>();

        public void OnBeforeSerialize() { supportName = support.ToString(); modeName = mode.ToString(); }
        public void OnAfterDeserialize() { }
    }

    /// <summary>スナップショット対象の原本アセット1件。</summary>
    [Serializable]
    public class ManifestAsset
    {
        public string path;
        public string guid;
        public string sha256;
        public string metaSha256;
        public long size;
        public bool snapshotted;
        public string snapshotFile;
        public string snapshotMetaFile;
        public string note;
    }

    [Serializable]
    public class OutputAsset
    {
        public string path;
        public string guid;
        public string kind;
        public string operationId;
        public string sourceRef;
    }

    [Serializable]
    public class SceneState
    {
        public string path;
        public string guid;
        public bool isLoaded;
        public bool wasDirty;
    }

    /// <summary>FORCE 承認の監査ログ。</summary>
    [Serializable]
    public class ForceApproval
    {
        public string operationId;
        public string approvedAtUtc;
        public string user;
        public List<string> risks = new List<string>();
        public List<string> items = new List<string>();
    }

    [Serializable]
    public class RunManifest : ISerializationCallbackReceiver
    {
        public int schemaVersion = VaoInfo.ManifestSchemaVersion;
        public string runId;
        public string toolVersion;
        public string unityVersion;
        public string sdkVersion;
        public string createdAtUtc;
        public string inputHash;
        public string settingsHash;
        public string snapshotHash;
        public RunState state;
        [SerializeField] string stateName;
        public List<TargetRef> targets = new List<TargetRef>();
        public List<string> outputPaths = new List<string>();
        public List<OutputAsset> outputs = new List<OutputAsset>();
        public List<OperationDecision> decisions = new List<OperationDecision>();
        public List<ManifestAsset> snapshotAssets = new List<ManifestAsset>();
        public List<SceneState> sceneStates = new List<SceneState>();
        public List<ForceApproval> forceApprovals = new List<ForceApproval>();

        public void OnBeforeSerialize() { stateName = state.ToString(); }
        public void OnAfterDeserialize() { }
    }

    /// <summary>処理中のログ。レポートにそのまま残す。</summary>
    [Serializable]
    public class RunLogEntry
    {
        public string level;
        public string source;
        public string message;
    }

    [Serializable]
    public class RunLog
    {
        public List<RunLogEntry> entries = new List<RunLogEntry>();

        public void Info(string source, string message) { Add("info", source, message); }
        public void Warn(string source, string message) { Add("warn", source, message); }
        public void Error(string source, string message) { Add("error", source, message); }

        void Add(string level, string source, string message)
        {
            entries.Add(new RunLogEntry { level = level, source = source, message = message });
        }
    }
}
