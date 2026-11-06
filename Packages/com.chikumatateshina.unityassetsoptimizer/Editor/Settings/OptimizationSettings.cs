using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    [Serializable]
    public class FeatureSetting
    {
        public string id;
        public ExecutionMode mode;
        /// <summary>プリセット初期値からユーザーが変更したか。</summary>
        public bool userModified;
    }

    public enum PrefabOutputMode { VariantWhenPossible, Unpacked }

    [Serializable]
    public class OptimizationSettings
    {
        public ContentPreset preset = ContentPreset.World;
        public List<FeatureSetting> features = new List<FeatureSetting>();

        [Header("Atlas")]
        public int atlasMaxSize = 4096;
        public int atlasPadding = 8;

        [Header("Texture Compression")]
        [Range(0, 100)] public int compressionQuality = 50;
        public bool standaloneCrunch;
        public int mobileAstcBlock = 6;   // 4, 5, 6, 8

        [Header("Texture Downscale")]
        public int downscaleMaxSize = 2048;

        [Header("Mesh")]
        /// <summary>SkinnedMeshRenderer も結合対象にするか（要件: 初期 OFF）。</summary>
        public bool meshCombineSkinned;
        /// <summary>静的結合の空間グリッド（m）。カリング単位を保つため、セルを跨いで結合しない。</summary>
        public float meshCombineCellSize = 10f;
        /// <summary>ポリゴン削減の目標比率（三角形数 × ratio）。</summary>
        [Range(0.05f, 0.95f)] public float polygonReductionRatio = 0.5f;
        /// <summary>簡略化の許容誤差（メッシュ境界ボックス対角長に対する比率）。超過したら不採用。</summary>
        public float decimationMaxError = 0.01f;
        public float[] lodRatios = { 0.5f, 0.25f };
        public float[] lodScreenHeights = { 0.5f, 0.2f, 0.05f };

        [Header("Output / Validation")]
        public PrefabOutputMode outputMode = PrefabOutputMode.VariantWhenPossible;
        /// <summary>隔離プレビューで前後をレンダリングし、画像差分を参考値として記録する。</summary>
        public bool visualDiff = true;

        [Header("Mobile Conversion")]
        /// <summary>モバイル用シェーダー変換のモード（非等価変換は FORCE のみ）。</summary>
        public ExecutionMode mobileShaderMode = ExecutionMode.Auto;

        public static OptimizationSettings CreateDefault(ContentPreset preset)
        {
            var s = new OptimizationSettings();
            s.ApplyPreset(preset);
            return s;
        }

        public void ApplyPreset(ContentPreset newPreset)
        {
            preset = newPreset;
            features.Clear();
            foreach (var def in FeatureCatalog.All)
            {
                features.Add(new FeatureSetting { id = def.Id, mode = PresetMode(def), userModified = false });
            }
        }

        public ExecutionMode PresetMode(FeatureDefinition def)
        {
            var d = def.DefaultFor(preset);
            return d == true ? ExecutionMode.Auto : ExecutionMode.Off;
        }

        public bool IsApplicable(string id)
        {
            var def = FeatureCatalog.Get(id);
            return def != null && def.DefaultFor(preset).HasValue;
        }

        public ExecutionMode GetMode(string id)
        {
            if (!IsApplicable(id)) return ExecutionMode.Off;
            foreach (var f in features) if (f.id == id) return f.mode;
            return ExecutionMode.Off;
        }

        public void SetMode(string id, ExecutionMode mode)
        {
            var def = FeatureCatalog.Get(id);
            if (def == null) return;
            var setting = features.Find(f => f.id == id);
            if (setting == null)
            {
                setting = new FeatureSetting { id = id };
                features.Add(setting);
            }
            setting.mode = mode;
            setting.userModified = mode != PresetMode(def);
        }

        public bool IsUserModified(string id)
        {
            var setting = features.Find(f => f.id == id);
            return setting != null && setting.userModified;
        }

        public OptimizationSettings Clone() => JsonUtility.FromJson<OptimizationSettings>(JsonUtility.ToJson(this));

        public string ComputeHash() => HashUtility.Sha256(JsonUtility.ToJson(this));
    }
}
