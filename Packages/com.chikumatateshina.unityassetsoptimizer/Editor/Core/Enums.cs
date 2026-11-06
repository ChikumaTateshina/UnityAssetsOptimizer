namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>処理対象・処理内容に対する対応判定。</summary>
    public enum SupportState { Supported, Unsupported, Unknown }

    /// <summary>機能ごとの実行モード。Force は Manual Override 有効時のみ選択可能。</summary>
    public enum ExecutionMode { Off, Auto, Force }

    /// <summary>指標値の信頼区分。予測値を実測値として扱わないために必ず付与する。</summary>
    public enum MetricKind { Calculated, Estimated, Measured, Unavailable }

    public enum RunState { Created, Analyzed, Planned, Previewed, Approved, Generating, Validating, Committed, RolledBack, Failed }

    public enum ConversionState { Verified, Partial, Incompatible }

    public enum ContentPreset { World, Avatar }

    public enum RankStatus { Official, Estimated, Incomplete, Unavailable }

    public enum VrcPlatform { PC, Android, iOS }

    public static class EnumLabels
    {
        public static string Label(this MetricKind kind)
        {
            switch (kind)
            {
                case MetricKind.Calculated: return "確定計算";
                case MetricKind.Estimated: return "推定";
                case MetricKind.Measured: return "実測";
                default: return "取得不可";
            }
        }

        public static string Label(this SupportState state)
        {
            switch (state)
            {
                case SupportState.Supported: return "対応";
                case SupportState.Unsupported: return "非対応";
                default: return "不明";
            }
        }

        public static string Label(this ExecutionMode mode)
        {
            switch (mode)
            {
                case ExecutionMode.Off: return "OFF";
                case ExecutionMode.Auto: return "AUTO";
                default: return "FORCE";
            }
        }

        public static string Label(this RankStatus status)
        {
            switch (status)
            {
                case RankStatus.Official: return "official (SDK公式)";
                case RankStatus.Estimated: return "estimated (独立推定)";
                case RankStatus.Incomplete: return "incomplete (評価不完全)";
                default: return "unavailable (取得不可)";
            }
        }
    }
}
