# 由来と規格対応

## 規格

ARIB STD-T99 v4.1（英語版）Annex 2 デジタル小電力コミュニティ無線。

- 物理層・チャネル符号化・メッセージの解釈は std-t99-receiver のもの（同リポジトリの PROVENANCE.md）をそのまま使います。
- このプラグインで加えた受信側の機能:
  - 選択呼出の着信判定（4.5、表 Annex 2-4-14）: 待受（全受信/個別/グループ）、自局機器ID、グループ番号で音声の再生を決めます。
    受信接続試験（表 Annex 2-7-6）の組み合わせを `src/T99.Smoke` で試験しています。std-t99-receiver は判定を実装していません。
  - チャネル表（表 Annex 2-3-1）からの選局チャネル表示、CH18（呼出チャネル、4.7）の表示。
  - 通話種別（個別/グループ/一斉 = グループ番号0）、緊急通信1・2・緊急応答（呼制御付加情報）、位置要求・遠隔送信要求・機器ID要求の表示。
- 受信に関係しない項目・できない項目は実装していません: 送信、応答（位置・機器ID・遠隔送信）、キャリアセンス、2周波待受（4.7.2。SDR#の選局IQは1チャネル分のため）。

## コード

| 部分 | 由来 |
|---|---|
| `third_party/std_t99_receiver` | [std-t99-receiver](https://github.com/tallcat4/std-t99-receiver) `a55adb6839e86462c38591438454e3b4cea38fe7` の `src/`（protocol, messages, session, receiver, frame_sync, center_channelizer, core types）。変更点は下記 |
| `third_party/spear` | 同リポジトリの `vendor/spear`（SPEAR `d1611fd408939ad238372d58828639e8fde28400` の FIR・PFB・RRC・Gardnerシンボル同期）。変更なし |
| `tests/spec_vectors.hpp` | 同リポジトリの `tests/native/spec_vectors.hpp`（規格書7.7.2の試験信号53フレーム） |
| `tests/spec_iq.cpp` | 同リポジトリの `tests/native/test_main.cpp` の試験用変調器と送信の組み立て |
| `native/t99.cpp` | 入力の変換（SDR#の選局IQ → 100 kHz）は [std-t98-receiver-sharp](https://github.com/tallcat4/std-t98-receiver-sharp) `defaf2a6bcdf0feade404f238ebbf11e607084f1` の `native/t98.cpp` のリサンプラー。C ABI・フレームキュー・アイパターンの受け渡しも同じ方式 |
| `native/tokuder.*` | [tokuder-neurodec](https://github.com/tallcat4/tokuder-neurodec) `fcd92e982e649e78d48164104b1ab7c5b53fba8e` の `fec.py`（誤り訂正）と `model.py`・`frontends.py`・`istft.py`・`excitation.py`（`tokuder48_causal_v1` の推論）をC++へ移植 |
| `src/SDRSharp.T99/japan-map.bin` | [Natural Earth](https://www.naturalearthdata.com) 1:10m（パブリックドメイン）のGeoJSON（[nvkelso/natural-earth-vector](https://github.com/nvkelso/natural-earth-vector) `ca96624a56bd078437bca8184e78163e5039ad19`）から `tools/make-map.py` で日本の範囲を切り出したもの。都道府県（admin_1、iso_a2=JP）、市街地、湖、河川、鉄道、道路、都市（populated_places、日本語名 name_ja）。都道府県ポリゴンの内側（約3 km以内）に頂点がある地物だけを残し、小さいズーム用に Douglas-Peucker（0.005度）で簡略化した形も持ちます |
| `src/SDRSharp.T99` | std-t98-receiver-sharp の `src/SDRSharp.T98`（プラグイン・ワーカー・パネル・アイパターン・音声出力・WAV）を基に作り直し。秘話関連は持ちません |

### std-t99-receiver からの変更

- `receiver.*`: アイパターン用のトレース出力（`set_eye_handler`）を追加。SPEAR `apps/std_t98/receiver.cpp` の eye tap と同じ計算です。
- `receiver.cpp` の捕捉経路: CRC-12で確定したフレームのちょうど1フレーム後（±1シンボル）なら、同期語・有効なRICH・SACCHのCRCで採用します。
  同期バーストの直後の音声フレームはCRC-12を持たず、シンボル同期の引き込み中に落ちていました（元の1.2 Msps経路でも試験信号1-1・6-1で1フレームずつ）。
  この追従はシンボル経路が同期中でも続けます（両経路が同じフレームを出したときは、従来の時刻による重複除去で1つになります）。
- `receiver.cpp` の `emit()`: CRCが1つも通らないフレームは、直前2秒以内にCRCが通ったフレームの80 ms格子（±半シンボル）に乗っていなければ出しません。
  毎フレーム同じ音声データ（試験信号の1031 Hzトーン。無音でも起こりうる）では、データ中の同期語に似た並びが192シンボルごとに現れ、
  シンボル経路が同期語だけでそこへロックし、RICHのパリティが偶然通るフレームを出し続けていました（試験信号6-1、開始位置によって発生）。

### 入力の変換

- 受信器の入力は100 kHz（PFB 8チャネル × 6.25 kHz の2倍）。50 kHz（初段の間引き1）では試験信号のシンボル経路でCRCがほとんど通らなかったため、間引き2にしています。
- 周波数補正（AFC）は行いません。受信器が同期語の平均（DC）でフレームごとに搬送波のずれを差し引くためで、入力を回転させるとフレームの途中でDCが動き、試験信号でCRCを落としました。
  表示する「ずれ」は同期中に受信器が測った値（弁別器出力の平均）です。

### TOKUDER デコーダー

- 誤り訂正は `fec.soft_decode` と同じ4096符号語の最尤復号、`info48` と同じ情報集合です。
- ネットワークは全層が因果的なので、状態（各畳み込みの履歴、励振の位相・窓、iSTFTの重ね合わせ）を持って1フレーム（20 ms）ずつ計算します。
  フロントエンドの1フレーム先読みと、iSTFTの重ね合わせ（256サンプル）のぶんだけ出力が遅れます。末尾の扱い（最後のフレームの先読み・生成器の1ステップの複製）も `model.py` と同じです。
- `tools/check-native-model.py` で同梱サンプル（VCTK、実機受信）について PyTorch 実装と比較します: FECは完全一致、フロントエンド出力の差は2e-5以下、
  同じ励振雑音を与えた波形のSNRは40 dB超（位相の累積を参照側もfloat64にした場合）。
- 消失（FACCHの半分、受信できなかったフレーム）は直前のフレームで置き換えます（`frames.to_info48` と同じ）。480 msを超える途切れ、同期バースト、終話で音声の流れを区切ります。
  終話のフレーム（FACCH/FACCH）は音声に加えません。スロット番号は各送信の最初の音声フレームからの相対で数えます。
  `tools/synthetic-check.py` は試験信号の開始位置を80 msの格子に対して20通りずらし、全フレームと音声サンプル数（音声フレーム数 × 640）を確かめます。
- 重み: `models/tokuder48_causal_v1.bin` は `models/tokuder48_causal_v1.pt`（SHA256 `4e869d1b52527bcbf9aec92c814263f4f41ab4a9afb0744223de1231ee5f1199`）を
  `tools/export-model.py` で並べ直しただけのFP32です（SHA256 `7401807cb390c202a0ffa44411e953b885dba702e0847471a018ed37bd2aa13b`）。量子化・再学習はしていません。
