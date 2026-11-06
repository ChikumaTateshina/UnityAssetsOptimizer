using System;
using System.Collections.Generic;
using System.Linq;

namespace ChikumaTateshina.AssetsOptimizer
{
    public static class OptimizationPlanner
    {
        public static List<OperationPlan> Plan(OptimizationContext context, IEnumerable<IOptimizationOperation> operations)
        {
            var ordered = TopologicalSort(operations.ToList());
            var plans = new List<OperationPlan>();
            foreach (var op in ordered)
            {
                var mode = context.ModeFor(op.Id);
                OperationPlan plan;
                if (mode == ExecutionMode.Off || !op.Capabilities.Implemented)
                {
                    plan = new OperationPlan { Operation = op };
                    plan.Decision.operationId = op.Id;
                    plan.Decision.mode = mode;
                    plan.Decision.support = op.Capabilities.Implemented ? SupportState.Supported : SupportState.Unsupported;
                }
                else
                {
                    var analysis = op.Analyze(context);
                    plan = op.Plan(analysis, context, mode);
                }

                SafetyGate.Evaluate(plan, op, mode, context);
                if (plan.WillExecute) op.ApplyToContext(plan, context);
                plans.Add(plan);
            }
            return plans;
        }

        /// <summary>RunsAfter に従う安定トポロジカルソート。循環は例外（fail-closed）。</summary>
        public static List<IOptimizationOperation> TopologicalSort(List<IOptimizationOperation> ops)
        {
            var result = new List<IOptimizationOperation>();
            var state = new Dictionary<string, int>();
            var byId = ops.ToDictionary(o => o.Id);

            void Visit(IOptimizationOperation op)
            {
                state.TryGetValue(op.Id, out int s);
                if (s == 2) return;
                if (s == 1) throw new InvalidOperationException("Operation の依存関係が循環しています: " + op.Id);
                state[op.Id] = 1;
                foreach (var dep in op.Capabilities.RunsAfter)
                    if (byId.TryGetValue(dep, out var d)) Visit(d);
                state[op.Id] = 2;
                result.Add(op);
            }

            foreach (var op in ops) Visit(op);
            return result;
        }
    }

    /// <summary>
    /// 実行可否の最終判定。Operation の自己申告に加え、解除不可の不変条件をここで強制する。
    /// </summary>
    public static class SafetyGate
    {
        public static void Evaluate(OperationPlan plan, IOptimizationOperation op, ExecutionMode mode, OptimizationContext context)
        {
            var d = plan.Decision;
            d.willExecute = false;

            if (mode == ExecutionMode.Off)
            {
                d.reasons.Add("OFF に設定されています。");
                return;
            }
            if (!op.Capabilities.Implemented)
            {
                d.reasons.Add((op is UnimplementedOperation u ? u.Reason : "未実装の処理です（Phase " + op.Capabilities.Phase + "）。") + " FORCE でも実行しません。");
                ExcludeAll(plan);
                return;
            }
            if (!context.IsApplicable(op.Id))
            {
                d.reasons.Add("このプリセットでは対象外です。");
                ExcludeAll(plan);
                return;
            }
            if (mode == ExecutionMode.Force && !op.Capabilities.CanForce)
            {
                d.reasons.Add("この処理は FORCE に対応していません。AUTO として判定します。");
                d.mode = ExecutionMode.Auto;
                foreach (var item in plan.Items) item.included = item.support == SupportState.Supported;
                plan.ForceRisks.Clear();
            }
            if (!ManualOverride.IsEnabled && d.mode == ExecutionMode.Force)
            {
                d.reasons.Add("Manual Override が無効のため FORCE は使用できません。AUTO として判定します。");
                d.mode = ExecutionMode.Auto;
                foreach (var item in plan.Items) item.included = item.support == SupportState.Supported;
                plan.ForceRisks.Clear();
            }
            if (context.Analysis.HasBlockingIssues)
            {
                d.reasons.Add("解析で実行不可の問題が検出されています。");
                ExcludeAll(plan);
                return;
            }

            // 非 forceable の非対応項目は FORCE でも含めない
            foreach (var item in plan.Items)
                if (item.included && item.support != SupportState.Supported && !(d.mode == ExecutionMode.Force && item.forceable))
                    item.included = false;

            if (!plan.Items.Exists(i => i.included))
            {
                d.reasons.Add("処理可能な対象がありません。");
                return;
            }

            d.willExecute = true;
            int skipped = plan.Items.Count(i => !i.included);
            d.reasons.Add("対象 " + plan.Items.Count(i => i.included) + " 件" + (skipped > 0 ? "、スキップ " + skipped + " 件" : string.Empty) + "。");
        }

        static void ExcludeAll(OperationPlan plan)
        {
            foreach (var item in plan.Items) item.included = false;
            plan.ForceRisks.Clear();
        }
    }
}
