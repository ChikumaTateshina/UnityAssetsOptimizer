using nadena.dev.ndmf;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer.Ndmf
{
    /// <summary>
    /// アバタールートに付けると、NDMF のビルド（アップロード/Play）時にビルド用クローンへ最適化を適用する。
    /// 原本・シーンは変更しない。INDMFEditorOnly のためビルド成果物には含まれない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VRChat Asset Optimizer/VAO Build Optimizer (NDMF)")]
    public sealed class VaoBuildOptimizer : MonoBehaviour, INDMFEditorOnly
    {
        /// <summary>OptimizationSettings の JSON。空なら Avatar プリセットの既定値。</summary>
        [SerializeField, HideInInspector] public string settingsJson;
    }
}
