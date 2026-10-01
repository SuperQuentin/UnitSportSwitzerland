// Generate EZ-Tree presets headless and dump their geometry as JSON for the Godot prototype.
globalThis.document = { createElementNS: () => ({ style: {}, addEventListener() {}, removeEventListener() {} }), createElement: () => ({ style: {}, getContext: () => null }) };
globalThis.self = globalThis;
const THREE = await import('three');
// textures are applied in Godot; stop three from trying to load images in Node
THREE.TextureLoader.prototype.load = function () { return new THREE.Texture(); };
const { Tree } = await import('@dgreenheck/ez-tree');
import fs from 'fs';
// game-ready cuts: fewer sections/segments, fewer but larger leaf cards
const GAME = {
  'Pine Medium': o => { o.branch.children[0] = 34; o.branch.sections = {0: 6, 1: 3, 2: 3, 3: 2}; o.branch.segments = {0: 5, 1: 3, 2: 3, 3: 3};
                        o.leaves.count = 14; o.leaves.size *= 2.2; },
  'Ash Medium':  o => { o.branch.levels = 2; o.branch.children = {0: 6, 1: 4, 2: 3}; o.branch.sections = {0: 6, 1: 4, 2: 3, 3: 2};
                        o.branch.segments = {0: 6, 1: 4, 2: 3, 3: 3}; o.leaves.count = 16; o.leaves.size *= 2.2; },
};
const presets = process.argv.slice(2);
for (const name of presets) {
  const tree = new Tree();
  tree.loadPreset(name);
  if (GAME[name]) GAME[name](tree.options);
  tree.options.seed = 1234;
  tree.generate();
  const out = { name, parts: {} };
  let tris = 0;
  tree.traverse(o => {
    if (!o.isMesh) return;
    const g = o.geometry; g.computeBoundingBox();
    const part = o.name || (out.parts.bark ? 'leaves' : 'bark');
    const a = k => g.attributes[k] ? Array.from(g.attributes[k].array) : null;
    const idx = g.index ? Array.from(g.index.array) : null;
    tris += (idx ? idx.length : g.attributes.position.count) / 3;
    out.parts[part] = { position: a('position'), normal: a('normal'), uv: a('uv'), index: idx, bbox: [g.boundingBox.min, g.boundingBox.max] };
  });
  fs.writeFileSync(`${name.replace(/ /g, '_').toLowerCase()}_game.json`, JSON.stringify(out));
  console.log(name, Object.keys(out.parts), 'tris', tris, JSON.stringify(Object.values(out.parts).map(p => p.bbox)));
}
