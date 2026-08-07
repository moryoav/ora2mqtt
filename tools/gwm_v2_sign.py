#!/usr/bin/env python3
"""
GWM v2 request signer — 1:1-Reimplementierung aus dem dekompilierten Dart-Code
der My-GWM-App 1.3.0 (com.gwm.eu), gw_flutter_network/src/config/gw_header_config.dart.

Referenz-Adressen (blutter-asm) in Kommentaren. Siehe ../V2_SIGN.md.

⚠️ Noch NICHT server-validiert: gegen die Live-Endpunkte liefert der Server mit
dieser Signatur weiterhin 550002, identisch zu no-sign. Der letzte Byte-genaue
Abgleich braucht einen Frida-Hook auf den echten sha256-Input der laufenden App.
"""
import hashlib
import json
import random
import re
import time
import urllib.parse

# EUHost.baseHost() — field_b = appkey, field_f = secret, field_7 = baseUrl
ENVS = {
    "prod":  ("https://eu-h5-gateway.gwmcloud.com",       "1874226830", "1eb6caa16ff203c96daf7f06309b8998"),
    "test":  ("https://eu-h5-gateway-test.gwmcloud.com",  "9436532908", "082768f81e4e58b5659aedb3ebe9be16"),
    "stage": ("https://eu-h5-gateway-stage.gwmcloud.com", "9436532908", "082768f81e4e58b5659aedb3ebe9be16"),
    "dev":   ("https://eu-h5-gateway-dev.gwmcloud.com",   "7302056476", "91d09c725df6dde45e11a879583861f5"),
}

# Dart Uri.encodeComponent: unreserved = A-Za-z0-9 - _ . ! ~ * ' ( )
_DART_UNRESERVED = "-_.!~*'()"


def _sha256_hex(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def current_timestamp_ms() -> int:
    # 0x7cfcd0 _getCurrentMicros(); /1000  ->  ms
    return int(time.time() * 1000)


def get_random_string(ts_ms: int) -> str:
    """RequestSign.getRandomString() @0x7d1ce4:
    sha256hex(str(ts_ms)) -> falls <16 mit Random+'0000000000000000' auffuellen ->
    substring(...). Ergebnis 16 Zeichen. (Server verifiziert den Header-nonce, die
    exakte Ableitung ist fuer die Verifikation egal.)"""
    h = _sha256_hex(str(ts_ms))
    if len(h) < 16:
        h = h + str(random.randint(0, 999)) + "0000000000000000"
    return h[:16]


def _format_params(method: str, path_and_query: str, body) -> str:
    """getFormatParameter @0x7d0c60:
      map = SplayTreeMap.from(uri.queryParameters)
      map.addAll(formatPostBody())          # bei JSON: {"json": jsonEncode(data)}
      return formatGETParameter(map)        # sortiert, "key=" + value.replaceAll(" ","")
    """
    params = {}
    # uri.queryParameters
    q = urllib.parse.urlparse(path_and_query).query
    if q:
        for k, v in urllib.parse.parse_qsl(q, keep_blank_values=True):
            params[k] = v
    # formatPostBody @0x7d1034: JSON-Content-Type -> {"json": jsonEncode(data)}
    if body is not None:
        params["json"] = json.dumps(body, separators=(",", ":"), ensure_ascii=False)
    # formatGETParameter @0x7d0d58: sortiert, key=value konkateniert, Werte ohne Spaces
    out = ""
    for k in sorted(params):
        out += f"{k}={str(params[k]).replace(' ', '')}"
    return out


def sign_request(env: str, method: str, endpoint: str, body=None,
                 ts_ms: int = None, nonce: str = None):
    """Liefert (headers_dict) mit gwm-auth-*.
    endpoint: Pfad relativ zu /app-api/api/v2.0/, z.B. 'userAuth/captcha/gen'.
    """
    base, appkey, secret = ENVS[env]
    ts_ms = ts_ms or current_timestamp_ms()
    nonce = nonce or get_random_string(ts_ms)

    full_url = f"{base}/app-api/api/v2.0/{endpoint}"

    # getHeaders @0x7cfe10-0x7cff3c: field_7 + PATH + PRE + PARAMS + field_f
    method_s = method.upper()                                     # field_7 (RequestOptions.method)
    path = full_url                                              # H5ShareUrl/'/eu-recommend' matchen nicht
    pre = (f"gwm-auth-appkey:{appkey}"
           f"gwm-auth-nonce:{nonce}"
           f"gwm-auth-timestamp:{ts_ms}")
    params = _format_params(method_s, full_url, body)

    sign_string = method_s + path + pre + params + secret
    sign_string = re.sub(r"\s|\t|\r|\n", "", sign_string)        # replaceAll(\s*|\t|\r|\n,"")
    sign_string = urllib.parse.quote(sign_string, safe=_DART_UNRESERVED)  # Uri.encodeComponent
    sign = _sha256_hex(sign_string)

    return {
        "gwm-auth-appkey": appkey,
        "gwm-auth-nonce": nonce,
        "gwm-auth-timestamp": str(ts_ms),
        "gwm-auth-sign": sign,
    }


if __name__ == "__main__":
    h = sign_request("prod", "POST", "userAuth/captcha/gen", {"captchaType": 1})
    print(json.dumps(h, indent=2))
