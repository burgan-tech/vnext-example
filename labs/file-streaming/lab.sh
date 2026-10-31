#!/usr/bin/env bash
# file-streaming lab — object stores for the x-storage streaming / integrity scenarios.
#
#   ./lab.sh up [<vnext-checkout>]     certs + stores + MinIO bucket; copies the Dapr components into the vNext
#                                      checkout's orchestration sidecar (restart the runtime sidecar after)
#   ./lab.sh env [<vnext-checkout>]    prints the env the orchestration host needs (FileStorage:Stores, daprbinding Local)
#   ./lab.sh down [<vnext-checkout>]   stops the stores and removes the copied components
set -euo pipefail
LAB="$(cd "$(dirname "$0")" && pwd)"
VNEXT="${2:-$LAB/../../../vnext}"
COMPONENTS=(fsl-minio.yaml fsl-stub-dapr.yaml)

certs() {
  [[ -f "$LAB/.certs/server.pem" ]] && return
  mkdir -p "$LAB/.certs"
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=fsl-lab-ca" \
    -keyout "$LAB/.certs/ca.key" -out "$LAB/.certs/ca.pem" 2>/dev/null
  openssl req -newkey rsa:2048 -nodes -subj "/CN=localhost" \
    -keyout "$LAB/.certs/server.key" -out "$LAB/.certs/server.csr" 2>/dev/null
  printf "subjectAltName=DNS:localhost,DNS:fsl-stub-tls,IP:127.0.0.1\n" > "$LAB/.certs/san.ext"
  openssl x509 -req -in "$LAB/.certs/server.csr" -CA "$LAB/.certs/ca.pem" -CAkey "$LAB/.certs/ca.key" \
    -CAcreateserial -days 30 -extfile "$LAB/.certs/san.ext" -out "$LAB/.certs/server.crt" 2>/dev/null
  cat "$LAB/.certs/server.crt" "$LAB/.certs/server.key" > "$LAB/.certs/server.pem"
}

case "${1:-}" in
  up)
    certs
    # The HTTP binding API carries data as JSON, so fsl-dapr118's copy decodes base64; the runtime's does not
    # (it sends raw bytes over gRPC).
    mkdir -p "$LAB/.repro"
    awk '{ print } /insecureSSL/ { print "    - { name: decodeBase64, value: \"true\" }" }' \
      "$LAB/components/fsl-stub-dapr.yaml" > "$LAB/.repro/fsl-stub-dapr.yaml"
    docker compose -f "$LAB/docker-compose.yml" up -d
    for _ in $(seq 1 30); do docker exec fsl-minio mc alias set local http://localhost:9000 fsladmin fsladmin-secret >/dev/null 2>&1 && break; sleep 1; done
    docker exec fsl-minio mc mb --ignore-existing local/fsl >/dev/null
    for c in "${COMPONENTS[@]}"; do cp "$LAB/components/$c" "$VNEXT/etc/orchestration/dapr/components/$c"; done
    echo "stores up; components copied to $VNEXT/etc/orchestration/dapr/components (restart vnext-orchestration-dapr)"
    ;;
  env)
    cat <<ENV
FileStorage__Stores__fsl-minio__Provider=s3
FileStorage__Stores__fsl-minio__ServiceUrl=http://localhost:9000
FileStorage__Stores__fsl-minio__Bucket=fsl
FileStorage__Stores__fsl-minio__AccessKey=fsladmin
FileStorage__Stores__fsl-minio__SecretKey=fsladmin-secret
FileStorage__Stores__fsl-stub__Provider=s3
FileStorage__Stores__fsl-stub__ServiceUrl=https://localhost:9443
FileStorage__Stores__fsl-stub__Bucket=fsl-stub
FileStorage__Stores__fsl-stub__AccessKey=lab
FileStorage__Stores__fsl-stub__SecretKey=lab-secret
FileStorage__Stores__fsl-stub__CaCertificatePath=$LAB/.certs/ca.pem
Workflow__TaskInvocation__Modes__daprbinding=Local
ENV
    ;;
  down)
    docker compose -f "$LAB/docker-compose.yml" down -v
    for c in "${COMPONENTS[@]}"; do rm -f "$VNEXT/etc/orchestration/dapr/components/$c"; done
    ;;
  *) sed -n 2,9p "$0"; exit 1 ;;
esac
