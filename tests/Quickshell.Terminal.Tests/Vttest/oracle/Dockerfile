FROM debian:bookworm

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        python3 vttest xterm xvfb xdotool xfonts-base x11-utils \
 && rm -rf /var/lib/apt/lists/*

COPY oracle.py /usr/local/bin/oracle.py
ENV TERM=xterm
ENTRYPOINT ["python3", "/usr/local/bin/oracle.py"]
