using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChikumaTateshina.AssetsOptimizer
{
    [Serializable]
    public class FeatureSetting
    {
        public string id;
        public ExecutionMode mode;
        /// <summary>プリセット初期値からユーザーが変更したか。</summary>
        public bool userModified;
    }

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
