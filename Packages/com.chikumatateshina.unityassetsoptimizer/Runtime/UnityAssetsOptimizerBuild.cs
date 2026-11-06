using nadena.dev.ndmf;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer.Ndmf
{
    /// <summary>
    /// アバタールートに付けると、NDMF のビルド（アップロード/Play）時にビルド用クローンへ最適化を適用する。
    /// 原本・シーンは変更しない。INDMFEditorOnly のためビルド成果物には含まれない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UnityAssetsOptimizer/Build Optimizer (NDMF)")]
    public sealed class UnityAssetsOptimizerBuild : MonoBehaviour, INDMFEditorOnly
    {
        /// <summary>OptimizationSettings の JSON。空なら Avatar プリセットの既定値。</summary>
        [SerializeField, HideInInspector] public string settingsJson;
    }
}
