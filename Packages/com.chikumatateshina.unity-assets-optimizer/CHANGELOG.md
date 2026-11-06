# Changelog

## [0.1.0] - 2026-11-07

### Added
- 非破壊パイプライン（Resolve → Snapshot → Analyze → Plan → Dry Run → Approve → Generate → Validate → Commit → Post-measure）
- 原本スナップショット（内容ハッシュ・GUID・.meta・階層構成）、トランザクション（ステージング＋ロールバック）、原本整合性検証
- 履歴/復元: 生成物の破棄、スナップショットからの原本書き戻し
- テクスチャアトラス化（Unity 組み込みシェーダー対応、決定的パッカー、頂点分離・サブメッシュ統合）
- 同一 Material 統合、重複 Texture 検出
- 生成テクスチャの圧縮（PC / Android・iOS ASTC）・解像度制限
- 指標の処理前/予測/処理後比較（確定計算・推定・取得不可の区分表示）
- JSON / Markdown レポート
- Manual Override（OFF/AUTO/FORCE、個別承認・監査ログ）
- Avatar Rank / Mobile Conversion の枠組み（未検証データでは算出・変換しない）
