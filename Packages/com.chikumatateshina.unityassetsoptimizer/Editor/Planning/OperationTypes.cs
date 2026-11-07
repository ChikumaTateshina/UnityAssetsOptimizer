using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    public sealed class OperationCapabilities
    {
        public bool World = true;
        public bool Avatar = true;
        public bool StaticMesh = true;
        public bool SkinnedMesh = true;
        public bool BlendShapes = true;
        public bool Implemented = true;
        /// <summary>FORCE を選択可能か。Operation 側が宣言した場合のみ。</summary>
        public bool CanForce;
        public int Phase = 1;
        /// <summary>この Operation より先に計画・実行される Operation。</summary>
        public string[] RunsAfter = new string[0];
        /// <summary>AssetDatabase へ書き込む種別（allowlist の宣言）。書き込み先は常にステージング領域。</summary>
        public string[] WritesAssetKinds = new string[0];
    }

    /// <summary>Operation の判定単位。</summary>
    [Serializable]
    public class PlanItem : ISerializationCallbackReceiver
    {
        public string target;
        public SupportState support;
        [SerializeField] string supportName;
        public string reason;
        /// <summary>FORCE で処理対象にできるか（互換性・外観の自動判定のみが理由の場合に限る）。</summary>
        public bool forceable;
        public bool included;

        public static PlanItem Supported(string target, string reason = "") =>
            new PlanItem { target = target, support = SupportState.Supported, reason = reason };

        public static PlanItem Skip(string target, SupportState support, string reason, bool forceable = false) =>
            new PlanItem { target = target, support = support, reason = reason, forceable = forceable };

        public void OnBeforeSerialize() { supportName = support.ToString(); }
        public void OnAfterDeserialize() { }
    }

    public class OperationAnalysis
    {
        public readonly List<PlanItem> Items = new List<PlanItem>();
        public readonly List<string> Notes = new List<string>();
        public object Data;
    }

    public sealed class OperationPlan
    {
        public IOptimizationOperation Operation;
        public OperationDecision Decision = new OperationDecision();
        public readonly List<PlanItem> Items = new List<PlanItem>();
        public readonly List<string> Notes = new List<string>();
        /// <summary>FORCE で許可した項目のリスク。承認ダイアログと監査ログに使う。</summary>
        public readonly List<string> ForceRisks = new List<string>();
        public string ExpectedEffect = string.Empty;
        public object Data;

        public bool WillExecute => Decision.willExecute;
        public bool UsesForce => Decision.willExecute && Decision.mode == ExecutionMode.Force && ForceRisks.Count > 0;
    }

    public sealed class ValidationResult
    {
        public bool Passed = true;
        public readonly List<string> Messages = new List<string>();

        public void Fail(string message)
        {
            Passed = false;
            Messages.Add(message);
        }
    }

    public interface IOptimizationOperation
    {
        string Id { get; }
        string DisplayName { get; }
        OperationCapabilities Capabilities { get; }
        /// <summary>読み取り専用の解析。対象ごとの対応判定を返す。</summary>
        OperationAnalysis Analyze(OptimizationContext context);
        /// <summary>モードに従って対象を選び、計画を作る。</summary>
        OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode);
        /// <summary>SafetyGate を通過し実行が決まった計画の効果を、後続 Operation の計画用にコンテキストへ反映する。</summary>
        void ApplyToContext(OperationPlan plan, OptimizationContext context);
        /// <summary>隔離されたワーキングコピーに対して生成を行う。</summary>
        void Execute(GenerationContext generation, OperationPlan plan);
        ValidationResult Validate(GenerationContext generation, OperationPlan plan);
    }

    /// <summary>
    /// 生成処理を区切りながら進められる Operation。バックグラウンドの Dry Run ではこちらが使われ、
    /// エディタの操作を妨げない（Execute は同じ処理を最後まで同期実行する）。
    /// </summary>
    public interface IStepwiseOperation
    {
        IEnumerator ExecuteRoutine(GenerationContext generation, OperationPlan plan);
    }

    public abstract class OptimizationOperationBase : IOptimizationOperation
    {
        public abstract string Id { get; }
        public virtual string DisplayName => FeatureCatalog.Get(Id)?.Label ?? Id;
        public abstract OperationCapabilities Capabilities { get; }
        public abstract OperationAnalysis Analyze(OptimizationContext context);

        public virtual OperationPlan Plan(OperationAnalysis analysis, OptimizationContext context, ExecutionMode mode)
        {
            var plan = CreatePlan(analysis, mode);
            return plan;
        }

        public virtual void ApplyToContext(OperationPlan plan, OptimizationContext context) { }
        public abstract void Execute(GenerationContext generation, OperationPlan plan);
        public virtual ValidationResult Validate(GenerationContext generation, OperationPlan plan) => new ValidationResult();

        /// <summary>
        /// 解析結果から項目の採否を決める。AUTO は Supported のみ。FORCE は Operation が CanForce を宣言し、
        /// かつ項目が forceable の場合に限り Unknown/Unsupported を含める。
        /// </summary>
        protected OperationPlan CreatePlan(OperationAnalysis analysis, ExecutionMode mode)
        {
            var plan = new OperationPlan { Operation = this, Data = analysis.Data };
            plan.Decision.operationId = Id;
            plan.Decision.mode = mode;
            plan.Decision.dependencies.AddRange(Capabilities.RunsAfter);
            plan.Notes.AddRange(analysis.Notes);

            foreach (var item in analysis.Items)
            {
                item.included = mode != ExecutionMode.Off &&
                                (item.support == SupportState.Supported ||
                                 (mode == ExecutionMode.Force && Capabilities.CanForce && item.forceable));
                if (item.included && item.support != SupportState.Supported)
                    plan.ForceRisks.Add(item.target + ": " + item.reason);
                plan.Items.Add(item);
            }

            plan.Decision.support = plan.Items.Exists(i => i.support == SupportState.Supported) ? SupportState.Supported
                : plan.Items.Exists(i => i.support == SupportState.Unknown) ? SupportState.Unknown
                : SupportState.Unsupported;
            return plan;
        }
    }

    /// <summary>計画中に Operation 間で共有する読み取り専用コンテキスト。</summary>
    public sealed class OptimizationContext
    {
        public DependencyGraph Graph;
        public AnalysisReport Analysis;
        public OptimizationSettings Settings;

        /// <summary>同一Material統合の結果（実行が決まった場合のみ設定）。</summary>
        public Dictionary<Material, Material> CanonicalMaterials;
        public bool TextureContentDedupe;
        /// <summary>アトラス化で生成予定のテクスチャ数（後続のテクスチャ処理の対象有無判定用）。</summary>
        public int PlannedGeneratedTextureCount;
        /// <summary>ライトマップを焼き込む Renderer（ライトマップ UV の再生成等で UV1 を変えない）。</summary>
        public HashSet<string> LightBakedRendererKeys;
        /// <summary>機能カタログ外の Operation（モバイル変換等）や NDMF 実行時のモード指定。</summary>
        public Dictionary<string, ExecutionMode> ModeOverrides;

        public ExecutionMode ModeFor(string id) =>
            ModeOverrides != null && ModeOverrides.TryGetValue(id, out var m) ? m : Settings.GetMode(id);

        public bool IsApplicable(string id) =>
            (ModeOverrides != null && ModeOverrides.ContainsKey(id)) || Settings.IsApplicable(id);

        public Material Canonical(Material m)
        {
            if (m == null || CanonicalMaterials == null) return m;
            return CanonicalMaterials.TryGetValue(m, out var c) ? c : m;
        }
    }
}
