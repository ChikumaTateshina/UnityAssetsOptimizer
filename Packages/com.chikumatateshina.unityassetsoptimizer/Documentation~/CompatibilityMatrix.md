# Compatibility Matrix

実装仕様書 §16 に基づき、対象 Unity / VRChat SDK / 外部パッケージの公開 API・仕様の検証状況を記録する。
**「未検証」の項目は実装済みとして扱わず、該当機能は無効（取得不可/変換不可/スキップ）とする。**

最終更新: 2026-11-07（v0.7.0）

## 検証環境

| 項目 | 版 | 確認方法 |
|---|---|---|
| Unity | 2022.3.22f1（VRChat 推奨）/ 2022.3.31f1 | Unity DLL に対するコンパイル、EditMode テスト（SDK なし / SDK・NDMF・lilToon・Poiyomi あり の2構成） |
| VRChat SDK Base / Avatars | 3.9.0 | パッケージのソースと公開クラスを確認し、SDK 入りプロジェクトで EditMode テスト |
| NDMF | 1.9.4 | `Plugin<T>` / `BuildPhase.Optimizing` / `Sequence.Run` / `INDMFEditorOnly` のソースを確認し、`AvatarProcessor.ProcessAvatar` でテスト |
| lilToon | 2.3.1 | `Shader/lts*.shader` と `Includes/lil_common_frag.hlsl` でテクスチャの UV を確認し、テスト |
| Poiyomi Toon（無料版） | 10.0.29（ThryEditor 2.74.4） | `Shaders/10.0/Toon/*.shader` の全サンプル呼び出しを抽出して分類し、テスト |
| Avatar Optimizer（anatawa12） | 1.8.14（使用範囲 >=1.8.0 <2.0.0） | `TraceAndOptimize` のソース（公開 API は型のみ・既定値で使用）を確認し、テスト |
| lilNDMFMeshSimplifier（lilxyzw） | 0.3.0（使用範囲 >=0.3.0 <0.4.0） | `NDMFMeshSimplifier`（`quality` 0〜1）と NDMF プラグインのソースを確認し、テスト |

## Shader Adapter（アトラス化）

| Adapter | 対象 | アトラス化するテクスチャ | 状態 |
|---|---|---|---|
| unity.standard / standard-specular | Standard, Standard (Specular setup) | Main/Metallic(Spec)/Bump/Parallax/Occlusion/Emission/DetailMask | 対応（Detail Albedo/Normal 使用時は非対応） |
| unity.unlit-* / legacy-diffuse / mobile-diffuse | Unlit/Texture, Unlit/Transparent(+Cutout), Legacy Shaders/Diffuse, Mobile/Diffuse | _MainTex | 対応 |
| liltoon | lilToon（通常/Cutout/Transparent/OnePass/TwoPass、各 Outline、Lite 系） | メイン UV（`uvMain`）でサンプルされるもの: Main/BaseMap/AlphaMask/Bump/Shadow*/Rim*/Matcap マスク/Emission(2nd)/Outline 等 | 対応（下記条件） |
| — | lilToon Fur/Gem/Refraction/Tessellation/FakeShadow/Overlay/Multi/OutlineOnly | — | 非対応（UV 以外の処理を含むため未検証） |
| poiyomi-toon | .poiyomi/Poiyomi Toon, Two Pass, Outline Early, World | `poiUV(poiMesh.uv[<名前>UV], <名前>_ST)` + パン でサンプルされる 71 テクスチャ（Main/Bump/AlphaMask/Emission 0-3/Matcap マスク/Rim/Shadow/Outline マスク 等） | 対応（下記条件） |
| — | Poiyomi Grab Pass / Lil Fur 版、ロック済み（Hidden/Locked/...）、Poiyomi Pro | — | 非対応（未検証。ロック済みはロック解除後に実行） |
| — | VRChat/Mobile/* | — | アトラス対象外（変換先として使用） |

lilToon の条件（いずれかに該当すると AUTO でアトラス化しない）:
- MatCap・グラデーション・ディザ・Ramp テクスチャがグループ内で異なる（UV に依存しないため同一である必要がある）
- 2nd/3rd メイン、2nd ノーマル、異方性、Glitter、視差、AudioLink、Dissolve のテクスチャ設定、または機能が有効
- `_MainTex_ScrollRotate`・`_OutlineTex_ScrollRotate`・発光マップの UV アニメーション、`_ShiftBackfaceUV`、発光マップの UV モード ≠ UV0、UDIM Discard

Poiyomi の条件（いずれかに該当すると AUTO でアトラス化しない）:
- 設定されたテクスチャの UV 選択（`<名前>UV`）が UV0 以外、パン（`<名前>Pan`）が 0 以外、Stochastic が有効、発光マップの Center Out が有効
- UV0 の全体 UV 設定（`_UVSettingsTiling0/Offset0/Angle0/Pan0/Rotate0`）が単位値でない
- 視差（`_PoiParallax`）、Pixel Mode、Video Effects、UDIM Discard、Text、UV Distortion のいずれかが有効
- MatCap・Toon Ramp・LUT（`_PoiSkinLUT` / `_SkinLUT` / `_ClothDFG`）・グリフ・歪みフローがグループ内で異なる（UV に依存しないかシェーダー既定値のため、同一である必要がある）

共通の前提: 全チャンネルの Tiling/Offset が単位値、サブメッシュの UV0 が [0,1] 内、テクスチャが 2D・Default（ノーマルは NormalMap かつ PNG/JPEG）。

## VRChat Avatar Performance Rank

| 項目 | 状態 |
|---|---|
| SDK 公式評価 | **対応**: `VRC.SDKBase.Validation.Performance.AvatarPerformance.CalculatePerformanceStats` と `AvatarPerformanceStats.GetPerformanceRatingForCategory` / `GetStatLevelForRating` をリフレクションで呼ぶ（SDK へのコンパイル時依存なし） |
| PC / Android | SDK の PC 閾値 / モバイル閾値で算出（official） |
| iOS | SDK 3.9.0 の閾値リソースは Windows と Quest のみで、評価はモバイル共通のため、モバイル閾値を適用（理由をレポートに明記） |
| SDK なし | 検証済み閾値 DB（`Assets/UnityAssetsOptimizerSettings/RankRules/*.json`、verified=true）があれば独立推定。なければ統計値のみ |
| ビルド時変換ツールあり | incomplete（評価不完全） |

## Mobile Conversion（Android / iOS）

| 項目 | 状態 |
|---|---|
| 有効化条件 | 能力 DB（`Editor/Platforms/Data/Capabilities`）の検証範囲: SDK `>=3.9.0 <3.10.0`、Unity `2022.3`。範囲外の SDK では無効（fail-closed） |
| iOS 対象 | SDK 3.9.0 の `VRCMultiPlatformBuild` に iOS が含まれることを確認 |
| アバターの判定 | `VRC.SDK3.Validation.AvatarValidation.FindIllegalShaders` で違反なし → 完了（SDK 検証済）。違反が残る → 部分変換/変換不可。SDK がビルド時に削除するコンポーネント（`FindIllegalComponents`）はレポートに列挙（本ツールは削除しない） |
| ワールドの判定 | SDK の検証 API に接続していないため、最大で「部分変換」 |
| 実機検証 | 本ツールでは行わない（別ステータス「未実施」） |
| アップロード | 行わない |

シェーダー変換規則（変換先のプロパティは SDK 3.9.0 同梱シェーダーのソースで確認）:

| 変換元 | 変換先 | AUTO |
|---|---|---|
| Standard（不透明・視差/Detail なし） | VRChat/Mobile/Standard Lite | ○ |
| Legacy/Mobile Diffuse（_Color が白） | VRChat/Mobile/Diffuse | ○ |
| Unlit/Texture | VRChat/Mobile/Toon Lit | × FORCE のみ（Unlit → Lit） |
| lilToon（不透明） | VRChat/Mobile/Toon Standard | × FORCE のみ（影・MatCap・輪郭等は失われる） |
| Poiyomi Toon（Rendering Preset = Opaque） | VRChat/Mobile/Toon Standard | × FORCE のみ（影・MatCap・リム・輪郭等は失われる） |
| 透明系 | — | 変換不可（モバイル用アバターシェーダーに透明なし） |

## NDMF（ビルド時適用）

- NDMF が入っている場合のみ、`ChikumaTateshina.UnityAssetsOptimizer.Ndmf.*` アセンブリが有効になる（`versionDefines` + `defineConstraints`）
- `Optimizing` フェーズで、Avatar Optimizer（`com.anatawa12.avatar-optimizer`）より前に実行する
- ビルド用クローンにだけ AUTO の処理を適用する。FORCE、元テクスチャのコピー、スナップショットは使わない。生成物は `BuildContext.AssetSaver` に保存する

## 連携ツール

導入済みかつ使用範囲内の版の場合のみ使う（範囲外・未導入はスキップし、理由と導入手順を表示）。コンパイル時の依存は持たない。

| ツール | 使い方 | VPM リポジトリ |
|---|---|---|
| Avatar Optimizer | 出力アバターのルートに `TraceAndOptimize` を既定値で追加（既にあれば何もしない） | https://vpm.anatawa12.com/vpm.json |
| lilNDMFMeshSimplifier | 削減対象の Renderer に `NDMFMeshSimplifier` を追加し `quality` を設定（既にあれば尊重）。品質 = (目標 − 対象外の三角形) ÷ 対象の三角形、下限は設定値 | https://lilxyzw.github.io/vpm-repos/vpm.json |
| NDMF | ビルド後の評価で `AvatarProcessor.ProcessAvatar` を複製に適用（`OverrideTemporaryDirectoryScope` で生成物を一時フォルダに保存し、評価後に削除） | https://vpm.nadena.dev/vpm.json |

- どちらもアップロード時（NDMF のビルド）に処理されるため、効果は「ビルド後の評価」で確認する
- NDMF のビルド時適用（Build Optimizer コンポーネント）からは追加しない（ビルド中は各ツールの処理順が確定しているため）
- VRCFury など NDMF 以外のビルド時ツールの処理はビルド後の評価に含まれない（Rank は「評価不完全」）

## 計測

| 指標 | 区分 |
|---|---|
| Mesh/Renderer/Material/SubMesh/三角形/頂点/Texture 数 | 確定計算 |
| 依存アセットのディスク容量 | 確定計算（Dry Run では生成物が未保存のため算出不可） |
| Texture GPU メモリ | 推定（graphicsFormat × 解像度 × ミップ。常駐 VRAM ではない） |
| Draw Calls / SetPass / Batches | 推定（モデル推定: Built-in Forward・平行光源1灯・影なし・バッチングなし・全可視） |
| CPU/GPU 描画時間 | 取得不可（実機/Profiler での実測が必要） |
| ファイルサイズ（圧縮後 / 展開後） | 確定計算（現在のビルドターゲットで AssetBundle をビルド。NDMF があれば適用後の構成、なければ Prefab のまま） |
| 外観の参考比較 | 隔離プレビューで3方向から描画した画像差分（参考値。グラフィックスデバイスがない環境では取得不可） |

## 実装しないもの

| 機能 | 理由 |
|---|---|
| Mesh Compression | Unity のメッシュ圧縮（量子化）の仕様が公開されておらず、要件の「形状誤差を検証して不採用判定」ができない |
| PC 版でのシェーダー自動置換 | 等価変換の検証済み組み合わせは Mobile Conversion でのみ提供する |
| Poiyomi Pro / Grab Pass / Lil Fur 版 | 有料配布または別処理を含み、ソースを確認していないため |
