#!/bin/sh
set -eu
tries=0
until mc alias set vault http://minio:9000 pdm-bootstrap "$PDM_MINIO_ROOT_PASSWORD" >/dev/null 2>&1 && mc ls vault >/dev/null 2>&1; do
    tries=$((tries+1))
    if [ "$tries" -ge 30 ]; then
        echo 'MinIO did not become available' >&2
        exit 1
    fi
    sleep 2
done
mc mb --ignore-existing vault/pdm-snapshots
mc admin policy create vault pdm-pilot /policy.json
mc admin user add vault pdm-api "$PDM_S3_SECRET_KEY"
if ! mc admin policy attach vault pdm-pilot --user pdm-api; then
    mc admin user info --json vault pdm-api | grep -Eq '"policyName"[[:space:]]*:[[:space:]]*"pdm-pilot"'
fi
