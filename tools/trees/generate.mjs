// Generates the realistic styles' trees: EZ-Tree (MIT, https://github.com/dgreenheck/ez-tree)
// presets cut to a game budget, written as .glb into assets/realistic/trees, with the bark and
// leaf textures they use. Run from this folder: npm install, then node generate.mjs.
//
// Each tree is scaled to the unit tree every style shares (radius 1 at its widest, height 1 to
// the top of the crown, base at the origin): the instance transform gives it its size
// (Terrain/ChunkNode.Pack). Two primitives, "bark" and "leaves"; the game assigns their materials
// (Styles/ModelCatalog), so the files carry geometry only.
globalThis.document = {
  createElementNS: () => ({ style: {}, addEventListener() {}, removeEventListener() {} }),
  createElement: () => ({ style: {}, getContext: () => null }),
};
globalThis.self = globalThis;
const THREE = await import('three');
// textures are applied in Godot: stop three from loading images in Node
THREE.TextureLoader.prototype.load = function () { return new THREE.Texture(); };
const { Tree } = await import('@dgreenheck/ez-tree');
import fs from 'fs';
import path from 'path';

const OUT = path.resolve('../../assets/realistic/trees');
const EZ = path.resolve('node_modules/@dgreenheck/ez-tree/src/lib/assets');

// game cuts: fewer sections and segments, fewer but larger leaf cards (~2-3.5k triangles)
const TREES = [
  {
    file: 'conifer', preset: 'Pine Medium', bark: 'pine', leaves: 'pine',
    cut: o => {
      o.branch.children[0] = 34;
      o.branch.sections = { 0: 6, 1: 3, 2: 3, 3: 2 };
      o.branch.segments = { 0: 5, 1: 3, 2: 3, 3: 3 };
      o.leaves.count = 14;
      o.leaves.size *= 2.2;
    },
  },
  {
    file: 'broadleaf', preset: 'Ash Medium', bark: 'oak', leaves: 'ash',
    cut: o => {
      o.branch.levels = 2;
      o.branch.children = { 0: 6, 1: 4, 2: 3 };
      o.branch.sections = { 0: 6, 1: 4, 2: 3, 3: 2 };
      o.branch.segments = { 0: 6, 1: 4, 2: 3, 3: 3 };
      o.leaves.count = 16;
      o.leaves.size *= 2.2;
    },
  },
];

fs.mkdirSync(OUT, { recursive: true });
for (const t of TREES) {
  const tree = new Tree();
  tree.loadPreset(t.preset);
  t.cut(tree.options);
  tree.options.seed = 1234;
  tree.generate();

  const parts = {};
  tree.traverse(o => {
    if (!o.isMesh) return;
    const name = o.name || (parts.bark ? 'leaves' : 'bark');
    parts[name] = o.geometry;
  });

  // unit tree: height 1 to the crown's top, radius 1 at its widest
  let height = 0, radius = 0;
  for (const g of Object.values(parts)) {
    const p = g.attributes.position.array;
    for (let i = 0; i < p.length; i += 3) {
      height = Math.max(height, p[i + 1]);
      radius = Math.max(radius, Math.hypot(p[i], p[i + 2]));
    }
  }
  const prims = [];
  let tris = 0;
  for (const name of ['bark', 'leaves']) {
    const g = parts[name];
    const p = g.attributes.position.array, n = g.attributes.normal.array, uv = g.attributes.uv.array;
    const count = p.length / 3;
    const pos = new Float32Array(count * 3), nrm = new Float32Array(count * 3), tex = new Float32Array(count * 2);
    for (let i = 0; i < count; i++) {
      const x = p[i * 3] / radius, y = p[i * 3 + 1] / height, z = p[i * 3 + 2] / radius;
      pos.set([x, y, z], i * 3);
      let nx, ny, nz;
      if (name === 'leaves') {
        // leaf cards lit as one rounded crown, not as hundreds of flat planes
        const ox = x, oy = (y - 0.6) * 2, oz = z, ol = Math.hypot(ox, oy, oz) || 1;
        nx = ox / ol * 0.8; ny = oy / ol * 0.8 + 0.35; nz = oz / ol * 0.8;
      } else {
        // non-uniform scale: normals go through the inverse transpose
        nx = n[i * 3] * radius; ny = n[i * 3 + 1] * height; nz = n[i * 3 + 2] * radius;
      }
      const l = Math.hypot(nx, ny, nz) || 1;
      nrm.set([nx / l, ny / l, nz / l], i * 3);
      // glTF's UV origin is the top left, three's the bottom left
      tex.set([uv[i * 2], 1 - uv[i * 2 + 1]], i * 2);
    }
    const index = g.index ? Uint32Array.from(g.index.array) : Uint32Array.from({ length: count }, (_, i) => i);
    tris += index.length / 3;
    prims.push({ name, pos, nrm, tex, index });
  }
  fs.writeFileSync(path.join(OUT, `${t.file}.glb`), glb(t.file, prims));
  fs.copyFileSync(path.join(EZ, 'bark', `${t.bark}_color_1k.jpg`), path.join(OUT, `bark_${t.bark}_color.jpg`));
  fs.copyFileSync(path.join(EZ, 'bark', `${t.bark}_normal_1k.jpg`), path.join(OUT, `bark_${t.bark}_normal.jpg`));
  fs.copyFileSync(path.join(EZ, 'leaves', `${t.leaves}_color.png`), path.join(OUT, `leaves_${t.leaves}.png`));
  console.log(`${t.file}: ${t.preset}, ${tris} triangles`);
}

// A minimal binary glTF: one mesh, one primitive per part, each with its own (empty) material so
// the importer keeps them as separate surfaces.
function glb(name, prims) {
  const views = [], accessors = [], chunks = [];
  let offset = 0;
  const add = (array, target, componentType, type, count, minmax) => {
    const bytes = Buffer.from(array.buffer, array.byteOffset, array.byteLength);
    const pad = (4 - (bytes.length % 4)) % 4;
    chunks.push(bytes, Buffer.alloc(pad));
    views.push({ buffer: 0, byteOffset: offset, byteLength: bytes.length, target });
    offset += bytes.length + pad;
    const acc = { bufferView: views.length - 1, componentType, count, type };
    if (minmax) Object.assign(acc, minmax);
    accessors.push(acc);
    return accessors.length - 1;
  };
  const bounds = a => {
    const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
    for (let i = 0; i < a.length; i += 3)
      for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], a[i + k]); max[k] = Math.max(max[k], a[i + k]); }
    return { min, max };
  };
  const primitives = prims.map((p, i) => ({
    attributes: {
      POSITION: add(p.pos, 34962, 5126, 'VEC3', p.pos.length / 3, bounds(p.pos)),
      NORMAL: add(p.nrm, 34962, 5126, 'VEC3', p.nrm.length / 3),
      TEXCOORD_0: add(p.tex, 34962, 5126, 'VEC2', p.tex.length / 2),
    },
    indices: add(p.index, 34963, 5125, 'SCALAR', p.index.length),
    material: i,
  }));
  const json = {
    asset: { version: '2.0', generator: 'UnitSportSwitzerland tools/trees (EZ-Tree)' },
    scene: 0, scenes: [{ nodes: [0] }], nodes: [{ name, mesh: 0 }],
    meshes: [{ name, primitives }],
    materials: prims.map(p => ({ name: p.name })),
    buffers: [{ byteLength: offset }], bufferViews: views, accessors,
  };
  let text = Buffer.from(JSON.stringify(json));
  text = Buffer.concat([text, Buffer.alloc((4 - (text.length % 4)) % 4, 0x20)]);
  const bin = Buffer.concat(chunks);
  const header = Buffer.alloc(12);
  header.writeUInt32LE(0x46546c67, 0); header.writeUInt32LE(2, 4);
  header.writeUInt32LE(12 + 8 + text.length + 8 + bin.length, 8);
  const chunk = (buf, type) => { const h = Buffer.alloc(8); h.writeUInt32LE(buf.length, 0); h.writeUInt32LE(type, 4); return Buffer.concat([h, buf]); };
  return Buffer.concat([header, chunk(text, 0x4e4f534a), chunk(bin, 0x004e4942)]);
}
