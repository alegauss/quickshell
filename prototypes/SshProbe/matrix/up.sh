#!/bin/sh
# Brings the compatibility matrix up (QS40). The keys are the fixture's, so that comes first.
set -e

cd "$(dirname "$0")"

if [ ! -f ../fixture/keys/probe_ed25519.pub ]; then
    ../fixture/up.sh
fi

docker compose up -d --build
