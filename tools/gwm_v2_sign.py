#!/usr/bin/env python3
"""
GWM request signer (gwm-auth-* Header) — My GWM App 1.3.0 (com.gwm.eu).

Aus dem dekompilierten Dart (blutter) rekonstruiert UND per Frida-Hook auf den
echten sha256-Input der laufenden App als Ground Truth bestätigt.

Ground-Truth-Sample (Frida-Hook auf Uri.encodeComponent, Input):
  GET/app-api/api/v1.0/complaintsComments/appInitConfig
  gwm-auth-appkey:1874226830
  gwm-auth-nonce:dc3c142844e37791
  gwm-auth-timestamp:1786133103101
  1eb6caa16ff203c96daf7f06309b8998

Live gegen eu-h5-gateway.gwmcloud.com verifiziert: appInitConfig (GET) -> 000000 SUCCESS.
"""
import hashlib
import json
import re
import time
import urllib.parse

# EUHost.baseHost() — field_b = appkey, field_f = secret
ENVS = {
    "prod":  ("1874226830", "1eb6caa16ff203c96daf7f06309b8998"),
    "test":  ("9436532908", "082768f81e4e58b5659aedb3ebe9be16"),
    "stage": ("9436532908", "082768f81e4e58b5659aedb3ebe9be16"),
    "dev":   ("7302056476", "91d09c725df6dde45e11a879583861f5"),
}
BASE = "https://eu-h5-gateway.gwmcloud.com"

# Dart Uri.encodeComponent: unreserved = A-Za-z0-9 - _ . ! ~ * ' ( )
_DART_UNRESERVED = "-_.!~*'()"


def _sha256_hex(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def make_nonce(ts_ms: int) -> str:
    # RequestSign.getRandomString(): sha256hex(str(ts_ms))[:16] (Random-Padding
    # nur falls <16; sha256hex ist 64 -> immer die ersten 16). Server verifiziert
    # den Header-nonce, die exakte Ableitung ist fuer die Verifikation egal.
    return _sha256_hex(str(ts_ms))[:16]


def _format_params(method: str, path: str, body) -> str:
    """getFormatParameter: SplayTreeMap.from(uri.queryParameters) + formatPostBody(),
    sortiert, pro Key  key + '=' + value.replaceAll(' ','')  konkateniert (kein Separator).
    formatPostBody bei JSON-Content-Type: {'json': jsonEncode(data)}."""
    params = {}
    q = urllib.parse.urlparse(path).query
    if q:
        for k, v in urllib.parse.parse_qsl(q, keep_blank_values=True):
            params[k] = v
    if body is not None:
        params["json"] = json.dumps(body, separators=(",", ":"), ensure_ascii=False)
    return "".join(f"{k}={str(params[k]).replace(' ', '')}" for k in sorted(params))


def sign(method: str, endpoint: str, body=None, env: str = "prod",
         ts_ms: int = None, nonce: str = None):
    """endpoint: relativer Pfad ab Host, z.B. '/app-api/api/v2.0/userAuth/loginWithPassword'.
    Liefert die gwm-auth-* Header (dict)."""
    appkey, secret = ENVS[env]
    ts_ms = ts_ms or int(time.time() * 1000)
    nonce = nonce or make_nonce(ts_ms)
    method = method.upper()

    path = endpoint  # RELATIVER Pfad (Host wird per replaceAll entfernt) — nicht die volle URL!
    pre = (f"gwm-auth-appkey:{appkey}"
           f"gwm-auth-nonce:{nonce}"
           f"gwm-auth-timestamp:{ts_ms}")
    params = _format_params(method, path, body)

    # getHeaders @0x7cff18: METHOD + PATH + PRE + PARAMS + SECRET
    s = method + path + pre + params + secret
    s = re.sub(r"\s|\t|\r|\n", "", s)          # replaceAll(RegExp(r'\s*|\t|\r|\n'), '')
    s = urllib.parse.quote(s, safe=_DART_UNRESERVED)  # Uri.encodeComponent
    return {
        "gwm-auth-appkey": appkey,
        "gwm-auth-nonce": nonce,
        "gwm-auth-timestamp": str(ts_ms),
        "gwm-auth-sign": _sha256_hex(s),
    }


# statische Standard-Header der App (ohne gwm-auth-*)
def base_headers(country="DE", lang="en", device_id="be53f73408f943158fef991ac9411787"):
    return {
        "rs": "2", "terminal": "GW_APP_GWM", "brand": "6", "appId": "1",
        "enterpriseId": "CC01", "channel": "APP", "cVer": "1.3.0",
        "regionCode": country, "country": country, "systemType": "1",
        "language": lang, "deviceId": device_id, "secVersion": "2.0",
    }


if __name__ == "__main__":
    print(json.dumps(sign("GET", "/app-api/api/v1.0/complaintsComments/appInitConfig"), indent=2))
