using System.Linq;
using ChikumaTateshina.UnityAssetsOptimizer.Ndmf;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(UnityAssetsOptimizerNdmfPlugin))]

namespace ChikumaTateshina.UnityAssetsOptimizer.Ndmf
{
    /// <summary>
    /// NDMF プラグイン。Optimizing フェーズで、Avatar Optimizer（AAO）より前に AUTO の処理を適用する。
    /// </summary>
    public sealed class UnityAssetsOptimizerNdmfPlugin : Plugin<UnityAssetsOptimizerNdmfPlugin>
    {
        public override string QualifiedName => UnityAssetsOptimizerInfo.PackageName;
        public override string DisplayName => "UnityAssetsOptimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Apply UnityAssetsOptimizer", ctx =>
                {
                    var component = ctx.AvatarRootObject.GetComponentInChildren<UnityAssetsOptimizerBuild>(true);
                    if (component == null) return;
                    var settings = UnityAssetsOptimizerBuildEditor.Load(component);
                    Object.DestroyImmediate(component);
                    var report = OptimizerPipeline.ApplyInPlace(ctx.AvatarRootObject, settings, o => ctx.AssetSaver.SaveAsset(o));
                    Debug.Log("[UnityAssetsOptimizer] NDMF ビルド時最適化を適用しました: " + report.runId + "（レポート: Library/UnityAssetsOptimizer/Logs）");
                });
        }
    }

    [CustomEditor(typeof(UnityAssetsOptimizerBuild))]
    public sealed class UnityAssetsOptimizerBuildEditor : UnityEditor.Editor
    {
        public static OptimizationSettings Load(UnityAssetsOptimizerBuild component)
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
            var component = (UnityAssetsOptimizerBuild)target;
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
                Undo.RecordObject(component, "Build Optimizer settings");
                component.settingsJson = JsonUtility.ToJson(settings);
                EditorUtility.SetDirty(component);
            }
        }
    }
}
