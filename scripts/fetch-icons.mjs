import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

const ROOT = path.resolve('C:/Users/luish/.gemini/antigravity/scratch/AetherBrowser');
const ICONS_DIR = path.join(ROOT, 'src/AetherBrowser/Assets/Icons');
const THEMES_DIR = path.join(ROOT, 'src/AetherBrowser/Themes');
const ASSETS_DIR = path.join(ROOT, 'src/AetherBrowser/Assets');

fs.mkdirSync(ICONS_DIR, { recursive: true });
fs.mkdirSync(THEMES_DIR, { recursive: true });

const ICONS = [
  'arrow-left', 'arrow-right', 'rotate-cw', 'x', 'plus', 'minus', 'square', 'copy',
  'shield', 'shield-check', 'shield-alert', 'shield-off', 'lock', 'lock-open',
  'star', 'download', 'puzzle', 'settings', 'menu', 'ellipsis-vertical', 'house', 'search',
  'moon', 'sun', 'cpu', 'zap', 'globe', 'history', 'bookmark', 'eye-off', 'trash-2',
  'refresh-cw', 'folder-open', 'external-link', 'panel-left', 'volume-2', 'volume-x',
  'pin', 'sliders-horizontal', 'keyboard', 'info', 'check', 'triangle-alert',
  'upload', 'sparkles', 'layers', 'chevron-right', 'chevron-down', 'compass',
  'bell-off', 'cookie', 'fingerprint', 'hard-drive', 'terminal', 'zoom-in', 'zoom-out',
  'maximize-2', 'printer', 'bug', 'package', 'wifi', 'feather'
];

function toPascal(name) {
  return name.split('-').map(s => s[0].toUpperCase() + s.slice(1)).join('');
}

function attr(tag, name) {
  const m = tag.match(new RegExp(`\\b${name}="([^"]+)"`));
  return m ? m[1] : null;
}

function normalizeSvgPath(d) {
  const s = d.trim();
  // In SVG, a lowercase 'm' at the start of a path is treated as ABSOLUTE M for the first pair,
  // and RELATIVE l for any subsequent implicit coordinate pairs.
  if (s.startsWith('m')) {
    const rest = s.slice(1).trim();
    const numRe = /^([-+]?(?:\d+\.?\d*|\.\d+)(?:e[-+]?\d+)?)(?:[\s,]+|(?=[-+.]))([-+]?(?:\d+\.?\d*|\.\d+)(?:e[-+]?\d+)?)(.*)$/i;
    const match = rest.match(numRe);
    if (match) {
      const x = match[1], y = match[2];
      const tail = match[3].trim();
      if (!tail) return `M${x} ${y}`;
      // If tail starts with a command letter, keep as is; otherwise insert 'l' for relative lineto
      if (/^[a-zA-Z]/.test(tail)) return `M${x} ${y} ${tail}`;
      return `M${x} ${y} l${tail}`;
    }
  }
  return s;
}

function svgToWpfPaths(svg) {
  const paths = [];
  const tagRe = /<(path|circle|rect|line|polyline|polygon)\b[^>]*>/g;
  let m;
  while ((m = tagRe.exec(svg)) !== null) {
    const tag = m[0];
    const kind = m[1];
    if (kind === 'path') {
      const d = attr(tag, 'd');
      if (d) paths.push(normalizeSvgPath(d));
    } else if (kind === 'line') {
      const x1 = attr(tag, 'x1') ?? '0', y1 = attr(tag, 'y1') ?? '0';
      const x2 = attr(tag, 'x2') ?? '0', y2 = attr(tag, 'y2') ?? '0';
      paths.push(`M${x1},${y1} L${x2},${y2}`);
    } else if (kind === 'circle') {
      const cx = parseFloat(attr(tag, 'cx') ?? '0');
      const cy = parseFloat(attr(tag, 'cy') ?? '0');
      const r = parseFloat(attr(tag, 'r') ?? '0');
      if (r > 0) {
        paths.push(`M${cx - r},${cy} A${r},${r} 0 1,0 ${cx + r},${cy} A${r},${r} 0 1,0 ${cx - r},${cy} Z`);
      }
    } else if (kind === 'rect') {
      const x = parseFloat(attr(tag, 'x') ?? '0');
      const y = parseFloat(attr(tag, 'y') ?? '0');
      const w = parseFloat(attr(tag, 'width') ?? '0');
      const h = parseFloat(attr(tag, 'height') ?? '0');
      const rx = Math.min(parseFloat(attr(tag, 'rx') ?? '0'), w / 2);
      const ry = Math.min(parseFloat(attr(tag, 'ry') ?? String(rx)), h / 2);
      if (rx > 0 || ry > 0) {
        const r = rx || ry;
        paths.push(
          `M${x + r},${y} H${x + w - r} A${r},${r} 0 0,1 ${x + w},${y + r} ` +
          `V${y + h - r} A${r},${r} 0 0,1 ${x + w - r},${y + h} ` +
          `H${x + r} A${r},${r} 0 0,1 ${x},${y + h - r} ` +
          `V${y + r} A${r},${r} 0 0,1 ${x + r},${y} Z`
        );
      } else {
        paths.push(`M${x},${y} H${x + w} V${y + h} H${x} Z`);
      }
    } else if (kind === 'polyline' || kind === 'polygon') {
      const nums = (attr(tag, 'points') ?? '').match(/[-+]?(?:\d+\.?\d*|\.\d+)/g) ?? [];
      if (nums.length >= 4) {
        let d = `M${nums[0]},${nums[1]}`;
        for (let i = 2; i + 1 < nums.length; i += 2) {
          d += ` L${nums[i]},${nums[i + 1]}`;
        }
        if (kind === 'polygon') d += ' Z';
        paths.push(d);
      }
    }
  }
  return paths.join(' ');
}

// --- Generate crisp PNG-in-ICO app icon (minimalist obsidian squircle + Aether prism) ---
function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) {
    c ^= buf[i];
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1));
  }
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const t = Buffer.from(type, 'ascii');
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([t, data])), 0);
  return Buffer.concat([len, t, data, crcBuf]);
}

function makePng(size) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  const cx = size / 2, cy = size / 2;
  const r = size * 0.44;
  const corner = size * 0.22;

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

  for (let y = 0; y < size; y++) {
    const rowOff = y * (size * 4 + 1);
    raw[rowOff] = 0;
    for (let x = 0; x < size; x++) {
      const px = x + 0.5 - cx;
      const py = y + 0.5 - cy;
      const dBox = sdRoundedBox(px, py, r, r, corner);

      let R = 0, G = 0, B = 0, A = 0;
      if (dBox < 1.0) {
        const bgAlpha = Math.max(0, Math.min(1, 0.5 - dBox));
        // Sleek neutral dark graphite gradient (zero violet/blue tint)
        const t = (y / size);
        R = 18 + t * 14;
        G = 18 + t * 14;
        B = 20 + t * 15;

        // Subtle brushed metal inner rim
        if (dBox > -size * 0.035) {
          R += 36; G += 36; B += 38;
        }

        // Minimalist geometric "A" / Aether mark in crisp brushed silver
        const s = size;
        const topX = 0, topY = -s * 0.24;
        const leftX = -s * 0.20, leftY = s * 0.21;
        const rightX = s * 0.20, rightY = s * 0.21;
        const midLX = -s * 0.10, midLY = s * 0.05;
        const midRX = s * 0.14, midRY = s * 0.01;
        const stroke = Math.max(1.4, s * 0.052);

        const d1 = distSeg(px, py, topX, topY, leftX, leftY);
        const d2 = distSeg(px, py, topX, topY, rightX, rightY);
        const d3 = distSeg(px, py, midLX, midLY, midRX, midRY);
        const dMark = Math.min(d1, d2, d3);

        // Soft neutral silver glow
        const glow = Math.exp(-Math.pow(dMark / (s * 0.11), 2)) * 0.22;
        R += glow * 210;
        G += glow * 210;
        B += glow * 218;

        const markAlpha = Math.max(0, Math.min(1, (stroke - dMark) * 1.1 + 0.5));
        if (markAlpha > 0) {
          const grad = (px / (s * 0.4) + 0.5);
          const mR = 232 - grad * 48;
          const mG = 232 - grad * 48;
          const mB = 236 - grad * 46;
          R = R * (1 - markAlpha) + mR * markAlpha;
          G = G * (1 - markAlpha) + mG * markAlpha;
          B = B * (1 - markAlpha) + mB * markAlpha;
        }

        A = bgAlpha * 255;
      }

      const off = rowOff + 1 + x * 4;
      raw[off] = Math.max(0, Math.min(255, Math.round(R)));
      raw[off + 1] = Math.max(0, Math.min(255, Math.round(G)));
      raw[off + 2] = Math.max(0, Math.min(255, Math.round(B)));
      raw[off + 3] = Math.max(0, Math.min(255, Math.round(A)));
    }
  }

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  const idat = zlib.deflateSync(raw, { level: 9 });
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', idat),
    pngChunk('IEND', Buffer.alloc(0))
  ]);
}

function makeIco(sizes) {
  const pngs = sizes.map(s => ({ size: s, data: makePng(s) }));
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(pngs.length, 4);

  let offset = 6 + pngs.length * 16;
  const entries = pngs.map(({ size, data }) => {
    const e = Buffer.alloc(16);
    e[0] = size === 256 ? 0 : size;
    e[1] = size === 256 ? 0 : size;
    e[2] = 0; e[3] = 0;
    e.writeUInt16LE(1, 4);
    e.writeUInt16LE(32, 6);
    e.writeUInt32LE(data.length, 8);
    e.writeUInt32LE(offset, 12);
    offset += data.length;
    return e;
  });

  return Buffer.concat([header, ...entries, ...pngs.map(p => p.data)]);
}

async function main() {
  console.log(`Fetching ${ICONS.length} Lucide SVG icons from unpkg.com...`);
  const xamlLines = [
    '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
    '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
    '    <!-- Genuine Lucide Icons downloaded from https://unpkg.com/lucide-static -->'
  ];

  let ok = 0;
  for (const name of ICONS) {
    const filePath = path.join(ICONS_DIR, `${name}.svg`);
    let svg;
    if (fs.existsSync(filePath)) {
      svg = fs.readFileSync(filePath, 'utf8');
    } else {
      const url = `https://unpkg.com/lucide-static@0.469.0/icons/${name}.svg`;
      const res = await fetch(url);
      if (!res.ok) {
        console.warn(`WARN: failed ${name} (${res.status})`);
        continue;
      }
      svg = await res.text();
      fs.writeFileSync(filePath, svg, 'utf8');
    }
    const geom = svgToWpfPaths(svg);
    if (geom) {
      xamlLines.push(`    <Geometry x:Key="Icon.${toPascal(name)}">${geom}</Geometry>`);
      ok++;
    }
  }

  xamlLines.push('</ResourceDictionary>');
  fs.writeFileSync(path.join(THEMES_DIR, 'Icons.xaml'), xamlLines.join('\n') + '\n', 'utf8');

  const ico = makeIco([16, 32, 48, 64, 128, 256]);
  fs.writeFileSync(path.join(ASSETS_DIR, 'aether.ico'), ico);
  fs.writeFileSync(path.join(ASSETS_DIR, 'aether-256.png'), makePng(256));

  console.log(`Done! Downloaded ${ok}/${ICONS.length} real Lucide SVG icons + generated Icons.xaml & aether.ico`);
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
