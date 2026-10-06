#!/bin/sh
# One account, `probe`, with the fixture's ed25519 key: what is under test is the server, so the
# client side is held still and is the same on every row.
set -e

if ! id -u probe >/dev/null 2>&1; then
    if command -v useradd >/dev/null 2>&1; then
        useradd -m -s /bin/bash probe
    else
        adduser -D -s /bin/sh probe
    fi
fi

# A password, though nothing logs in with it: Dropbear treats an account whose password field is
# locked as locked for every method, keys included.
echo 'probe:probe-pw' | chpasswd

mkdir -p /home/probe/.ssh
cat /keys/probe_ed25519.pub > /home/probe/.ssh/authorized_keys

# And an RSA key, because the RSA signature is where the versions part: OpenSSH before 7.2 knows
# only ssh-rsa over SHA-1, which newer clients will not sign with.
cat /keys/probe_rsa.pub >> /home/probe/.ssh/authorized_keys
chown -R probe /home/probe/.ssh
chmod 700 /home/probe/.ssh
chmod 600 /home/probe/.ssh/authorized_keys

if [ "$1" = "dropbear" ]; then
    mkdir -p /etc/dropbear
    # -R makes the host keys on first connection, -s refuses passwords, -F and -E stay in the
    # foreground and log to stderr where docker can see them.
    exec dropbear -F -E -R -s -p 22
fi

mkdir -p /run/sshd
ssh-keygen -A >/dev/null 2>&1 || true
exec /usr/sbin/sshd -D -e
