# Goal

This is an application to publish the current status of a GWM ORA Funky Cat via MQTT and to send commands to the car via MQTT.

# Deep Gratitude

Special thanks goes to the original creator, @zivillian (https://github.com/zivillian/ora2mqtt), who unfortunately stopped working on the original project. I'm trying to continue here where he left off.

# I want to contribute...

Very welcome! I currently don't have a plan for the next steps. Therefore, it's best if you [open an issue](https://github.com/moryoav/ora2mqtt/issues/new) and tell us what you plan to do, what you can do, what you want, what you need...

What I did to get to the current state can be found under [How to...?](#how-to)

If you need help with this, you can also simply [open an issue](https://github.com/moryoav/ora2mqtt/issues/new).

# Status

There is a command-line application that runs on Windows and can read the current values and publish them via MQTT.

In the first step, the configuration file must be created with `ora2mqtt configure`. It's best to create an additional account and share the car with this account. Afterwards, the application can be started with `ora2mqtt run` or simply `ora2mqtt`. This should make the current values visible in MQTT.

The values (SOC, Range, and Odometer) can be integrated into [evcc](https://github.com/evcc-io/evcc/) with the following configuration:

```yaml
vehicles:
- name: ora
  type: custom
  title: Ora Funky Cat
  capacity: 45
  phases: 3
  soc:
    source: mqtt
    topic: GWM/<vehicleId>/status/items/2013021/value
    timeout: 1m
  range:
    source: mqtt
    topic: GWM/<vehicleId>/status/items/2011501/value
    timeout: 1m
  odometer:
    source: mqtt
    topic: GWM/<vehicleId>/status/items/2103010/value
    timeout: 1m
```

## Linux

For the binaries to run under Linux, the root certificate must be installed. Download the [`gwm_root.pem`](libgwmapi/Resources/gwm_root.pem) certificate from the repository and copy it to the system's certificate folder with `sudo cp gwm_root.pem /etc/ssl/certs/`.

Additionally, the [`openssl.cnf`](openssl.cnf) must be downloaded from the repository. Afterwards, the binaries from the release can be started with the following script.

```
#/bin/bash

export OPENSSL_CONF=/path/to/the/file/openssl.cnf
cd /path/to/the/binary/ora2mqtt/

# restart when failed
while :
do
    ./ora2mqtt -i 60
    sleep 30
done
```

The script restarts the program in an endless loop if the connection is lost. In addition, the polling interval is increased from 10s to 60s to reduce the number of requests to the GMW server.

## Docker

There is now also a Docker container. The config must be created beforehand with `ora2mqtt configure`:

```bash
docker run -d --restart=unless-stopped -v ./ora2mqtt.yml:/config/ora2mqtt.yml moryoav/ora2mqtt:latest
```

# Data points

I can read the following data points:

| Data point | Description
| ---------- | ------------
| 2011501    | Range in km
| 2013021    | SOC
| 2013022    | remaining charging time in minutes
| 2013023    | 
| 2041142    | Charging active
| 2041301    | SOCE
| 2042071    | 
| 2042082    | bool flag, only active when charging (but not always)
| 2078020    | 
| 2101001    | Tire pressure front left in kPa
| 2101002    | Tire pressure front right in kPa
| 2101003    | Tire pressure rear left in kPa
| 2101004    | Tire pressure rear right in kPa
| 2101005    | Tire temperature front left in °C
| 2101006    | Tire temperature front right in °C
| 2101007    | Tire temperature rear left in °C
| 2101008    | Tire temperature rear right in °C
| 2102001    | 
| 2102002    | 
| 2102003    | 
| 2102004    | 
| 2102007    | 
| 2102008    | 
| 2102009    | 
| 2102010    | 
| 2103010    | Odometer in km
| 2201001    | Interior temperature in tenths of °C
| 2202001    | Air conditioning on
| 2208001    | Lock open
| 2210001    | Window closed front left
| 2210002    | Window closed front right
| 2210003    | Window closed rear left
| 2210004    | Window closed rear right
| 2210010    | 
| 2210011    | 
| 2210012    | 
| 2210013    | 
| 2222001    | 
| 2310001    | 

# How it started?

In evcc, [someone suggested](https://github.com/evcc-io/evcc/discussions/9524#discussioncomment-6832420) that we should take a look at the app...

# How it's going...

## Endpoints

There are at least 4 API endpoints (for each region):

### https://eu-h5-gateway.gwmcloud.com

This is the standard endpoint for the app. Authentication takes place here, the user profile is managed, and there used to be a _Community_.

### https://eu-app-gateway.gwmcloud.com

Communication with the car takes place via this endpoint. This endpoint requires a client certificate from the GWM CA. Fortunately, the APP provides [one](#client-cert) that works.

### https://eu-data-upload-gateway.gwmcloud.com

The configuration for tracking is initially retrieved here, and then every click in the app is uploaded as gzipped JSON.

### https://eu-app-gateway-common.gwmcloud.com

So far, only one request is known, through which the app issues an individual certificate that is used to access the `eu-app-gateway` endpoint.

## HTTP Headers

Each request contains many non-standardized HTTP headers. Not all are needed, so here are only the relevant ones:

|Name       |Value     |Description                                                          |
|-----------|----------|----------------------------------------------------------------------|
|Rs         |         2|                                                             required |
|Terminal   |GW_APP_ORA|                                                             required |
|Brand      |         3|                                                             required |
|accessToken|       JWT|                                                   Result from login |
|language   | de/en/...| affects error messages and must be set for some requests |
|systemType |         1|                                                   sometimes required |
|country    |        DE|             if the value changes, the accessToken becomes invalid |

If the headers are missing, the API only returns an error. Sometimes it indicates which header is missing.

## Cert pinning

The root certificate for the `eu-app-gateway` endpoint is pinned in the app. For this, the app includes the Global Sign Root certificate as a resource (`res/raw/globalsign_chain.crt`). If this is replaced in the app (in version 1.8.1), the traffic can be intercepted with [mitmproxy](https://mitmproxy.org/).

## Client Cert

The `eu-app-gateway` endpoint requires a client certificate from the GWM CA. The app already contains a certificate. `assets/gwm_general.cer` contains the certificate, `assets/gwm_general.key` the corresponding private key, and `assets/gwm_root.pem` the certificate chain up to the GWM CA.

Upon first login, the app issues its own certificate. The certificate is stored locally on the device and can be read if the `android:debuggable` flag is set.

The certificate is stored in memory under `files/pki/cert/cert`, along with the files `files/pkey_data11`, `files/pkey_data21`, and `files/pkey_data31`. The `1` stands for the n-th certificate (because it eventually expires and needs to be renewed). The supplied key is stored in the file `files/pkey_data30`.

### pkey_data1x

This is the Public Key.

### pkey_data2x

This is also the Public Key, but the RSA parameter e has been _transformed_.

### pkey_data3x

This is the Private Key - the RSA parameter d has been _transformed_.

### _Transformation_

Both the supplied keys and the keys of the created client certificate are _transformed_. Additionally, only the RSA parameters n, d, and e are stored - the other parameters p, q, dp, dq, and qInv must be calculated. The code to reverse the transformation and calculate the missing parameters is in [CertificateHandler.cs](libgwmapi/CertificateHandler.cs).

Alternatively, this can also be done in Python with [cryptography.hazmat.primitives.asymmetric.rsa](https://cryptography.io/en/latest/hazmat/primitives/asymmetric/rsa/#handling-partial-rsa-private-keys).

# How to...?

I disassembled and reassembled the app with [apktool](https://apktool.org/). This allows the certificates to be read and replaced. To understand what's in the certificates and what is _transformed_ and how, [asn1js](https://lapo.it/asn1js) was very helpful.

To be able to install the modified app, it must be signed - this is relatively easy with [uber-apk-signer](https://github.com/patrickfav/uber-apk-signer/).

The traffic can be monitored with [mitmproxy](https://mitmproxy.org/). The root certificate must be [installed](https://docs.mitmproxy.org/stable/concepts-certificates/#installing-the-mitmproxy-ca-certificate-manually) on the device or emulator, and the client certificate from the app must be [extracted](#client-cert), [_transformed_](#transformation), and [specified](https://docs.mitmproxy.org/stable/concepts-certificates/#using-a-client-side-certificate).

The app includes some native binaries (relevant are `libbean.so` and `libbeancrypto.so`) - certificates and private keys are also processed there. However, this can be investigated very well with [Ghidra](https://ghidra-sre.org/). For the crypto part, [libtomcrypt](https://github.com/libtom/libtomcrypt/) is used - which also allows understanding the _transformation_ of the RSA parameters.
