# STD-T99 Receiver for SDR#: 録音ファイル

同じ名前（`T99-日時-ID`）で4つのファイルを作ります。既存のファイルは上書きしません。
受信中に選局（周波数）・IQレート・ソースが変わると、受信器を作り直すため新しい名前のファイル組に切り替わります（`t` も0から数え直します）。

## .burst

std-t98系・std-t99-receiverと同じレコード形式。ヘッダーなし、1レコード = 1フレーム（80 ms）= 41 byte。

`FF (48 + 9-byte payload) × 4`

- 書くのはRICHが「TCH / TCH」（F=10, O=11）のフレームだけです。FACCHが半分を使うフレームと同期バースト・終話フレームは書きません（`.jsonl`にはすべて残ります）。
- 72-bitは規格書第5章のTCHブロックを送信bit順のまま保存します（TOKUDER: 音声48 + 誤り訂正24 bit / 20 ms）。
- 隣接レコードがRF上で連続している保証はありません。`.jsonl`の`burst`欄（レコード番号）と`t`で対応を取れます。

## .llr

1レコード288 byte = 4 × 72個のsigned int8、`.burst`と同じbit順。正=1、負=0、絶対値=信頼度。レコード数は`.burst`と同じです。

## .jsonl

受信したフレームごとに1行のJSON。std-t99-receiverの`.jsonl`（暫定 v0）に`utc`を加えたものです。

| 欄 | 内容 |
|---|---|
| `utc` | 受信したPCの時刻（UTC） |
| `t`, `sample` | このファイル組の受信開始からの秒、受信器の100 kHzサンプル番号（同期語の先頭） |
| `sync`, `sse` | `ok`（同期語あり）/`flywheel`（予測位置で取得）、同期語の二乗誤差 |
| `kind` | `SB`（同期バースト）/`SC`（通信チャネル） |
| `rich` | F, M, O, D, パリティ |
| `sacch` | CRC、ビタビのパス誤り数、構造識別、グループ番号、18-bit単位 |
| `halves` | 前半・後半の種別（PICH/FACCH/TCH）。制御はCRCと80-bitメッセージ、TCHは72-bit × 2のhex |
| `messages` | このフレームで復号できたメッセージ。SACCHのスーパーフレームは4単位揃ったフレームに出ます |
| `name` | CRC-32が一致した発信者名（UTF-8） |
| `burst` | `.burst`のレコード番号、書かなかったフレームは`null` |
| `raw` | デホワイト後の SW / RICH / SACCH / 前半 / 後半 のhex |

`messages[].ch`は`PICH1`・`PICH2`・`SACCH`・`SACCH(SF)`・`FACCH1`・`FACCH2`。

| type | 主な欄 |
|---|---|
| Identification（PICH1） | `extension`（20 bit）, `caller`, `called` |
| Voice / End call / Remote TX request / Location request / Device ID request | `comm`, `caller`, `called`, `call_control`, `voice_mode` |
| GPS data 1、およびPICH2（型によらず位置の配置） | `lat`, `lon`（度、10進）, `lat_raw`/`lon_raw`（ddmmmmmm / dddmmmmmm）, `alt_m`, `gps_valid` |
| GPS data 2 | `gps_time`（JST）, `time_raw`（hhmmss）, `date_raw`（ddmmyy）, `speed_01kn`, `direction_deg` |
| Idle | `maker` |
| Caller name | `name_part`（1/2）, `name_bytes`（Shift_JIS hex）, `name_check`（2/2のCRC-32） |

機器IDは「メーカー識別3桁-個別ID 7桁」。宛先`000-0000000`はグループ/一斉呼出です。

`.jsonl`を`rf.jsonl`、`.llr`を`rf.llr`として同じフォルダーに置くと、tokuder-neurodecの`tokuder2wav`（受信ログのディレクトリ入力）で読めます。

## .wav

8 kHz、モノラル、PCM16。復号したTOKUDER音声を受信順に連結します（音声フレームの最初から終話の直前まで。途中のFACCHや受信できなかったフレームは直前のフレームで補います）。待受（選択呼出）の設定によらず、すべての通話の音声を書きます。
