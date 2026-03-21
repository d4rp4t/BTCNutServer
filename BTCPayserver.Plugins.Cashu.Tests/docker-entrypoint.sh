#!/bin/sh
set -e

FILTERS="--filter Category=Integration"
if [ -n "$TEST_FILTERS" ]; then
  FILTERS="--filter $TEST_FILTERS"
fi

dotnet test -c "${CONFIGURATION_NAME:-Release}" $FILTERS --no-build -v n --logger "console;verbosity=normal" < /dev/null
