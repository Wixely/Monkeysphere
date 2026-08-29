#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
outer_engine=${OUTER_CONTAINER_ENGINE:-podman}
container_name="monkeysphere-dind-verification-$$"
inner_port=${MONKEYSPHERE_VERIFY_PORT:-15083}

case "$outer_engine" in
    podman) ;;
    *) echo "OUTER_CONTAINER_ENGINE must be podman." >&2; exit 2 ;;
esac
if ! command -v "$outer_engine" >/dev/null 2>&1; then
    echo "Required command is unavailable: $outer_engine" >&2
    exit 2
fi

cleanup() {
    "$outer_engine" rm --force "$container_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT HUP INT TERM

"$outer_engine" run --detach --privileged \
    --name "$container_name" \
    --env DOCKER_TLS_CERTDIR= \
    --volume "$repository_root:/workspace:ro" \
    docker.io/library/docker:28.5.2-dind@sha256:2a232a42256f70d78e3cc5d2b5d6b3276710a0de0596c145f627ecfae90282ac \
    --storage-driver=vfs >/dev/null

attempt=0
until "$outer_engine" exec "$container_name" docker info >/dev/null 2>&1; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 120 ] || [ "$("$outer_engine" inspect --format '{{.State.Running}}' "$container_name" 2>/dev/null || true)" != true ]; then
        "$outer_engine" logs "$container_name" >&2 || true
        echo "The isolated Docker daemon did not become ready." >&2
        exit 1
    fi
    sleep 1
done

"$outer_engine" exec "$container_name" apk add --no-cache curl >/dev/null

"$outer_engine" exec \
    --env DOCKER_BUILDKIT=0 \
    --env COMPOSE_DOCKER_CLI_BUILD=0 \
    --env "MONKEYSPHERE_VERIFY_PORT=$inner_port" \
    --env "MONKEYSPHERE_VERIFY_PROJECT=monkeysphere-verify-nested-$$" \
    "$container_name" \
    sh /workspace/eng/VerifyDocker.sh

echo "Docker Engine and Compose verification passed inside an isolated nested daemon."
