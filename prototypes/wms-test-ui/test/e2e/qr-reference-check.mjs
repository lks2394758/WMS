// Optional developer verification against the upstream MIT reference encoder.
// Network access happens ONLY in this check; the application has no network renderer.
import assert from "node:assert/strict";
import { runInNewContext } from "node:vm";
import { createHash } from "node:crypto";
import { qrMatrix } from "../../v2-labels.js";

const response = await fetch("https://www.nayuki.io/res/qr-code-generator-library/qrcodegen.js", { signal: AbortSignal.timeout(20000) });
if (!response.ok) throw new Error("Reference encoder unavailable");
const source = await response.text();
const reference = runInNewContext(source + "\nqrcodegen;", Object.create(null), { timeout: 5000 });
for (const text of ["BC-000001", "A-B-C-04", "SN-000001", "仓储商品-123", "X".repeat(128), "仓".repeat(128)]) {
  const segments = [reference.QrSegment.makeEci(26), reference.QrSegment.makeBytes([...new TextEncoder().encode(text)])];
  const qr = reference.QrCode.encodeSegments(segments, reference.QrCode.Ecc.LOW, 1, 15, 0, false);
  const expected = Array.from({ length: qr.size }, (_, y) => Array.from({ length: qr.size }, (_, x) => qr.getModule(x, y)));
  const actual = qrMatrix(text, 0);
  assert.deepEqual(actual, expected);
  const hash = createHash("sha256").update(actual.map(row => row.map(Number).join("")).join("\n")).digest("hex");
  console.log(JSON.stringify({ text, size: actual.length, hash }));
}
console.log("PASS 6 QR reference comparisons (including multiple ECC blocks and UTF-8)");
