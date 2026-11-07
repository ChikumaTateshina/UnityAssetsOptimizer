# Changelog

## [0.13.0] - 2026-11-08

### Added
- ライトマップ焼き込み（World、既定 OFF）: Unity（Progressive）・Bakery でベイクしてシーンに適用されたライトマップを、Renderer ごとに使っている部分だけ切り出して
  1 枚の光テクスチャ（UV1）にまとめ、色に掛け合わせる。色のテクスチャ（アトラス）は複製しないため、同じ Material を使うオブジェクトもそれぞれの場所の光になる
  - シェーダー: 「Unlit に切り替え」（同梱の `UnityAssetsOptimizer/Baked Light Unlit`。強度は Material の Light Strength で後から調整可）または
    「元のシェーダーのまま」（Standard は Detail Albedo ×2、lilToon はメイン 2nd の乗算に光を入れる。lilToon は _Color2nd のアルファで強度を調整可）
  - 強度（0〜1）と光テクスチャの最大解像度を設定可能。焼き込んだオブジェクトは Contribute GI を外し、再ベイクで光が二重にならないようにする
  - ライトマップは GPU 経由で読み取り、保存形式（HDR / RGBM / dLDR）に応じて元の明るさに戻す

### Fixed
- 結合したメッシュをライトベイクすると光がちぐはぐになる問題: 各メッシュのライトマップ UV（UV1）がそのまま重なっていた。
  ライトマップの対象（Contribute GI）を結合した場合は、各メッシュの UV1 を面積に比例した区画へ重ならないよう並べ直す（UV1 がなければ自動生成）

## [0.12.0] - 2026-11-08

### Changed
- 出力先を指定した場合、変換したオブジェクトごとにフォルダ（`<出力先>/<オブジェクト名>/`、Android/iOS 版は `<名前>_Android` 等）を作り、
  Prefab と Materials/・Meshes/・Textures/ をその中に置く（オブジェクト間で共有しない）。1 回の実行で複数のオブジェクトを変換し、
  アトラス等を共有した場合のみ `<出力先>/_Shared/` に置く
- 再変換で上書きするとき、前回の実行が出力先に作り空になったフォルダ（旧構成の Materials/ 等）も片付ける

## [0.11.1] - 2026-11-08

### Fixed
- ポリゴン削減・LOD 生成でテクスチャが歪む問題: 形の誤差だけで縮約を判定していたため、平らな面（標識・看板等）では頂点が自由にまとめられ、
  消えた頂点の UV が失われていた。縮約前後の UV（UV0 と、あれば UV1）の差を、消える頂点・周囲の三角形の重心・辺の中点で測り、
  許容値（UV 単位 0.0005。4096 のアトラスで約 2 テクセル）を超える縮約は行わない

## [0.11.0] - 2026-11-08

アトラスの余白をなくし、色・発光・数値をテクスチャへ焼き込んで、できるだけ一つのアトラスにまとめる。

### Changed
- アトラスの配置: 元の解像度を落とさずに収まる最小の正方形を選び、一律の倍率を最大化（二分探索）したうえで残りの余白へ各領域を広げ、正方形全体を使う
  （MaxRects 方式。以前は 1/2 刻みの縮小と棚詰めで余白が多かった）
- オブジェクトの大きさに応じた配分: 使用面のワールド面積に対して解像度が過剰なテクスチャ（小物に大きなテクスチャ等）は、中央値のテクセル密度まで縮める
- 結合で空になった GameObject は既定で「非表示 + EditorOnly」（ビルドに含まれない。Prefab Variant でも可）。削除・そのまま残すも選べる

### Added
- 発光の焼き込み（Standard・lilToon）: 発光色 × 発光マップを焼き込み、HDR の強さはアトラス Material に移す。発光のない Material は黒として同じアトラスにまとめる
- 数値の焼き込み（Standard）: Metallic / Smoothness（Specular 版は Specular 色）を _MetallicGlossMap / _SpecGlossMap に焼き込み、値が違う Material もまとめる
- ないテクスチャの補完: ノーマルマップ等が一部の Material にしかなくても、ない領域をシェーダーの既定値（未割り当て時と同じ値）で埋めてまとめる
  （テクスチャの有無で処理が切り替わるもの・Poiyomi は対象外）
- チャンネルごとの必要十分な解像度: 元テクスチャの解像度を下回らない範囲で縮める（単色だけの発光マスク等は 1/8）
- Standard の Opaque で使われないメインテクスチャのアルファを捨て、JPEG・アルファなしの圧縮にする
- 実ブース（Standard 16 Material、63 オブジェクト）で確認: Material 16 → 1、Draw Calls（推定）72 → 8、テクスチャメモリ 10.7 → 11.3 MB

## [0.10.0] - 2026-11-07

### Added
- 空になった GameObject の削除（既定 ON、「メッシュ結合」の下で切り替え）: 結合で Renderer がなくなり Transform だけになった GameObject と、
  子がすべて消えて空になったグループを削除する。出力形式が Unpacked（独立 Prefab）の場合と NDMF のビルド時のみ
  （Prefab Variant では元の子を削除できないため、その旨をログに出す）。ほかのコンポーネントから参照されているもの・アニメーション対象・元から空のものは残す
- シーンへの配置（実行結果の「シーンの元オブジェクトを生成 Prefab に置き換える」、履歴の「シーンに配置」）: 対象がシーンのオブジェクトならそれを、
  FBX/Prefab なら開いているシーン内のそのインスタンスを生成 Prefab に置き換える。親・並び順・位置・回転・拡大・名前・アクティブ状態を引き継ぎ、
  ほかのオブジェクトからの参照は同じ位置のオブジェクトに付け替える（対応先がない参照は一覧で警告）。Ctrl+Z で元に戻せる。シーンは保存しない
- 実プロジェクト（Vket ブースの FBX、63 オブジェクト）で確認: Unpacked 出力で GameObject 64 → 9、Renderer 63 → 8

### Changed
- 構造検証は、削除した GameObject を除いて階層を照合する

## [0.9.0] - 2026-11-07

### Changed
- 再変換時の上書き（既定 ON。「出力と実行」で切り替え可）: 同じ対象・同じ種類（PC / Android / iOS）を再変換すると、新しく作らずに前回の出力を上書きする
  - Prefab は GUID と内部 ID を保ったまま内容を置き換えるため、シーンに置いた Prefab はそのまま更新され、インスタンスへの参照や上書き設定も残る
  - 前回の Material・Mesh・Texture 等は削除し、新しい生成物が同じ名前を使う。ほかのアセット（シーン・Prefab・Material 等）から参照されているものだけは残す
  - 前回の実行は履歴で「上書き済み」になる。途中で失敗した場合は前回の出力を元の場所・内容に戻す

### Fixed
- 同じ対象を 1 秒以内に再実行すると「出力フォルダが既に存在します（衝突）」で失敗する問題

## [0.8.1] - 2026-11-07

### Fixed
- メッシュ結合: Collider（BoxCollider・MeshCollider 等）が付いた静的オブジェクトが「Renderer 以外のコンポーネントを持つ」として
  すべて対象外になっていた問題（Collider はそのまま残して結合する。Rigidbody 等が付いたものは引き続き対象外）

### Changed
- 「SkinnedMesh も結合する」を詳細パラメータから「メッシュ結合」の行の下へ移動（アバターでは OFF だとほとんど結合されない旨を表示）
- 結合されなかった理由を具体的に表示（最も条件の近い Renderer と何が違うか、どのコンポーネントが原因か）

## [0.8.0] - 2026-11-07

### Added
- 近似 Material 統合（選択制）: 同じシェーダー・描画状態・キーワード・テクスチャで、数値や色だけが違う Material を
  「解析と予測の結果」に一覧表示（差分のプロパティと値つき）。チェックしたものだけを代表 Material（使用スロットが最も多いもの）に統合し、
  自動では統合しない。選択は設定に保存され、予測・NDMF のビルド時適用にも反映される。統合した Material はアトラス化の対象にもなる
- 実アバター（lilToon 使用）では 4 グループ・13 個が候補になり、すべて選ぶとマテリアル 49 → 34（予測）

### Changed
- 保存済みの設定にない（新しい版で追加された）機能は、プリセットの既定値で有効/無効を決める

## [0.7.0] - 2026-11-07

広く使われているビルド時ツールとの連携を追加（導入されていなければ VCC へのリポジトリ追加ボタンと導入手順を表示し、処理はスキップ）。

### Added
- AAO Trace And Optimize（Avatar Optimizer 1.8 系）: Avatar プリセットでは既定 ON。出力アバターのルートに付け、
  アップロード時に BlendShape・未使用オブジェクト/ボーン・PhysBone・Animator の最適化と SkinnedMesh の自動結合が行われる
- ビルド時ポリゴン削減（lilNDMFMeshSimplifier 0.3 系）: SkinnedMesh も BlendShape・ボーンを保ったまま削減。
  目標三角形数（既定は VRChat SDK の PC Poor 基準）に収まる品質を計算して各メッシュに設定し、Viseme・まぶたの顔メッシュは保護する（既定 OFF）
- ビルド後の評価（Avatar Rank タブ）: NDMF をアバターの複製に適用し、アップロード時と同じ構成で Rank・三角形数を評価する（原本・プロジェクトは変更しない）
- ファイルサイズの計測（Avatar Rank タブ）: NDMF 適用後の構成を AssetBundle にビルドし、アップロード時と同じ圧縮後/展開後のサイズを
  処理前・処理後で比較する（NDMF がない場合は Prefab のまま計測。一時ファイルは計測後に削除）
- 連携ツールの導入状況の表示（Avatar プリセット）

### 実アバターでの確認（Renard、AAO・lilNDMFMeshSimplifier・Modular Avatar 使用、SkinnedMesh 結合・ビルド時ポリゴン削減 ON）
- ビルド後: 三角形 193,483 → 89,695、SkinnedMesh 38 → 13、Renderer 80 → 27、マテリアルスロット 102 → 62
- サイズ（PC）: 圧縮後 21.77 MB → 20.26 MB、展開後 97.94 MB → 90.23 MB
- Rank は PhysBone・ライト等が VeryPoor のため VeryPoor のまま

## [0.6.0] - 2026-11-07

実アバター（Renderer 80・Material 49・約19万ポリゴン、Modular Avatar / AAO 使用）での試験運用で見つかった問題を修正。

### Added
- メインカラーのアトラスへの焼き込み: テクスチャのない Material（色だけの Material）もアトラス化でき、
  テクスチャを持つ Material もメインカラーの違いを焼き込んで統合できる（HDR の色・lilToon の色調補正/グラデーション・
  Poiyomi のテーマカラー/Ignore Alpha/Two Pass 版は対象外）
- アトラスのファイル形式: 不透明なカラー/マスクは JPEG（品質 95、PSNR 40dB 以上をテストで確認）、
  アルファあり・ノーマルマップは PNG。詳細パラメータで常に PNG にもできる

### Fixed
- Performance Rank が実プロジェクトで「算出不可」になる問題（SDK の閾値データが未初期化のまま評価 API を呼んでいた）
- Rank の評価で元のアバターの状態（VRC Constraint のグループ）が変わり、初回の実行が「依存アセットが変更されました」で止まる問題
  （評価は常に一時的な複製で行う）
- ルートの Animator パラメータ等のアニメーションで、ほぼすべての Renderer が結合対象外になる問題
  （表示切替・Transform・Renderer のアニメーションだけで判定）
- アトラス化でテクスチャメモリが増える問題
  （元ファイルの解像度ではなくインポート設定の最大サイズを使う。統合対象外の Material と共有するテクスチャを含む組は AUTO では統合しない）

## [0.5.1] - 2026-11-07

### Changed
- 出力先を指定した場合、Prefab だけでなく Material・Mesh・Texture などすべての生成物を指定フォルダへ出力
  （`Materials/` 等のサブフォルダで整理。復元用の Manifest・スナップショット・レポートは管理フォルダに残す。破棄時は出力先の生成物と作成したフォルダも削除）

### Fixed
- Draw Calls / SetPass Calls / Batches の推定が、ライティングを使うシェーダー（Standard・lilToon・Poiyomi 等）で常に 0 になっていた問題
  （Unity がパスの LightMode タグを大文字で返すため、大文字・小文字を区別せずに判定するよう修正）

## [0.5.0] - 2026-11-07

### Added
- 解析と予測（Dry Run）を自動で裏側で実行。対象・設定・シーン/アセットの変更を検知して再計算し、エディタの操作を妨げない
  （処理はフレームごとに時間を区切って進め、ファイルのハッシュ計算・アトラスの合成・ポリゴン削減の計算は別スレッドで実行）
- Prefab の出力先フォルダを指定可能に（依存アセットとスナップショットは管理フォルダに残す。破棄時は出力先の Prefab も削除）
- 設定をプロジェクトごとに保存

### Changed
- 「解析」「Dry Run」ボタンを廃止し、進捗をウィンドウ内に表示
- 未実装の機能と算出しない指標（CPU/GPU 時間）を UI に表示しない
- FBX などのモデル Prefab 内のオブジェクトも結合できるように修正
  （アニメーションの判定を、実際に使われている AnimationClip だけで行う。モデルに内蔵されているだけの Clip は判定に使わない）
- Unity 2022.3.22f1（VRChat の推奨版）でテスト

## [0.4.0] - 2026-11-07

### Changed
- 名称を UnityAssetsOptimizer に統一
  - パッケージ名: `com.chikumatateshina.unity-assets-optimizer` → `com.chikumatateshina.unityassetsoptimizer`（旧名は `legacyPackages` で置き換え）
  - 表示名・メニュー（`Tools > UnityAssetsOptimizer`）・名前空間/アセンブリ（`ChikumaTateshina.UnityAssetsOptimizer.*`）
  - 生成先 `Assets/UnityAssetsOptimizer/`、ログ `Library/UnityAssetsOptimizer/Logs/`、閾値 DB `Assets/UnityAssetsOptimizerSettings/RankRules/`
  - NDMF コンポーネント: `UnityAssetsOptimizer > Build Optimizer (NDMF)`
- 旧バージョンの生成物（`Assets/VAOGenerated/`）は自動では移動しません。不要なら削除してください

## [0.3.0] - 2026-11-07

### Added
- Poiyomi Toon 10.0.29（無料版）の Shader Adapter。全テクスチャのサンプル方法をソースから抽出して分類し、UV 選択・パン・Stochastic・全体 UV 設定・視差等の前提条件を検証
- Poiyomi（Opaque）→ VRChat/Mobile/Toon Standard の変換規則（非等価のため FORCE のみ）
- ロック済みシェーダー（Hidden/Locked/...）に対する専用のスキップ理由

### Changed
- テクスチャチャンネルごとの前提条件（そのテクスチャが設定されている場合のみ評価）に対応

## [0.2.0] - 2026-11-07

### Added
- lilToon 2.3.1 の Shader Adapter（メイン UV 系テクスチャのアトラス化。UV 非依存テクスチャの同一性・UV 関連機能の前提条件を検証）
- メッシュ結合（静的: 空間セル単位 / SkinnedMesh: 設定で有効化。アニメーション・参照・他コンポーネントを持つものは対象外）
- Static Batching 支援、GPU Instancing 支援（Material をコピーして有効化）、Lightmap UV 再生成
- 不要頂点属性の削除（シェーダーアダプターが使用属性を宣言している場合のみ）
- ポリゴン削減（QEM / half-edge collapse。誤差上限・境界・UV 継ぎ目・Material 境界を保護）、LOD 生成、MeshCollider 簡略化
- 元テクスチャの圧縮・解像度削減（コピーを生成して適用。元の Importer は変更しない）
- Prefab Variant 出力（Prefab Asset、または構造変更のないインスタンスの場合）
- VRChat SDK 公式評価 API による Avatar Performance Rank（PC / Android / iOS）
- Android / iOS 派生版の生成（SDK のシェーダー検証 API で判定、非等価変換は FORCE のみ）
- NDMF ビルド時適用（`Build Optimizer (NDMF)` コンポーネント、NDMF がある場合のみ有効）
- Draw Calls / SetPass / Batches のモデル推定、外観の参考比較（画像差分）
- テスト追加、GitHub Actions でのテスト（GameCI、Secret 未設定時はスキップ）

### Changed
- 各処理の直後にその処理の検証を行う順序に変更
- 構造検証は、各処理が登録した構造変更（結合で外した Renderer、LOD の追加など）だけを許容する

### Not implemented
- Mesh Compression（誤差を検証できないため）

## [0.1.0] - 2026-11-07

### Added
- 非破壊パイプライン、スナップショット、トランザクション、復元
- アトラス化（Unity 組み込みシェーダー）、同一 Material 統合、重複 Texture 検出、生成テクスチャの圧縮
- 指標の比較、JSON / Markdown レポート、Manual Override
