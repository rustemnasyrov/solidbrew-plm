FROM golang:1.25-alpine AS toolchain
RUN apk add --no-cache git ca-certificates
ENV CGO_ENABLED=0
WORKDIR /src

FROM toolchain AS server-build
# MinIO RELEASE.2025-10-15T17-29-55Z
RUN git init -q . \
    && git fetch -q --depth=1 https://github.com/minio/minio.git 9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a \
    && git checkout -q --detach FETCH_HEAD \
    && mkdir -p /out \
    && go build -trimpath -o /out/minio .

FROM toolchain AS client-build
# MinIO Client RELEASE.2025-08-13T08-35-41Z
RUN git init -q . \
    && git fetch -q --depth=1 https://github.com/minio/mc.git 7394ce0dd2a80935aded936b09fa12cbb3cb8096 \
    && git checkout -q --detach FETCH_HEAD \
    && mkdir -p /out \
    && go build -trimpath -o /out/mc .

FROM alpine:3.23 AS server
RUN apk add --no-cache ca-certificates \
    && addgroup -S -g 10001 vault \
    && adduser -S -D -H -u 10001 -G vault vault \
    && mkdir -p /data && chown 10001:10001 /data
COPY --from=server-build /out/minio /usr/local/bin/minio
COPY --from=server-build /src/LICENSE /usr/local/share/licenses/minio
USER 10001:10001
ENTRYPOINT ["/usr/local/bin/minio"]

FROM alpine:3.23 AS client
RUN apk add --no-cache ca-certificates
COPY --from=client-build /out/mc /usr/local/bin/mc
COPY --from=client-build /src/LICENSE /usr/local/share/licenses/mc
ENTRYPOINT ["/usr/local/bin/mc"]
