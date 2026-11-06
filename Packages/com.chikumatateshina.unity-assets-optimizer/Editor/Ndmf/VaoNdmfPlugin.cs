using System.Linq;
using ChikumaTateshina.AssetsOptimizer.Ndmf;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(VaoNdmfPlugin))]

namespace ChikumaTateshina.AssetsOptimizer.Ndmf
{
    /// <summary>
    /// NDMF プラグイン。Optimizing フェーズで、Avatar Optimizer（AAO）より前に AUTO の処理を適用する。
    /// </summary>
    public sealed class VaoNdmfPlugin : Plugin<VaoNdmfPlugin>
    {
        public override string QualifiedName => VaoInfo.PackageName;
        public override string DisplayName => "VRChat Asset Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Apply VRChat Asset Optimizer", ctx =>
                {
                    var component = ctx.AvatarRootObject.GetComponentInChildren<VaoBuildOptimizer>(true);
                    if (component == null) return;
                    var settings = VaoBuildOptimizerEditor.Load(component);
                    Object.DestroyImmediate(component);
                    var report = OptimizerPipeline.ApplyInPlace(ctx.AvatarRootObject, settings, o => ctx.AssetSaver.SaveAsset(o));
                    Debug.Log("[VAO] NDMF ビルド時最適化を適用しました: " + report.runId + "（レポート: Library/VAOLogs）");
                });
        }
    }

    [CustomEditor(typeof(VaoBuildOptimizer))]
    public sealed class VaoBuildOptimizerEditor : UnityEditor.Editor
    {
        public static OptimizationSettings Load(VaoBuildOptimizer component)
        {
            OptimizationSettings settings = null;
            if (!string.IsNullOrEmpty(component.settingsJson))
            {
                try { settings = JsonUtility.FromJson<OptimizationSettings>(component.settingsJson); }
                catch (System.Exception) { settings = null; }
            }
            return settings != null && settings.features.Count > 0 ? settings : OptimizationSettings.CreateDefault(ContentPreset.Avatar);
        }

        public override void OnInspectorGUI()
        {
            var component = (VaoBuildOptimizer)target;
            var settings = Load(component);
            EditorGUILayout.HelpBox("NDMF ビルド時（アップロード/Play）にビルド用クローンへ AUTO の処理を適用します。原本・シーンは変更しません。FORCE とテクスチャのコピー生成はビルド時には行いません。", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            var preset = (ContentPreset)EditorGUILayout.EnumPopup("プリセット", settings.preset);
            if (preset != settings.preset) settings.ApplyPreset(preset);
            foreach (var def in FeatureCatalog.All.Where(d => settings.IsApplicable(d.Id)))
            {
                bool on = settings.GetMode(def.Id) != ExecutionMode.Off;
                bool now = EditorGUILayout.Toggle(new GUIContent(def.Label, def.Note), on);
                if (now != on) settings.SetMode(def.Id, now ? ExecutionMode.Auto : ExecutionMode.Off);
            }
            settings.atlasMaxSize = EditorGUILayout.IntPopup("アトラス最大解像度", settings.atlasMaxSize, new[] { "1024", "2048", "4096" }, new[] { 1024, 2048, 4096 });
            settings.meshCombineSkinned = EditorGUILayout.Toggle("SkinnedMesh も結合", settings.meshCombineSkinned);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(component, "VAO Build Optimizer settings");
                component.settingsJson = JsonUtility.ToJson(settings);
                EditorUtility.SetDirty(component);
            }
        }
    }
}
