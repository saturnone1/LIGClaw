import { Transform, type TransformCallback } from "node:stream";
import { MAXIMUM_HEADER_BYTES, MAXIMUM_PAYLOAD_BYTES } from "./protocol.js";

const HEADER_TERMINATOR = Buffer.from("\r\n\r\n", "ascii");

export function frameMessage(message: unknown): Buffer {
  const payload = Buffer.from(JSON.stringify(message), "utf8");
  if (payload.length > MAXIMUM_PAYLOAD_BYTES) {
    throw new Error(`RPC payload exceeds ${MAXIMUM_PAYLOAD_BYTES} bytes.`);
  }

  const header = Buffer.from(`Content-Length: ${payload.length}\r\n\r\n`, "ascii");
  return Buffer.concat([header, payload]);
}

export class MessageDecoder extends Transform {
  private pending = Buffer.alloc(0);
  private contentLength: number | undefined;

  constructor() {
    super({ readableObjectMode: true });
  }

  override _transform(chunk: Buffer, _encoding: BufferEncoding, callback: TransformCallback): void {
    try {
      this.pending = Buffer.concat([this.pending, chunk]);
      this.decodeAvailableMessages();
      callback();
    } catch (error) {
      callback(error as Error);
    }
  }

  override _flush(callback: TransformCallback): void {
    if (this.contentLength !== undefined || this.pending.length > 0) {
      callback(new Error("RPC stream ended with an incomplete message."));
      return;
    }
    callback();
  }

  private decodeAvailableMessages(): void {
    while (true) {
      if (this.contentLength === undefined) {
        const terminator = this.pending.indexOf(HEADER_TERMINATOR);
        if (terminator < 0) {
          if (this.pending.length > MAXIMUM_HEADER_BYTES) {
            throw new Error("RPC header exceeds the allowed size.");
          }
          return;
        }

        const header = this.pending.subarray(0, terminator).toString("ascii");
        this.pending = this.pending.subarray(terminator + HEADER_TERMINATOR.length);
        const match = /^Content-Length:\s*(\d+)$/im.exec(header);
        if (match?.[1] === undefined) throw new Error("RPC Content-Length is missing.");
        this.contentLength = Number.parseInt(match[1], 10);
        if (this.contentLength > MAXIMUM_PAYLOAD_BYTES) {
          throw new Error("RPC payload exceeds the allowed size.");
        }
      }

      if (this.pending.length < this.contentLength) return;
      const payload = this.pending.subarray(0, this.contentLength);
      this.pending = this.pending.subarray(this.contentLength);
      this.contentLength = undefined;
      this.push(JSON.parse(payload.toString("utf8")));
    }
  }
}
