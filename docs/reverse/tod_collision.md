# Tools of Destruction: collision.dat

Level collision for ToD (NPEA00452). One file per level, next to `main.dat`. It is **not** an IGHW
container: it is a raw big-endian blob that the game relocates in place after loading.

This is the first decoding, kept for its EBOOT trail. ReLunacy now reads collision for every game with
upstream's `ReLunacy.Engine/Assets/Collision/LevelCollision.cs`, documented in `collision.md`, which
re-checks and extends what is below.

## EBOOT

| Address | What |
| --- | --- |
| `0x1002CFD8` | `"/app_home/%s/collision.dat"` |
| `0x1002BFC8` | `"Packed level has no collision data. Re-run 'lb' and 'cb' ..."` |
| `0x004C2970` | Level collision init. Prints the warning above when header `+0x04` (instance count) is 0, then calls `0x004E0760` (runtime state) and `0x004E0970`. |
| `0x004E0970` | Relocates the file: turns header offsets into pointers, adds the mesh base to every instance's `+0x74`, fixes the grid's cell pointers, and derives the grid dimensions from the bounds. |

The EBOOT has three TOC bases (`0x8AB078`, `0x8BB068`, `0x8CB054`). Ghidra resolves TOC loads with the
first one only, which is why the string references above show no code xrefs in the listing.

## Header (0x80 bytes)

| Offset | Type | Meaning |
| --- | --- | --- |
| `0x00` | u32 | Instance array offset (0x80 on every retail file) |
| `0x04` | u32 | Instance count |
| `0x08` | u32 | Mesh blob offset (instance `+0x74` is relative to this) |
| `0x0C` | u32 | Mesh blob length |
| `0x10..0x2E` | | Table offsets and u16 counts copied to a runtime struct (0 on the levels checked) |
| `0x20` | f32 | Grid cell size (10.0) |
| `0x24` | f32 | 0.1, used with the cell size when sizing the grid |
| `0x30` | u32 | Spatial grid offset (`+0x14` cell words `count << 20 \| first`, `+0x18` entries, `+0x2C` cell count) |
| `0x38` | u8 | Copied to the runtime struct (0x47 on cobalia) |
| `0x3C` | u32 | Offset to another table (not decoded) |
| `0x40` | vec3 | Level bounds min |
| `0x50` | vec3 | Level bounds max |

## Instance (0x80 bytes)

| Offset | Type | Meaning |
| --- | --- | --- |
| `0x00` | mat4 | Mesh to world, row-vector (translation at `+0x30`), same layout as a TIE placement |
| `0x40` | vec4 | World bounding sphere |
| `0x50` | vec3 | World box min |
| `0x60` | vec3 | World box max. About 2% of instances have one side trimmed short of the mesh. |
| `0x70` | u32 | Bit 29 set: TIE collision. Low 28 bits count up across the chunks of one TIE class. |
| `0x74` | u32 | Mesh offset in the blob |
| `0x78` | u32 | Low 16: mesh byte length (padded to 0x10). High 16: 0x6A / 0x6E / 0x6F, not closed. |

TIE collision: each instance's matrix equals a `0x9240` TIE placement's exactly (3852 of 3858 TIEs
on cobalia, all 2228 on fastoon). World collision: identity matrix, the mesh is already in world space.

## Mesh

| Offset | Type | Meaning |
| --- | --- | --- |
| `0x00` | u16 | Vertex count (max 512 seen) |
| `0x02` | u16 | Vertex block offset (0x60 everywhere) |
| `0x04` | u16 | 1 everywhere |
| `0x06` | u16 | Triangle count |
| `0x08` | u16 | Triangle offset |
| `0x0C` | u16 | Material offset (one byte per triangle) |
| `0x14` | 3 x vec4 | Oriented box: axis xyz + half extent |
| `0x44` | vec3 | Oriented box centre |
| `0x50` | f32 | 1.0 everywhere |

Vertex block: `s32 x, y, z` origin, 4 bytes padding, then groups of 8 vertices as three lines of
8 x u16 (x line, y line, z line). Position = (origin + value) / 1024.

Inside each line the slots are ordered **0, 4, 1, 5, 2, 6, 3, 7**, so vertex `k` of a group is at
slot `2k` (k < 4) or `2(k - 4) + 1`. Read in plain order, the triangles still index valid vertices
but come out scrambled. Triangle pairs that form quads are 96.5% planar on metropolis in this order
against 46% in plain order. Bounds checks cannot catch this, since they don't depend on vertex order.

Triangle: one u32. Indices in bits 18-26, 9-17, 0-8 (in that order), flags in bits 27-31 (not
closed). No index is out of range and none are degenerate on any of the six levels checked. Winding is
consistent: `cross(b - a, c - a)` points out of the surface. About 99% of shared edges run in opposite
directions in their two triangles (Stratus City, cobalia, metropolis), and most near-flat triangles face
up; the ones facing down are ceilings and undersides. A mirrored placement (negative determinant) flips
it. (An earlier note here said winding was inconsistent; that was measured before the vertex slot order
was known, with scrambled vertices.)

## Open

- Triangle flags (bits 27-31) and the instance `+0x78` high half.
- What each material byte means in game (surface type, footstep sound, damage). The renderer
  only colours by it.
- Header tables at `+0x10..+0x2E` and `+0x3C`, and the grid's entry layout. They're only needed for
  queries, not for drawing.
