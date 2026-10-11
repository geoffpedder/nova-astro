#!/usr/bin/env bash
# Starts an INDI server with the simulator drivers, for developing on Linux without hardware.
# Then run NOVA with Nova__Backend=Indi (launch profile "indi").
set -euo pipefail
exec indiserver -v \
  indi_simulator_telescope \
  indi_simulator_ccd \
  indi_simulator_focus \
  indi_simulator_wheel
