# Protocol and validation notes

This is a concise developer reference for the implementation. The original companion APK and JADX export were used during protocol research, but are intentionally not included in this repository. Protocol claims below are based on that inspection and on the hardware runs noted here.

## Tested hardware and BLE transport

- Tested keyboard: **ZOOM TKL DYNA**, firmware `0.0.14`, product generation `3`.
- Wuque service: `1f40eaf8-aab4-14a3-f1ba-f61f35cddbaa`.
- Command write: `1f400001-aab4-14a3-f1ba-f61f35cddbaa`.
- Notification characteristics: `1f400002-aab4-14a3-f1ba-f61f35cddbaa` and `1f400004-aab4-14a3-f1ba-f61f35cddbaa`.
- Display-data write: `1f400003-aab4-14a3-f1ba-f61f35cddbaa`.
- A successful connection negotiated `GattSession.MaxPduSize=247`, allowing 244-byte characteristic values (ATT PDU size minus 3).
- Display data uses **Write Without Response**. Await each Windows GATT write call before submitting the next packet; OS write success alone is not proof that the keyboard accepted the full image.

## Frame format and setup

Normal command frame: `88 00 00 | payload length (4-byte big-endian) | XOR(payload) | payload`. The product ID is `88`; XOR is the bytewise XOR of the payload bytes.

Transfer setup is metadata, dial start, then readiness polling:

1. Static image metadata uses body `09 03 01 00 0B 00 00 FF FD | image size (4-byte big-endian) | FF FF FF`.
2. GIF metadata is a distinct generation-3/type-2 body and container; see the GIF section below.
3. Dial start body: `08 03 00 FF FF FF 00 FF FF FF`; expected response status is `02`.
4. Poll status with body `00 13 00` until keyboard state byte 13 is `03`.

## Image and packet format

- Dial image is 320×172 pixels, RGB565, high byte first. Each frame is 110,080 bytes before GIF container overhead.
- Static images are center-cropped or fit into the target, JPEG-encoded at quality 70, decoded, then converted to RGB565. GIF frames convert directly to RGB565.
- Data is split into 4,096-byte blocks. Continuation packets carry up to 243 data bytes plus one XOR byte. The first packet is a special 244-byte header packet carrying 218 image bytes.
- ACK status `05` was observed after data blocks during a successful run. Final `02` is the observed success response. Status `06` appeared in deliberately interrupted tests; do not treat successful Windows writes or progress ACKs alone as completion.

## GIF format and tested result

- GIF transfer requires product generation 3 and is limited to 50 prepared frames by the companion app behavior.
- The source GIF's per-frame delays are discarded. Firmware receives a uniform speed code; app preview is calibrated to the observed keyboard playback rate.
- Payload structure: 28-byte header, 324 bytes of `FF` padding, four little-endian 32-bit section offsets, three 28-byte settings records, one little-endian 32-bit start offset per frame, concatenated RGB565 frames, and trailer `FC FF 00 00`.
- A 50-frame GIF produced 5,504,656 bytes, 1,344 blocks, and 22,847 packets. A hardware run completed successfully in **5 minutes 12 seconds**: 1,343 status-`05` ACKs and final status `02`.
- Current sending policy is Write Without Response, ACK-gated per block, with a 225 ms minimum interval between block starts (maximum about 4.4 blocks/second) and a 5-second per-block ACK timeout. Keep the keyboard connected until the final `02` arrives.

## Other verified behavior

- The version query is one non-mutating command with payload `00 01 00`. Version/product-generation parsing is guarded by notification shape checks.
- Support bundles are local JSON exports; they may contain Bluetooth addresses, nearby-device observations, Windows transport details, log events, and a selected image path. Review before sharing.
- Static-image transfer was validated on hardware and displayed correctly. The 50-frame GIF transfer above also reached final protocol success.
