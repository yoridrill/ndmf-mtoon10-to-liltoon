# NDMF MToon1.0 → lilToon Converter

MToon 1.0 / 互換 MToon マテリアルを lilToon へ変換するための Unity + NDMF 向けツールです。

## 概要

このツールは以下を目的としています。

- Build 時に NDMF フェーズで自動変換
- Editor 上での非破壊 Preview
- 髪周りのルック調整（マテリアル結合 / atlas）
- 変換結果のレポート表示（件数・warning・unsupported）

## 前提

- Unity Editor
- NDMF
- lilToon

## 使い方

1. 対象アバターの Renderer を持つオブジェクトに `MToonLilToonComponent` を追加
2. `lilToon Shader` を設定
3. `Scan Materials` で候補収集
4. 必要に応じて 髪周りのルック調整 の対象を調整
5. `Preview` で変換後を確認（元オブジェクトは非破壊）
6. Build 時は NDMF plugin が自動適用

## 髪周りのルック調整

- `HAIR`（大小無視）を含む名前のマテリアルを候補化し、Cull Mode 多数派かつ描画系統多数派と一致するもののみを初期選択
- 描画系統の多数派判定は `Transparent` と `Opaque+Cutout` の2系統で行い、`Opaque` は `Cutout` 側として扱う
- 手動でチェックした項目は render type 混在でもそのまま merge 対象
- merge 後マテリアルの render type は、`Transparent` が `Opaque+Cutout` より多い場合のみ `Transparent`、それ以外は `Cutout`
- オプション: 眉ステンシル / FakeShadow（Inspector で ON/OFF、向き・オフセットを調整可能）
- 眉ステンシル対象マテリアルは名前ベースで自動選択され、Inspector で手動変更可能
- FakeShadow は「Enable FakeShadow」が ON かつ「チェックされた結合対象」がある場合に、結合後の髪マテリアルへ適用
- FakeShadow 用の顔マテリアルは名前ベースで自動選択され、Inspector で手動変更可能
- チェックが1つだけの場合は atlas 化せず、元のUV/テクスチャ設定を維持
- atlas 対象: main / shade / emission / normal / outline
- UV は atlas rect に再配置し、サブメッシュ統合を実施

atlas サイズ方針:

- 21 枚以下: 原寸寄り（7×3 想定）
- 22 枚以上: 軽い縮小を許可し 32 枚（8×4）を優先

## Render Queue

- Opaque: `Geometry`
- Cutout: `AlphaTest`
- Transparent: **必ず `2460` 開始**
- Transparent は **元の MToon transparent 同士の相対順序のみ** を保持し、`2460 + rank` で**連番に詰めて再採番**
- VRoid などで `4000` 等の不適切な queue が入っていても、その絶対値は信用せず、lilToon/VRChat アバター運用向けに再構成
- この方針は **VRChat アバターで Focus が外れてボケる問題を避けるための仕様** であり、Unity 一般慣例より優先

### Render Queue ポリシー（変更禁止レベル）

- 本ツールは「Unity の一般的な queue 運用」ではなく、**VRChat でアバターに lilToon を使う実運用**を優先する。
- そのため Transparent queue は 2500/3000 帯へ分割しない。**2460 帯で密に採番**する。
- 将来の変更でも、このルールを崩す場合は「VRChat での実機検証結果」と「既存アバターへの影響評価」を必須とする。

## Inspector UI

- Preview ボタン（有効中は緑）
- Scan ボタン
- Hair 選択 UI
- 髪周りのルック調整オプション（眉ステンシル / FakeShadow）
- FakeShadow パラメータ（向き / オフセット）
- レポート表示
- 日本語 / 英語切り替え
- lilToon ユーザー設定ラベル（影を受け取る、境界の色、境界の幅、距離フェード、逆光ライト）

## 注意

- 実行環境外では実機見た目確認はできません
- 特殊なメッシュ構成では atlas/UV 結合結果の追加調整が必要な場合があります
