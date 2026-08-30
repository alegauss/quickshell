# A real OpenSSH server, because the whole point of QS5 is that no answer here comes from
# documentation. Ubuntu 24.04 carries OpenSSH 9.6, which is the "modern sshd" the line means.
FROM ubuntu:24.04

RUN apt-get update \
 && apt-get install -y --no-install-recommends openssh-server coreutils putty-tools \
 && rm -rf /var/lib/apt/lists/*

COPY sshd_config /etc/ssh/sshd_config
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 22
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
