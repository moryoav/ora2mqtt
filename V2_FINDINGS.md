# GWM v2 API — Analyse-Stand (2026-08-07)

## Nachtrag 2026-08-09 — alles hier gemessen, nicht abgeleitet

- **`refreshToken` ist auf `v1.0` geblieben.** Auf `v2.0` antwortet der Host
  `HTTP 404` mit Body-Code `001 No message available`. Signiert wird er wie alles andere.
- **Der `accessToken`-Header muss beim Refresh mitgeschickt werden**, zusätzlich zum
  Body (`accessToken`, `refreshToken`, `deviceId`). Ohne Header: `Empty accessToken`.
- **`607198 System busy` heißt nur „Refresh abgelehnt".** Reproduziert mit einem
  absichtlich kaputten Refresh-Token gegen den *funktionierenden*, signierten v1.0-Endpunkt.
  Die Deutung vom 04.08. („v1-Token-Minting abgeschaltet") lässt sich aus diesem Code
  allein also **nicht** belegen — was am 04.08. wirklich fehlte, war die Signatur.
- **JWT**: `exp` in Sekunden, `iat + 86400` → Access-Token lebt **24 h**. Damit lässt sich
  der Ablauf lokal prüfen, statt vor jedem Zyklus einen API-Call zu verbrennen.
- **`appAuth/applyCertificate` darf mehrfach laufen.** Sieben Enrollments an einem Tag,
  das jeweils vorherige Zertifikat blieb weiter gültig (die Produktivinstanz lief mit dem
  alten unverändert durch). Laufzeit je Zertifikat: **1 Jahr**.

## Ausgangslage
ora2mqtt hängt seit **2026-08-04 10:12** in Endlosschleife. Token-Refresh gegen
`app-api/api/v1.0/userAuth/refreshToken` → `607198 System busy, please try later`.
Handy-App (My GWM 1.3.0) läuft weiter.

## Root Cause (belegt)
GWM hat am 04.08. (Wartungsfenster laut In-App-Meldung) das **Token-Minting der
v1.0-API abgeschaltet**. Die App ist auf **`app-api/api/v2.0`** umgezogen.

Falsifiziert (jeweils gemessen, nicht vermutet):
- **Header** — kompletter Satz der ORA-App 1.9.8 nachgebaut (`enterpriseId=CC01`,
  `appId`, `channel`, `cVer`, `regionCode`, UA, `iccid`) → keine Änderung
- **Client-Zertifikat** — ohne Cert *identische* Antworten. Gateway erzwingt kein mTLS.
  (ora2mqtt nutzt `gwm_general.cer`, byte-identisch mit APK-Asset. Die App nutzt es
  nur als Bootstrap solange `cmn.crt` fehlt — `dex1/sources/b2/b.java:88`.)
- **Body** — Müll-Refresh-Token = gleiche Antwort wie echter
- **Credentials** — Passwort akzeptiert, Code kommt an, Code-Prüfung besteht
  (falscher Code → sauber `308012 Incorrect verification code`)
- **Signing v1** — existiert in der ORA-App nicht

## v2 Auth-Surface (aus com.gwm.eu 1.3.0, Flutter, `lib/arm64-v8a/libapp.so`)

| Zweck | v1 (tot) | v2 (App heute) |
|---|---|---|
| Passwort-Login | `userAuth/loginAccount` | `userAuth/loginWithPassword` |
| Code-Login | `userAuth/loginWithSMS` | `userAuth/loginWithVerifyCode` |
| Code anfordern | `userAuth/getSMSCode` | `userAuth/getVerifyCode` |
| Code prüfen | — | `userAuth/checkVerifyCode` |
| Captcha | — | `userAuth/captcha/gen`, `userAuth/captcha/checkCaptcha` |

- **Host**: weiter `eu-h5-gateway.gwmcloud.com`. `eu-front-service` → 503 (ALB, nicht die API).
- **Client-Identität**: `terminal=GW_APP_GWM`, `brand=6`, `appId=1`, `enterpriseId=CC01`.
  (brand 3 / terminal-Mismatch → `551008 Illegal terminal, brand or enterpriseId`.)
- **Body-Validierung** greift: fehlende Felder → `002 参数错误, <feld>` bzw.
  `550002 <feld>`. Bekannte Felder:
  - `getVerifyCode`: `type` (@NotNull), `account`, `appType`, `countryCode`, `areaCode`
  - `loginWithPassword`: `account`, `password`, `deviceId`, `appType`, `isEncrypt`,
    `captchaId`, `captcha`
  - `captcha/gen`: `captchaType` (numerisch, 1/2)

## Der Blocker: v2-Requests sind signiert
Sobald das Schema stimmt, antwortet **jeder** v2-Call generisch
`550002 System busy,please try later` — auch `captcha/gen` (das keine Auth braucht).
Grund: die Flutter-App **signiert v2-Requests**. Header:

```
gwm-auth-appkey
gwm-auth-nonce
gwm-auth-timestamp
gwm-auth-sign        ← HMAC-SHA256 (pointycastle: HMac.withDigest + SHA256Digest)
```

`appkey`, HMAC-Secret und String-to-Sign-Reihenfolge liegen im **AOT-kompilierten
Dart** (`libapp.so`), nicht in Assets/Config. Statisch nicht greifbar.

## Offener nächster Schritt (für vollständigen PoC nötig)
Signatur-Secret dynamisch abgreifen — eins von:
1. **Frida** auf Emulator/gerootetem Gerät: HMAC-Funktion in `libapp.so` hooken,
   `(appkey, secret, message)` dumpen. Danach in C# nachbauen (System.Security.Cryptography).
2. **mitmproxy** am echten Handy (hat funktionierende App) + Pinning-Bypass:
   echte signierte Requests capturen, Sign-Format aus mehreren Samples ableiten.

## Werkzeuge (in diesem Repo, alles hinter Env-Flags, Default-Verhalten unverändert)
- `probe --path <p> --body <json> [--token]` — Raw-Request-Verb
- `login -e <mail> -p <pw>` / `login -e <mail> --code <code>` — nicht-interaktiv
- `GWM_H5_BASE` — Host/Base-Override
- `GWM_HEADER_*`, `GWM_EXTRA_HEADERS` — beliebige Header
- `GWM_NO_CLIENT_CERT=1` — Cert weglassen
- `GWM_REFRESH_PATH` — Refresh-Route umbiegen

## Betriebs-Fixes (unabhängig vom v2-Thema, bereits committed)
- Token bei Refresh-Fehler wiederherstellen (kein `Empty accessToken`-Dauerloop)
- Exponentielles Backoff 10s→15min statt fixe 10s (verhinderte die 21k Requests/2 Tage)

## Update 2026-08-07 — statische RE-Umgebung + blutter

**Emulator hier nicht möglich**: LXC hat **kein `/dev/kvm`** (Host-AMD-V nicht durchgereicht),
2 vCPU, App ist arm64-only. Android-Emulator würde unbenutzbar kriechen.

**blutter** (Dart-AOT-Decompiler) aufgesetzt (`~/apk-analyse/blutter`, Deps installiert).
Scheitert an **Bangcle-Schutz**: `libapp.so` hat zerschossene Section-Header und **keine
Symboltabelle** (`.dynsym`/`.symtab`/`DT_SYMTAB` alle weg). Die Symbol-*Namen* überleben
im String-Bereich (~0x334180), die `Elf64_Sym`-Einträge mit den Adressen sind entfernt.

**Aber: der Dart-Code ist NICHT verschlüsselt** (Entropie 6.5 bit/B, High-Byte-Histogramm
zeigt ARM64-Opcodes `0xf9`/`0x94`(BL)/`0x8b`(ADD)). Statische RE ist also machbar.

**Snapshot-Blob-Adressen rekonstruiert** (für ELF-Reparatur / manuelle .dynsym):
```
_kDartVmSnapshotData           0x214     (beginnt mit Versionshash)
_kDartIsolateSnapshotData      0x3f94    size ~0x330364
_kDartVmSnapshotInstructions   0x340000  size ~0x16560
_kDartIsolateSnapshotInstr     0x357000  (page-aligned, ARM64 f85ff009…)  size ~0x56fc90
```
Flutter 3.3.0 stable, Snapshot-Hash `ee1eb666c76a5cb7746faf39d0b97547`.

**Offener nächster Schritt** — eins von:
1. ELF von `libapp.so` reparieren (obige 4 Symbole in neue `.dynsym` schreiben, e_shoff
   fixen) → blutter füttern. ⚠️ blutter klont+baut das komplette Dart-3.3.0-SDK →
   mehrere GB + Stunden auf 2 Kernen. **Disk-Fill-Risiko auf dieser 26G-LXC** → nur mit
   dediziertem Storage oder auf anderer Maschine.
2. **Frida dynamisch**: braucht laufendes Android mit KVM → VM auf dem Proxmox-Host
   (hat AMD-V), nicht dieser LXC. HMAC-Funktion in libapp.so hooken → `(appkey, secret,
   message)` direkt dumpen. Robuster, kein Dart-Build, kein ELF-Fummeln.
