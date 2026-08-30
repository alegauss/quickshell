#!/bin/sh
# Brings the fixture up from nothing: keys, a CA, a signed certificate, two servers.
set -e

cd "$(dirname "$0")"
mkdir -p keys

if [ ! -f keys/probe_ed25519 ]; then
    ssh-keygen -t ed25519 -N "" -C "quickshell-probe" -f keys/probe_ed25519
fi

if [ ! -f keys/ca ]; then
    ssh-keygen -t ed25519 -N "" -C "quickshell-probe-ca" -f keys/ca
fi

# The certificate names `certonly` as its principal, which is the account with no authorized_keys.
if [ ! -f keys/probe_ed25519-cert.pub ]; then
    ssh-keygen -s keys/ca -I quickshell-probe -n certonly,probe -V -5m:+52w keys/probe_ed25519.pub
fi

# The key material QS41 needs: every type and every format a user might hand this client. Made
# inside the image rather than here, because puttygen is not something a Windows developer has and
# the image already carries it.
docker compose build target >/dev/null

if [ ! -f keys/probe.ppk ]; then
    # pwd -W is Git Bash's Windows spelling of the current directory. Without it the mount path
    # arrives as /d/Git/... which the engine does not understand, and the container starts with no
    # /keys at all - which fails as "cannot save the key" rather than as "nothing is mounted".
    here="$(pwd -W 2>/dev/null || pwd)"

    docker run --rm -v "$here/keys:/keys" --entrypoint sh fixture-target -c '
        set -e
        ssh-keygen -t rsa -b 3072 -N "" -C quickshell-rsa -f /keys/probe_rsa
        ssh-keygen -t ecdsa -b 256 -N "" -C quickshell-ecdsa -f /keys/probe_ecdsa
        ssh-keygen -t ed25519 -N "sesame" -C quickshell-locked -f /keys/probe_locked

        # The same RSA key in the two other formats a user may hand over. Converted in /tmp and
        # copied back: the bind mount arrives world-writable from Windows, and ssh-keygen refuses to
        # read a private key with permissions that open.
        for format in PEM PKCS8; do
            lower=$(echo "$format" | tr "A-Z" "a-z")
            cp /keys/probe_rsa "/tmp/$lower"
            chmod 600 "/tmp/$lower"
            ssh-keygen -p -N "" -m "$format" -f "/tmp/$lower" >/dev/null
            cp "/tmp/$lower" "/keys/probe_$lower"
        done
        cp /keys/probe_rsa.pub /keys/probe_pem.pub
        cp /keys/probe_rsa.pub /keys/probe_pkcs8.pub

        # And PuTTY, because a MobaXterm user'"'"'s keys are very often in that one.
        cp /keys/probe_ed25519 /tmp/ppk-source
        chmod 600 /tmp/ppk-source
        puttygen /tmp/ppk-source -O private -o /keys/probe.ppk
        chmod 644 /keys/*.pub /keys/*.ppk
    '
fi

docker compose up -d --build

echo "waiting for sshd"
for _ in $(seq 1 30); do
    if ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
           -o BatchMode=yes -i keys/probe_ed25519 -p 2222 probe@127.0.0.1 true 2>/dev/null; then
        echo "target is up"
        break
    fi
    sleep 1
done
