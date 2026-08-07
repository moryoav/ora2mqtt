# GWM v2 Request-Signatur — aus asm rekonstruiert (2026-08-07)

Quelle: My GWM 1.3.0 (`com.gwm.eu`), Flutter, `libapp.so` via blutter.
`gw_flutter_network/src/config/gw_header_config.dart` (`RequestSign.getHeaders`,
`getFormatParameter`, `formatPostBody`, `formatGETParameter`, `getRandomString`) +
`gw_host_config.dart` (`EUHost.baseHost` / `H5ShareUrl` / `H5BaseUrl`).

## Algorithmus (plain SHA-256, kein HMAC)
```
sign = sha256hex( Uri.encodeComponent( stripWhitespace( signString ) ) )

signString = METHOD + PATH + PRE + PARAMS + SECRET      # Reihenfolge!
```
Reihenfolge verifiziert per `+`-Stack-Konvention (Receiver im höheren Slot; Beleg:
`uri.replaceAll(H5ShareUrl,"")`). Aufbau in `getHeaders` @0x7cfe10–0x7cff3c:
`field_7 + PATH + PRE + PARAMS + field_f`.

- **METHOD** = `RequestOptions.field_7` = HTTP-Methode, Default `"GET"` (Konstruktor
  `0x549e50 r0="GET" → StoreField field_7`). Für POST-Calls = `"POST"`.
- **PATH** = `uri.toString()` mit `H5ShareUrl()` und `"/eu-recommend"` per replaceAll
  entfernt. `H5ShareUrl = H5BaseUrl + "/wey-recommend"`, `H5BaseUrl` enthält
  `/eu-global-service` → matcht den API-Pfad nicht → **PATH = volle URL**
  `https://eu-h5-gateway.gwmcloud.com/app-api/api/v2.0/<endpoint>`.
- **PRE** = `"gwm-auth-appkey:"+appkey + "gwm-auth-nonce:"+nonce + "gwm-auth-timestamp:"+ts`
  (interpolate @0x7cfe14 aus `["gwm","-auth-appkey:",appkey,"gwm","-auth-nonce:",nonce,
  "gwm","-auth-timestamp:",ts]`).
- **PARAMS** = `getFormatParameter` = merge(`uri.queryParameters`, `formatPostBody()`) in
  `SplayTreeMap` (sortiert), dann pro Key `key + "=" + value.replaceAll(" ","")` konkateniert
  (kein Separator, Start `""`). **`formatPostBody` bei JSON-Content-Type** → nicht flach,
  sondern `{"json": jsonEncode(data)}` (@0x7d115c/0x7d118c). D.h. bei JSON-POST:
  `PARAMS = "json=" + <kompakter-json-body>` (jsonEncode = kompakt, keine Spaces).
- **SECRET** = `baseHost().field_f`.

`stripWhitespace` = `replaceAll(RegExp(r"\s*|\t|\r|\n"), "")` über den **ganzen** String.
`Uri.encodeComponent` (unreserved: `A-Za-z0-9-_.!~*'()`), auf den ganzen String, VOR sha256.

nonce = `getRandomString()` = `substring(sha256hex(currentMicros/1000) + random-padding)`
— enthält Random, also NICHT aus ts nachrechenbar. Server nutzt den **Header**-nonce →
beliebiger nonce ok, solange Header == signString-nonce. ts = `currentMicros/1000` (ms).

## Konstanten je Umgebung (appkey = field_b, secret = field_f)
| Host | appkey | secret |
|---|---|---|
| **prod** `eu-h5-gateway.gwmcloud.com` | **1874226830** | **1eb6caa16ff203c96daf7f06309b8998** |
| test/stage | 9436532908 | 082768f81e4e58b5659aedb3ebe9be16 |
| dev | 7302056476 | 91d09c725df6dde45e11a879583861f5 |

## Header (v2)
`gwm-auth-appkey/nonce/timestamp/sign` + terminal=GW_APP_GWM, brand=6, appId=1,
enterpriseId=CC01, channel=APP, cVer, regionCode, systemType, rs=2, country, language,
deviceId, secVersion=2.0, gwId, iccid, ip, gray-version, communityBrand.

## ⚠️ Status: NICHT server-validiert
Live gegen `captcha/gen`/`loginWithPassword`/`getVerifyCode`: mit dieser Formel weiterhin
`550002 System busy`, **identisch zu no-sign / falschem Sign**. Der Server discriminiert
also nicht sichtbar → entweder Rest-Diskrepanz im Encoding, die nur ein Frida-Hook des
echten sha256-Inputs auflöst (App crasht unter x86-Translation wg. SecNeo-Packer), oder
`550002` ist für diese Endpunkte gar nicht der Sign-Gate. Referenz-Signer:
`~/…/scratchpad/sign_final.py`. Nächster definitiver Schritt: arm64-Voll-Emulation +
Frida-Hook auf `Digest.toString`-Input, um die exakte Byte-Sequenz zu sehen.
