# ora2mqtt

Publishes the status of a GWM ORA (Funky Cat / ORA 03) to MQTT and sends remote commands
back to the car. Includes Home Assistant MQTT discovery, so the car shows up as a device
without any manual YAML.

This is a fork of [moryoav/ora2mqtt](https://github.com/moryoav/ora2mqtt), which continued
[zivillian/ora2mqtt](https://github.com/zivillian/ora2mqtt) — all credit for the original
work and the reverse engineering of the GWM API goes to them.

## What is different in this fork

GWM shut down the v1 API's token minting on **2026-08-04**; every third-party client that
used `userAuth/refreshToken` or `loginWithSMS` started getting `607198 System busy`. The My
GWM app had moved to a signed **v2** API. This fork implements that flow:

- **v2 request signing** — the `gwm-auth-appkey` / `-nonce` / `-timestamp` / `-sign` headers.
  Plain SHA-256 (not HMAC) over method, path, headers, params and secret, see
  [V2_SIGN.md](V2_SIGN.md).
- **v2 login** — `loginWithPassword` plus `getVerifyCode` / `checkVerifyCode` for the
  e-mail code an unknown device gets asked for.
- **mTLS certificate enrollment** — the app-gateway no longer accepts the bootstrap
  certificate shipped in the app (`400 No required SSL certificate was sent`). The client now
  enrolls its own per-install certificate via `appAuth/applyCertificate` and stores it in the
  config file. This happens automatically on the first `run`.

Beyond the v2 work:

- **Home Assistant discovery** — one device with sensors, A/C switch + climate entity, door
  lock, close-windows button and a remote command status sensor. Republished on reconnect and
  on the HA birth message.
- **Reliability** — the access token is kept when a refresh fails (a failed refresh used to
  wipe it and hammer the API), exponential backoff up to 15 min on repeated failures, per-cycle
  exception handling and persistent MQTT reconnect. The endless-loop wrapper script the old
  README suggested is no longer needed.

Tested against the EU region with one ORA 03. Other regions (in particular AU/NZ, which uses
a different app key) are not implemented.

## Setup

Create the config file — the wizard asks for country, account, MQTT broker, optional Home
Assistant discovery and the vehicle security PIN:

```bash
ora2mqtt configure
```

It is a good idea to create a second GWM account and share the car with it, rather than using
your main account.

Then start it:

```bash
ora2mqtt run      # or just: ora2mqtt
ora2mqtt run -i 60   # slower polling, default is 10s
```

Remote commands (A/C, lock, windows) need the vehicle security PIN from the official app. The
wizard stores it, or you add it manually:

```yaml
account:
  securityPin: "123456"
```

The PIN is hashed before it is sent.

### Docker

This fork does not publish images, so build it yourself:

```bash
docker build -t ora2mqtt .
docker run -d --restart=unless-stopped -v ./ora2mqtt.yml:/config/ora2mqtt.yml ora2mqtt
```

The image ships `gwm_root.pem` and sets `OPENSSL_CONF` already. Note that the config file is
written back (tokens, enrolled certificate), so it has to be a writable bind mount, not a
read-only one.

### Running the binaries on Linux

The GWM certificates need a relaxed OpenSSL security level, and the GWM root certificate has
to be trusted:

```bash
sudo cp libgwmapi/Resources/gwm_root.pem /etc/ssl/certs/
export OPENSSL_CONF=/path/to/openssl.cnf   # from this repository
./ora2mqtt run
```

## MQTT topics

Everything lives below `GWM/<VIN>/`:

| Topic | Content |
| ----- | ------- |
| `status/items/<code>/value` | raw data point, see the table below |
| `status/items/<code>/unit` | unit, when the API sends one |
| `status/AcquisitionTime`, `status/UpdateTime` | timestamps in ms |
| `status/Latitude`, `status/Longitude`, `status/Location` | position (`Location` is JSON) |
| `status/ac/mode`, `status/ac/targetTemperature`, `status/ac/action` | A/C state for the climate entity |
| `status/commandStatus` | result of the last remote command |
| `command/ac` | `{"switchOrder":"1","temperature":"22","operationTime":"30"}` |
| `command/ac/mode` | `off` / `cool` |
| `command/ac/temperature` | target temperature, e.g. `22` |
| `command/lock` | `LOCK` / `UNLOCK` |
| `command/windows/close` | `PRESS` |

## evcc

```yaml
vehicles:
- name: ora
  type: custom
  title: Ora Funky Cat
  capacity: 45
  phases: 3
  soc:
    source: mqtt
    topic: GWM/<VIN>/status/items/2013021/value
    timeout: 1m
  range:
    source: mqtt
    topic: GWM/<VIN>/status/items/2011501/value
    timeout: 1m
  odometer:
    source: mqtt
    topic: GWM/<VIN>/status/items/2103010/value
    timeout: 1m
```

## Data points

| Code | Description |
| ---- | ----------- |
| 2011501 | Range in km |
| 2013021 | SOC |
| 2013022 | Remaining charging time in minutes |
| 2041142 | Charging active |
| 2041301 | SOCE |
| 2042082 | Bool flag, only active while charging (but not always) |
| 2101001–2101004 | Tire pressure FL / FR / RL / RR in kPa |
| 2101005–2101008 | Tire temperature FL / FR / RL / RR in °C |
| 2103010 | Odometer in km |
| 2201001 | Interior temperature in tenths of °C |
| 2202001 | Air conditioning on |
| 2208001 | Lock open |
| 2210001–2210004 | Window closed FL / FR / RL / RR |

Published but still unidentified: 2013023, 2042071, 2078020, 2102001–2102010, 2210010–2210013,
2222001, 2310001.

## Troubleshooting

**`Code required. Please check your mail`** — GWM treats the `deviceId` in the config as a new
device and wants an e-mail code (`308103` / `110641`). Enter it once; the token survives from
there via refresh.

**`400 No required SSL certificate was sent`** — the certificate enrollment did not run or
failed. Delete `account.clientCertificate` / `clientCertificateKey` from the config and start
`run` again; it re-enrolls with a valid token.

**Repeated `System busy`** — usually a transient GWM cloud problem. The run loop backs off up
to 15 minutes on its own; do not restart it in a tight loop, the API rate-limits hard.

## Development

Two extra verbs help when GWM changes the backend again:

```bash
ora2mqtt login -e mail@example.com -p secret [--code 1234] [--dry-run]
ora2mqtt probe --path ../v2.0/userAuth/captcha/gen --method POST --body '{"captchaType":1}'
```

Environment overrides: `GWM_H5_BASE` (base URL), `GWM_HEADER_<NAME>` (e.g.
`GWM_HEADER_TERMINAL`, `GWM_HEADER_BRAND`), `GWM_EXTRA_HEADERS` (`name=value,name=value`) and
`GWM_NO_CLIENT_CERT=1`.

The signing scheme is documented in [V2_SIGN.md](V2_SIGN.md), the analysis that led to it —
including what was ruled out — in [V2_FINDINGS.md](V2_FINDINGS.md) (partly German).
`tools/gwm_v2_sign.py` is a standalone signer for poking at the API with curl.

The GWM API details from the original project (endpoints, the obfuscated RSA keys in the app,
certificate pinning) are described in the
[upstream README](https://github.com/zivillian/ora2mqtt/blob/main/README.md); the
deobfuscation code lives in [CertificateHandler.cs](libgwmapi/CertificateHandler.cs) and is
still used for the bootstrap certificate.

## Contributing

Issues and pull requests are welcome: [open an issue](https://github.com/Oponn4/ora2mqtt/issues/new).
