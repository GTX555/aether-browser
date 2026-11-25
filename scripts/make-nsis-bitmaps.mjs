import fs from 'node:fs';
import path from 'node:path';

const OUT_DIR = path.resolve('C:/Users/luish/.gemini/antigravity/scratch/AetherBrowser/installer/assets');
fs.mkdirSync(OUT_DIR, { recursive: true });

function writeBmp24(filePath, width, height, pixelFn) {
  const rowStride = Math.ceil((width * 3) / 4) * 4;
  const imgSize = rowStride * height;
  const fileSize = 54 + imgSize;
  const buf = Buffer.alloc(fileSize);

  // BITMAPFILEHEADER
  buf.write('BM', 0, 2, 'ascii');
  buf.writeUInt32LE(fileSize, 2);
  buf.writeUInt32LE(54, 10);

  // BITMAPINFOHEADER
  buf.writeUInt32LE(40, 14);
  buf.writeInt32LE(width, 18);
  buf.writeInt32LE(height, 22); // bottom-up
  buf.writeUInt16LE(1, 26);
  buf.writeUInt16LE(24, 28);
  buf.writeUInt32LE(0, 30);
  buf.writeUInt32LE(imgSize, 34);

  for (let y = 0; y < height; y++) {
    const bmpRow = (height - 1 - y) * rowStride;
    for (let x = 0; x < width; x++) {
      const [r, g, b] = pixelFn(x, y, width, height);
      const off = 54 + bmpRow + x * 3;
      buf[off] = Math.max(0, Math.min(255, Math.round(b)));
      buf[off + 1] = Math.max(0, Math.min(255, Math.round(g)));
      buf[off + 2] = Math.max(0, Math.min(255, Math.round(r)));
    }
  }
  fs.writeFileSync(filePath, buf);
}

function sdRoundedBox(px, py, bx, by, rad) {
  const qx = Math.abs(px) - bx + rad;
  const qy = Math.abs(py) - by + rad;
  return Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - rad;
}

function distSeg(px, py, ax, ay, bx, by) {
  const pax = px - ax, pay = py - ay;
  const bax = bx - ax, bay = by - ay;
  const h = Math.max(0, Math.min(1, (pax * bax + pay * bay) / (bax * bax + bay * bay)));
  return Math.hypot(pax - bax * h, pay - bay * h);
}

function drawAetherBadge(x, y, cx, cy, size, bgR, bgG, bgB) {
  const px = x + 0.5 - cx;
  const py = y + 0.5 - cy;
  const r = size * 0.46;
  const corner = size * 0.22;
  const dBox = sdRoundedBox(px, py, r, r, corner);
  if (dBox >= 1.0) return [bgR, bgG, bgB];

  const a = Math.max(0, Math.min(1, 0.5 - dBox));
  const t = (py + r) / (2 * r);
  let R = 22 + t * 14, G = 22 + t * 14, B = 24 + t * 15;
  if (dBox > -size * 0.035) { R += 34; G += 34; B += 36; }

  const d1 = distSeg(px, py, 0, -size * 0.24, -size * 0.20, size * 0.21);
  const d2 = distSeg(px, py, 0, -size * 0.24, size * 0.20, size * 0.21);
  const d3 = distSeg(px, py, -size * 0.10, size * 0.05, size * 0.14, size * 0.01);
  const dMark = Math.min(d1, d2, d3);
  const stroke = Math.max(1.2, size * 0.052);
  const ma = Math.max(0, Math.min(1, (stroke - dMark) * 1.1 + 0.5));
  if (ma > 0) {
    R = R * (1 - ma) + 235 * ma;
    G = G * (1 - ma) + 235 * ma;
    B = B * (1 - ma) + 238 * ma;
  }

  return [
    bgR * (1 - a) + R * a,
    bgG * (1 - a) + G * a,
    bgB * (1 - a) + B * a
  ];
}

// 1. MUI Header bitmap (150x57) — crisp native white header with Aether badge on the right
writeBmp24(path.join(OUT_DIR, 'header.bmp'), 150, 57, (x, y) => {
  return drawAetherBadge(x, y, 121, 28, 38, 255, 255, 255);
});

// 2. MUI Welcome/Finish Sidebar bitmap (164x314) — sleek architectural charcoal grey with Aether badge + minimalist download glyph
writeBmp24(path.join(OUT_DIR, 'sidebar.bmp'), 164, 314, (x, y, w, h) => {
  const t = y / h;
  let R = 28 + t * 14;
  let G = 28 + t * 14;
  let B = 31 + t * 14;

  // Subtle architectural grid lines
  if ((x % 28 === 0 || y % 28 === 0) && y > 130) {
    R += 5; G += 5; B += 5;
  }

  // Right 1px divider edge
  if (x === w - 1) return [58, 58, 64];

  // Top Aether badge centered at (82, 74)
  if (y < 140) {
    return drawAetherBadge(x, y, 82, 74, 72, R, G, B);
  }

  // Clean minimal laptop/download silhouette at bottom half (matching classic Windows installer aesthetic in monochrome grey)
  const cx = 82;
  // Arrow shaft + head
  const dShaft = distSeg(x, y, cx, 168, cx, 224);
  const dLeft = distSeg(x, y, cx - 18, 206, cx, 224);
  const dRight = distSeg(x, y, cx + 18, 206, cx, 224);
  const dTrayL = distSeg(x, y, cx - 34, 232, cx - 34, 248);
  const dTrayB = distSeg(x, y, cx - 34, 248, cx + 34, 248);
  const dTrayR = distSeg(x, y, cx + 34, 248, cx + 34, 232);
  const d = Math.min(dShaft, dLeft, dRight, dTrayL, dTrayB, dTrayR);
  const a = Math.max(0, Math.min(1, (2.6 - d) * 1.0 + 0.5));
  if (a > 0) {
    R = R * (1 - a) + 218 * a;
    G = G * (1 - a) + 218 * a;
    B = B * (1 - a) + 222 * a;
  }
  return [R, G, B];
});

console.log('Generated NSIS MUI2 header.bmp (150x57) and sidebar.bmp (164x314)');
