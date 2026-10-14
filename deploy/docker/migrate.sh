#!/bin/sh
# Runs the EF Core migrations bundle against the database named in ConnectionStrings__collectionmaxxing.
set -eu
: "${ConnectionStrings__collectionmaxxing:?ConnectionStrings__collectionmaxxing is not set}"
exec /app/efbundle --connection "$ConnectionStrings__collectionmaxxing"
