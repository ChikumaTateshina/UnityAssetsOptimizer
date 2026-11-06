# Compatibility Matrix

実装仕様書 §16 に基づき、対象 Unity / VRChat SDK の公開 API・公式仕様の検証状況を記録する。
**「未検証」の項目は実装済みとして扱わず、該当機能は無効（取得不可/変換不可/スキップ）とする。**

最終更新: 2026-11-07（v0.1.0）

## 環境

| 項目 | 状態 | 備考 |
|---|---|---|
| Unity 2022.3（Built-in RP） | コンパイル・EditMode テストで確認 | 2022.3.31f1 で確認。VRChat が指定する 2022.3 系パッチ版での確認は利用者側で実施すること |
| VRChat SDK（Base/Avatars/Worlds） | コンパイル時依存なし | バージョンは `PackageInfo` から読み取り、レポートに記録のみ |
| Modular Avatar / VRCFury / NDMF | 検出のみ | 型名の名前空間で検出し「評価不完全」として扱う。ビルド時変換にはアクセスしない |

## Shader Adapter（アトラス化）

| Adapter ID | シェーダー | チャンネル | 状態 |
|---|---|---|---|
| unity.standard | Standard | _MainTex, _MetallicGlossMap, _BumpMap, _ParallaxMap, _OcclusionMap, _EmissionMap, _DetailMask | 対応（Detail Albedo/Normal 使用時は非対応） |
| unity.standard-specular | Standard (Specular setup) | 同上（_SpecGlossMap） | 対応（同上） |
| unity.unlit-texture 他 | Unlit/Texture, Unlit/Transparent, Unlit/Transparent Cutout, Legacy Shaders/Diffuse, Mobile/Diffuse | _MainTex | 対応 |
| — | VRChat/Mobile/* | — | **未検証**（プロパティ仕様の確認後に Adapter を追加） |
| — | lilToon / Poiyomi / UTS 等 | — | **未検証**（専用 Adapter が必要） |

前提条件（すべて満たす場合のみ AUTO で処理）:
- 全チャンネルの Tiling/Offset が (1,1)/(0,0)
- サブメッシュの UV0 が [0,1] 内（タイル/ミラー/負座標はスキップ）
- テクスチャが 2D・Texture Type が Default（ノーマルは NormalMap）・PNG/JPEG（カラー/データは TGA/PSD/TIFF/BMP/GIF も GPU 読み戻しで可）
- 互換グループ内で Shader・Render Queue・Keyword・Pass・非テクスチャプロパティ・Importer 設定（sRGB/Alpha/Filter/Aniso）が一致

## VRChat Avatar Performance Rank

| 項目 | 状態 |
|---|---|
| SDK 公式評価 API | **未検証** → `SdkOfficialRankProvider` は常に unavailable |
| 閾値 DB（PC/Android/iOS） | **未検証** → `Editor/Platforms/Data/RankRules/TEMPLATE.*.json`（verified=false）のみ同梱。ランクは「算出不可」、統計値のみ表示 |

閾値を有効化する手順: 公式ドキュメントで対象 SDK 版の閾値を確認し、`Assets/VAOSettings/RankRules/<id>.json` に
`verified: true`・`sdkMinVersion`/`sdkMaxVersionExclusive`・`source`（出典）を記入して配置する。

## Mobile Conversion（Phase 5）

| プラットフォーム | World | Avatar |
|---|---|---|
| Android | 未検証（変換不可） | 未検証（変換不可） |
| iOS | 未検証（変換不可） | 未検証（変換不可） |

能力 DB テンプレート: `Editor/Platforms/Data/Capabilities/*.json`（すべて verified=false）。

## 計測

| 指標 | 状態 |
|---|---|
| Mesh/Renderer/Material/SubMesh/三角形/頂点/Texture 数 | 確定計算 |
| 依存アセットのディスク容量 | 確定計算（Dry Run では生成物が未保存のため算出不可） |
| Texture GPU メモリ | 推定（graphicsFormat × 解像度 × ミップ。常駐 VRAM ではない） |
| Draw Calls / SetPass / Batches | **取得不可**（隔離プレビュー計測は Phase 3） |
| CPU/GPU 描画時間 | **取得不可**（Profiler 計測は Phase 3） |
