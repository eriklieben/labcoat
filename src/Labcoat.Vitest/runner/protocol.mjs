// JSON-RPC 2.0 framed as "Content-Length: N\r\n\r\n<utf-8 json>" (the transport of MTP server mode, docs/protocol.md).

/** `--name value` / `--name=value` / `--flag` into { name: value | true }. */
export function parseArgs(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) continue;
    const eq = a.indexOf("=");
    if (eq > 2) {
      result[a.slice(2, eq)] = a.slice(eq + 1);
      continue;
    }
    const next = argv[i + 1];
    if (next !== undefined && !next.startsWith("--")) {
      result[a.slice(2)] = next;
      i++;
    } else result[a.slice(2)] = true;
  }
  return result;
}

export function encodeMessage(message) {
  const body = Buffer.from(JSON.stringify(message), "utf8");
  return Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, "ascii"), body]);
}

/** Splits a byte stream into messages; calls `onMessage` with each parsed one. */
export class MessageReader {
  #buffer = Buffer.alloc(0);
  #onMessage;

  constructor(onMessage) {
    this.#onMessage = onMessage;
  }

  push(chunk) {
    this.#buffer = this.#buffer.length === 0 ? chunk : Buffer.concat([this.#buffer, chunk]);
    for (;;) {
      const headerEnd = this.#buffer.indexOf("\r\n\r\n");
      if (headerEnd < 0) return;
      const header = this.#buffer.subarray(0, headerEnd).toString("ascii");
      const match = /content-length:\s*(\d+)/i.exec(header);
      if (!match) throw new Error("Missing Content-Length header: " + header);
      const start = headerEnd + 4;
      const length = Number(match[1]);
      if (this.#buffer.length - start < length) return;
      const body = this.#buffer.subarray(start, start + length).toString("utf8");
      this.#buffer = this.#buffer.subarray(start + length);
      this.#onMessage(JSON.parse(body));
    }
  }
}
