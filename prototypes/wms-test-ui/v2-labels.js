/*
 * Small QR byte-mode encoder adapted from Project Nayuki's QR Code generator.
 * Copyright (c) Project Nayuki. MIT License.
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
 * of the Software, and to permit persons to whom the Software is furnished to do
 * so, subject to the following conditions:
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
// No remote renderer, network dependency or framework. ECI 26 makes UTF-8 explicit.
import { escapeHtml } from "./v2-client.js";
const eccLengths = [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22];
const blockCounts = [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6];
function rawCodewords(version) {
  let count = (16 * version + 128) * version + 64;
  if (version >= 2) { const n = Math.floor(version / 7) + 2; count -= (25 * n - 10) * n - 55; if (version >= 7) count -= 36; }
  return Math.floor(count / 8);
}
function multiply(x, y) {
  let result = 0;
  for (let bit = 7; bit >= 0; bit--) { result = result << 1 ^ (result >>> 7) * 0x11d; result ^= (y >>> bit & 1) * x; }
  return result;
}
function divisor(degree) {
  const result = Array(degree).fill(0); result[degree - 1] = 1;
  for (let i = 0, root = 1; i < degree; i++, root = multiply(root, 2)) {
    for (let j = 0; j < degree; j++) result[j] = multiply(result[j], root) ^ (result[j + 1] || 0);
  }
  return result;
}
function remainder(data, polynomial) {
  const result = polynomial.map(() => 0);
  for (const byte of data) { const factor = byte ^ result.shift(); result.push(0); polynomial.forEach((n, i) => result[i] ^= multiply(n, factor)); }
  return result;
}
const masks = [
  (x, y) => (x + y) % 2 === 0, (_, y) => y % 2 === 0, x => x % 3 === 0,
  (x, y) => (x + y) % 3 === 0, (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0,
  (x, y) => x * y % 2 + x * y % 3 === 0, (x, y) => (x * y % 2 + x * y % 3) % 2 === 0,
  (x, y) => ((x + y) % 2 + x * y % 3) % 2 === 0
];
function penalty(matrix) {
  let result = 0, dark = 0; const size = matrix.length;
  const score = line => {
    let n = 1;
    for (let i = 1; i <= size; i++) { if (line[i] === line[i - 1]) n++; else { if (n >= 5) result += 3 + n - 5; n = 1; } }
    const text = line.map(Number).join("");
    for (let i = 0; i <= size - 11; i++) if (["00001011101", "10111010000"].includes(text.slice(i, i + 11))) result += 40;
  };
  for (let y = 0; y < size; y++) {
    score(matrix[y]); score(matrix.map(row => row[y]));
    for (let x = 0; x < size; x++) { if (matrix[y][x]) dark++; if (x && y && matrix[y][x] === matrix[y - 1][x] && matrix[y][x] === matrix[y][x - 1] && matrix[y][x] === matrix[y - 1][x - 1]) result += 3; }
  }
  return result + Math.floor(Math.abs(dark * 100 / (size * size) - 50) / 5) * 10;
}
export function qrMatrix(text, forcedMask = null) {
  if (!text || text.length > 128 || /[\u0000-\u001f\u007f]/.test(text)) throw new Error("标签编码必须为 1～128 个字符，不能含控制字符。");
  const bytes = [...new TextEncoder().encode(text)];
  let version = 1;
  while (version <= 15 && 12 + 4 + (version < 10 ? 8 : 16) + bytes.length * 8 > (rawCodewords(version) - eccLengths[version] * blockCounts[version]) * 8) version++;
  if (version > 15) throw new Error("编码过长。");
  const capacity = rawCodewords(version) - eccLengths[version] * blockCounts[version];
  const bits = [], append = (value, count) => { for (let i = count - 1; i >= 0; i--) bits.push(value >>> i & 1); };
  append(7, 4); append(26, 8); append(4, 4); append(bytes.length, version < 10 ? 8 : 16);
  bytes.forEach(byte => append(byte, 8)); append(0, Math.min(4, capacity * 8 - bits.length)); append(0, (8 - bits.length % 8) % 8);
  for (let pad = 0xec; bits.length < capacity * 8; pad ^= 0xec ^ 0x11) append(pad, 8);
  const data = Array(capacity).fill(0); bits.forEach((bit, i) => data[i >>> 3] |= bit << (7 - (i & 7)));
  const blocks = [], count = blockCounts[version], degree = eccLengths[version];
  const shortLength = Math.floor(rawCodewords(version) / count), shortCount = count - rawCodewords(version) % count, poly = divisor(degree);
  for (let block = 0, offset = 0; block < count; block++) {
    const part = data.slice(offset, offset + shortLength - degree + (block < shortCount ? 0 : 1)); offset += part.length;
    const ecc = remainder(part, poly); if (block < shortCount) part.push(0); blocks.push([...part, ...ecc]);
  }
  const words = [];
  for (let i = 0; i < blocks[0].length; i++) blocks.forEach((block, n) => { if (i !== shortLength - degree || n >= shortCount) words.push(block[i]); });
  const size = version * 4 + 17, matrix = Array.from({ length: size }, () => Array(size).fill(false)), fixed = matrix.map(row => row.slice());
  const set = (x, y, dark) => { matrix[y][x] = Boolean(dark); fixed[y][x] = true; };
  for (let n = 0; n < size; n++) { set(6, n, n % 2 === 0); set(n, 6, n % 2 === 0); }
  for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]]) for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) {
    const x = cx + dx, y = cy + dy, distance = Math.max(Math.abs(dx), Math.abs(dy));
    if (x >= 0 && x < size && y >= 0 && y < size) set(x, y, distance !== 2 && distance !== 4);
  }
  if (version > 1) {
    const n = Math.floor(version / 7) + 2, step = Math.floor((version * 8 + n * 3 + 5) / (n * 4 - 4)) * 2, positions = [6];
    for (let p = size - 7; positions.length < n; p -= step) positions.splice(1, 0, p);
    positions.forEach((cx, i) => positions.forEach((cy, j) => {
      if ((i === 0 && j === 0) || (i === 0 && j === n - 1) || (i === n - 1 && j === 0)) return;
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(cx + dx, cy + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }));
  }
  const format = mask => {
    const value = 1 << 3 | mask; let rem = value;
    for (let i = 0; i < 10; i++) rem = rem << 1 ^ (rem >>> 9) * 0x537;
    const word = (value << 10 | rem) ^ 0x5412, bit = i => word >>> i & 1;
    for (let i = 0; i < 6; i++) set(8, i, bit(i));
    set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
    for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
    for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
    for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i)); set(8, size - 8, true);
  };
  format(0);
  if (version >= 7) {
    let rem = version; for (let i = 0; i < 12; i++) rem = rem << 1 ^ (rem >>> 11) * 0x1f25;
    const word = version << 12 | rem;
    for (let i = 0; i < 18; i++) { const a = size - 11 + i % 3, b = Math.floor(i / 3); set(a, b, word >>> i & 1); set(b, a, word >>> i & 1); }
  }
  let bitIndex = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vertical = 0; vertical < size; vertical++) for (let j = 0; j < 2; j++) {
      const x = right - j, y = (right + 1 & 2) === 0 ? size - 1 - vertical : vertical;
      if (!fixed[y][x] && bitIndex < words.length * 8) { matrix[y][x] = Boolean(words[bitIndex >>> 3] >>> (7 - (bitIndex & 7)) & 1); bitIndex++; }
    }
  }
  if (bitIndex !== words.length * 8) throw new Error("QR data placement failed");
  const apply = mask => { for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (!fixed[y][x] && masks[mask](x, y)) matrix[y][x] = !matrix[y][x]; };
  let bestMask = forcedMask, bestScore = Infinity;
  if (forcedMask != null && (!Number.isInteger(forcedMask) || forcedMask < 0 || forcedMask > 7)) throw new Error("Invalid mask");
  if (bestMask == null) for (let mask = 0; mask < 8; mask++) { apply(mask); format(mask); const score = penalty(matrix); if (score < bestScore) { bestMask = mask; bestScore = score; } apply(mask); }
  apply(bestMask); format(bestMask);
  return matrix;
}
export function qrSvg(text) {
  const matrix = qrMatrix(text), size = matrix.length + 8; let path = "";
  matrix.forEach((row, y) => row.forEach((dark, x) => { if (dark) path += `M${x + 4},${y + 4}h1v1h-1z`; }));
  return `<svg xmlns="http://www.w3.org/2000/svg" role="img" aria-label="二维码 ${escapeHtml(text)}" viewBox="0 0 ${size} ${size}" width="240" height="240" shape-rendering="crispEdges"><rect width="100%" height="100%" fill="white"/><path d="${path}" fill="black"/></svg>`;
}

// Code 128 standard module widths; independently rendered using subset B.
// Table reference: ZXing Code128Reader (standard numerical encoding data).
const patterns = [
  "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213","221312","231212","112232","122132","122231","113222","123122","123221","223211","221132","221231","213212","223112","312131","311222","321122","321221","312212","322112","322211","212123","212321","232121","111323","131123","131321","112313","132113","132311","211313","231113","231311","112133","112331","132131","113123","113321","133121","313121","211331","231131","213113","213311","213131","311123","311321","331121","312113","312311","332111","314111","221411","431111","111224","111422","121124","121421","141122","141221","112214","112412","122114","122411","142112","142211","241211","221114","413111","241112","134111","111242","121142","121241","114212","124112","124211","411212","421112","421211","212141","214121","412121","111143","111341","131141","114113","114311","411113","411311","113141","114131","311141","411131","211412","211214","211232","2331112"
];
export function code128Symbols(text) {
  if (!text || text.length > 128 || !/^[\x20-\x7e]+$/.test(text)) throw new Error("一维条码支持可打印英文、数字和符号；此编码请使用二维码。");
  const symbols = [...text].map(c => c.charCodeAt(0) - 32);
  const check = (104 + symbols.reduce((sum, value, i) => sum + value * (i + 1), 0)) % 103;
  return [104, ...symbols, check, 106];
}
export function code128Svg(text) {
  let x = 10, path = "";
  for (const symbol of code128Symbols(text)) [...patterns[symbol]].forEach((width, i) => { const n = Number(width); if (i % 2 === 0) path += `M${x},0h${n}v65h-${n}z`; x += n; });
  const width = x + 10;
  return `<svg xmlns="http://www.w3.org/2000/svg" role="img" aria-label="一维条码 ${escapeHtml(text)}" viewBox="0 0 ${width} 65" width="${Math.max(220, width * 2)}" height="130" shape-rendering="crispEdges"><rect width="100%" height="100%" fill="white"/><path d="${path}" fill="black"/></svg>`;
}
