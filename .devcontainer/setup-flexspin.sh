#!/usr/bin/env bash

set -euo pipefail

git clone https://github.com/totalspectrum/spin2cpp.git /opt/spin2cpp

cd /opt/spin2cpp

make -C /opt/spin2cpp

ln -s /opt/spin2cpp/build/{flexspin,flexcc,spin2cpp} /usr/local/bin/

flexspin --version
