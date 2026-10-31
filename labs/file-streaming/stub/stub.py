#!/usr/bin/env python3
"""A deliberately DUMB S3-compatible object store for the file-streaming lab.

It stores every PUT body exactly as received — it does NOT decode `Content-Encoding: aws-chunked`. That is the
behaviour of the bank's S3 in vnext-client-sdk-core#145: Dapr 1.17+ bindings.aws.s3 frames every TLS upload as
aws-chunked with a CRC32 trailer, and a server that does not decode it keeps the frame as the object.

Path-style only: /{bucket}/{key}. Supports PUT, GET (with a single Range), HEAD, DELETE and a minimal
ListObjectsV2 (GET /{bucket}?list-type=2). Signatures are not checked.

Admin API (for tests, never a real S3 path):
  GET  /_admin/objects/{bucket}/{key}  -> {"size": n, "sha256": "...", "headers": {...last PUT request headers}}
  PUT  /_admin/objects/{bucket}/{key}  -> replaces the stored bytes with the request body (simulates a store that changed them)
"""
import hashlib, json, os, sys, threading, email.utils, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs, unquote

OBJECTS = {}          # (bucket, key) -> (bytes, content_type, put_headers, mtime)
LOCK = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stderr.write("stub %s %s\n" % (self.command, self.path))

    # ---- helpers -----------------------------------------------------------------------------------------
    def _read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":   # HTTP/1.1 transfer chunking only
            out = bytearray()
            while True:
                size = int(self.rfile.readline().split(b";")[0].strip(), 16)
                if size == 0:
                    while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                        pass
                    return bytes(out)
                out += self.rfile.read(size)
                self.rfile.readline()
        length = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(length) if length else b""

    def _send(self, status, body=b"", headers=None):
        self.send_response(status)
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        if "Content-Length" not in (headers or {}):
            self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD" and body:
            self.wfile.write(body)

    def _target(self):
        u = urlparse(self.path)
        parts = unquote(u.path).lstrip("/").split("/", 1)
        bucket = parts[0]
        key = parts[1] if len(parts) > 1 else ""
        return u, bucket, key

    def _not_found(self):
        self._send(404, b"<Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message></Error>",
                   {"Content-Type": "application/xml"})

    # ---- verbs -------------------------------------------------------------------------------------------
    def do_PUT(self):
        u, bucket, key = self._target()
        body = self._read_body()
        if bucket == "_admin":
            _, rest = key.split("/", 1)
            b, k = rest.split("/", 1)
            with LOCK:
                old = OBJECTS.get((b, k))
                OBJECTS[(b, k)] = (body, old[1] if old else "application/octet-stream", old[2] if old else {}, time.time())
            return self._send(200, b"{}", {"Content-Type": "application/json"})
        if not key:  # CreateBucket
            return self._send(200)
        with LOCK:
            OBJECTS[(bucket, key)] = (body, self.headers.get("Content-Type", "application/octet-stream"),
                                      {k: v for k, v in self.headers.items()}, time.time())
        self._send(200, b"", {"ETag": '"%s"' % hashlib.md5(body).hexdigest()})

    def do_GET(self):
        u, bucket, key = self._target()
        if bucket == "_admin":
            _, rest = key.split("/", 1)
            b, k = rest.split("/", 1)
            with LOCK:
                obj = OBJECTS.get((b, k))
            if not obj:
                return self._not_found()
            data = json.dumps({"size": len(obj[0]), "sha256": hashlib.sha256(obj[0]).hexdigest(),
                               "headers": obj[2]}).encode()
            return self._send(200, data, {"Content-Type": "application/json"})
        if not key:
            q = parse_qs(u.query)
            prefix = (q.get("prefix") or [""])[0]
            with LOCK:
                keys = sorted(k for (b, k) in OBJECTS if b == bucket and k.startswith(prefix))
            items = "".join("<Contents><Key>%s</Key><Size>%d</Size></Contents>" % (k, len(OBJECTS[(bucket, k)][0])) for k in keys)
            xml = ('<?xml version="1.0" encoding="UTF-8"?><ListBucketResult><Name>%s</Name><KeyCount>%d</KeyCount>'
                   '<IsTruncated>false</IsTruncated>%s</ListBucketResult>' % (bucket, len(keys), items)).encode()
            return self._send(200, xml, {"Content-Type": "application/xml"})
        with LOCK:
            obj = OBJECTS.get((bucket, key))
        if not obj:
            return self._not_found()
        data, ctype, _, mtime = obj
        headers = {"Content-Type": ctype, "ETag": '"%s"' % hashlib.md5(data).hexdigest(), "Accept-Ranges": "bytes",
                   "Last-Modified": email.utils.formatdate(mtime, usegmt=True)}
        rng = self.headers.get("Range")
        if rng and rng.startswith("bytes="):
            start_s, end_s = rng[6:].split("-", 1)
            start = int(start_s) if start_s else len(data) - int(end_s)
            end = int(end_s) if (end_s and start_s) else len(data) - 1
            end = min(end, len(data) - 1)
            part = data[start:end + 1]
            headers["Content-Range"] = "bytes %d-%d/%d" % (start, end, len(data))
            return self._send(206, part, headers)
        self._send(200, data, headers)

    def do_HEAD(self):
        u, bucket, key = self._target()
        with LOCK:
            obj = OBJECTS.get((bucket, key))
        if not obj:
            return self._send(404)
        data, ctype, _, mtime = obj
        self._send(200, b"", {"Content-Type": ctype, "Content-Length": str(len(data)),
                              "ETag": '"%s"' % hashlib.md5(data).hexdigest(), "Accept-Ranges": "bytes",
                              "Last-Modified": email.utils.formatdate(mtime, usegmt=True)})

    def do_DELETE(self):
        u, bucket, key = self._target()
        with LOCK:
            OBJECTS.pop((bucket, key), None)
        self._send(204)


if __name__ == "__main__":
    port = int(os.environ.get("PORT", "9100"))
    print("dumb s3 stub on :%d" % port, flush=True)
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
