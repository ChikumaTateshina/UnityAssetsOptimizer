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

    /// <summary>ユーザーが選んだ近似 Material の統合（Material はアセットパス + ローカル ID で識別）。</summary>
    [Serializable]
    public class MaterialMergeChoice
    {
        public string member;
        public string representative;
    }

    /// <summary>生成テクスチャ（アトラス）のファイル形式。</summary>
    public enum AtlasImageFormat
    {
        /// <summary>不透明でノーマルマップでなければ JPEG、それ以外（アルファあり・ノーマルマップ）は PNG。</summary>
        JpegWhenOpaque,
        /// <summary>常に PNG（可逆）。</summary>
        Png,
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

        [Header("Atlas File")]
        public AtlasImageFormat atlasImageFormat = AtlasImageFormat.JpegWhenOpaque;
        /// <summary>JPEG の品質（1〜100）。既定の 95 は見た目で劣化が分からない程度。</summary>
        [Range(1, 100)] public int jpegQuality = 95;

        [Header("Mesh")]
        /// <summary>SkinnedMeshRenderer も結合対象にするか（要件: 初期 OFF）。</summary>
        public bool meshCombineSkinned;
        /// <summary>静的結合の空間グリッド（m）。カリング単位を保つため、セルを跨いで結合しない。</summary>
        public float meshCombineCellSize = 10f;
        /// <summary>結合で空になった GameObject を削除する（独立 Prefab 出力とビルド時のみ。参照・アニメーション対象は残す）。</summary>
        public bool removeEmptyObjects = true;
        /// <summary>ポリゴン削減の目標比率（三角形数 × ratio）。</summary>
        [Range(0.05f, 0.95f)] public float polygonReductionRatio = 0.5f;
        /// <summary>簡略化の許容誤差（メッシュ境界ボックス対角長に対する比率）。超過したら不採用。</summary>
        public float decimationMaxError = 0.01f;
        public float[] lodRatios = { 0.5f, 0.25f };
        public float[] lodScreenHeights = { 0.5f, 0.2f, 0.05f };

        [Header("Build-time Polygon Reduction (lilNDMFMeshSimplifier)")]
        /// <summary>目標三角形数。0 なら VRChat SDK の PC Poor 基準（SDK 3.9 では 70,000）。</summary>
        public int buildReductionTargetTriangles;
        /// <summary>品質の下限（これより粗くしない）。</summary>
        [Range(0.1f, 1f)] public float buildReductionMinQuality = 0.4f;
        /// <summary>Viseme・まぶたに使う顔のメッシュは削減しない。</summary>
        public bool protectFaceMesh = true;

        [Header("Similar Material Merge")]
        /// <summary>近似とみなす差分プロパティ数の上限（数値・色のみ。テクスチャ・描画状態が違うものは候補にしない）。</summary>
        [Range(1, 32)] public int similarMaxDifferences = 8;
        public List<MaterialMergeChoice> similarMaterialMerges = new List<MaterialMergeChoice>();

        [Header("Output / Validation")]
        public PrefabOutputMode outputMode = PrefabOutputMode.VariantWhenPossible;
        /// <summary>生成物（Prefab・Material・Mesh・Texture 等）の出力先（Assets 配下）。空なら実行フォルダ内。Manifest・スナップショット・レポートは常に管理フォルダに置く。</summary>
        public string prefabOutputFolder = string.Empty;
        /// <summary>同じ対象を再変換したら、前回の出力（Prefab は GUID を保って）を上書きする。</summary>
        public bool overwritePreviousOutput = true;
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
            // 保存済みの設定にない（後の版で追加された）機能はプリセットの既定値
            return PresetMode(FeatureCatalog.Get(id));
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

        /// <summary>処理内容に影響する設定のハッシュ（出力先は処理内容に影響しないため含めない）。</summary>
        public string ComputeHash()
        {
            var copy = Clone();
            copy.prefabOutputFolder = string.Empty;
            copy.overwritePreviousOutput = true;
            return HashUtility.Sha256(JsonUtility.ToJson(copy));
        }

        /// <summary>出力先フォルダが使えない理由。使えるなら null。</summary>
        public static string ValidateOutputFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal)) return "出力先は Assets フォルダ内を指定してください: " + folder;
            if (folder.Contains("~") || folder.Contains("..")) return "出力先に使えない文字が含まれています: " + folder;
            return null;
        }
    }
}
