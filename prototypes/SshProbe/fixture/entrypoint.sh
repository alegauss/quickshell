#!/bin/sh
set -e

mkdir -p /run/sshd

for user in probe certonly twofactor; do
    id -u "$user" >/dev/null 2>&1 || useradd -m -s /bin/bash "$user"
done

echo 'probe:probe-pw' | chpasswd
echo 'twofactor:twofactor-pw' | chpasswd
echo 'certonly:certonly-pw' | chpasswd

# probe and twofactor authorise the key directly. certonly deliberately does not: the only way
# in for that account is a certificate the CA signed, which is what makes its answer evidence.
for user in probe twofactor; do
    install -d -m 700 -o "$user" -g "$user" "/home/$user/.ssh"
    cp /keys/probe_ed25519.pub "/home/$user/.ssh/authorized_keys"
    chown "$user:$user" "/home/$user/.ssh/authorized_keys"
    chmod 600 "/home/$user/.ssh/authorized_keys"
done

# 64 MB of printable ASCII, so the throughput question is asked of a shell stream carrying what a
# terminal actually receives rather than of a binary channel.
if [ ! -f /srv/big.txt ]; then
    mkdir -p /srv
    head -c 50000000 /dev/zero | base64 | head -c 67108864 > /srv/big.txt
    chmod 644 /srv/big.txt
fi

ssh-keygen -A >/dev/null 2>&1 || true

exec /usr/sbin/sshd -D -e
