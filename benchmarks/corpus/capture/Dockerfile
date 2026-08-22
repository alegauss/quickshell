# The machine the corpus is captured from. It is a real Linux with real programs, because
# synthetic input flatters a parser and the whole point of the corpus is that it does not.
#
# The header libraries are here for their source: boost and eigen give this machine hundreds of
# megabytes of genuine C++ that vim can open and cat can pour down a terminal. Nothing in the
# corpus is written for the occasion; it is all found on a real filesystem.
FROM debian:bookworm

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        python3 htop vim-nox tmux procps util-linux coreutils less ncurses-bin \
        libboost-dev libeigen3-dev \
 && rm -rf /var/lib/apt/lists/*

COPY capture.py /usr/local/bin/capture.py
COPY session.sh /usr/local/bin/session.sh
RUN chmod +x /usr/local/bin/session.sh

ENV TERM=xterm-256color
ENTRYPOINT ["/usr/local/bin/session.sh"]
