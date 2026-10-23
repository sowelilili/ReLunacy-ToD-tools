# Tools of Destruction: wall jumps and glitched wallbounces

How ToD (NPEA00452) puts Ratchet on a wall and pushes him off, and why some tilted surfaces launch
him far faster than intended. Traced in Ghidra and checked against live RPCS3 memory (read through
PINE) on Stratus City. Function names below are the labels added to the Ghidra project.

## Finding Ratchet

- Hero struct: `*( *(0x10740BB4) + 0xC684 )` (from RaCMAN). Feet position `+0x1260`, moby `+0x14`.
- Main state machine at `+0x14B0`, secondary at `+0x1F68`. `+4` is a handle; resolve it through
  `0x005E72DC` / `0x0063B3D0` (TOC `0x8CB054`): context `*(*(TOC-0x4604))`, table
  `*(*(TOC-0x70AC)) + ctx*0x30`, object = `pool(+4) + u16[slots(+0) + (handle>>16)*8 + 4] * stride(+0x20)`.
  The state object's `+8` is its id.
- Wall-jump state id: **`0x3CD`**.
- Movement controller at `+0x1120` (called M below): `+0x00` desired velocity, `+0x10` direction,
  `+0x20` speed, `+0x24` vertical speed, `+0x28` mode (3 in the air), `+0x30` direction override
  (flag `+0x2C & 2`), `+0x110` velocity, `+0x320` up.

## The wall probe (`Hero_UpdateWallProbe`, `0x0025A3AC`)

Every frame a sphere (radius 0.3) is swept from 1.0 above Ratchet's feet (`hero+0x1450` = up) along
his movement direction, about 1.7 long. On a hit it stores the normal (`+0x1AD0`), point (`+0x1AE0`),
distance (`+0x1B10`), a hit flag (`+0x1B5D`), the hit object (`+0x1A60`) and the hit triangle's flag
byte (`+0x1B5F`).

## Entering the wall state (`Hero_TryEnterWallJumpState`, `0x0020822C`)

All of these must hold (TOC `0x8AB078` constants in brackets):

1. Probe hit closer than 0.7 (`+0x5B00`).
2. Falling: `+0x1334 < 0` (`+0x5B04`).
3. The hit is level collision, not a moby.
4. The hit triangle's flag bit 0 is clear (`(+0x1B5F & 1) == 0`).
5. A timer check passes (`FUN_00010790`).
6. A **line** (no radius, `FUN_00011DF0`) from feet + 2.75 up (`+0x5B18`), 1.5 along Ratchet's
   facing (`+0x5B1C`), hits anything.

Nothing checks the angle of the surface the probe hit. Its normal becomes the push direction
(`HeroWallJumpState_Enter`, `0x00223648`, copies it to state `+0x80`).

## Push-off (`HeroWallJumpState_UpdatePushOff`, `0x00223950`)

The state sets acceleration 60/s, deceleration 60/s and a target speed of 6.0 (`FUN_0025BCBC`,
TOC `+0x6778`/`+0x677C`). Each frame:

1. `speed` moves from `|M.velocity|` toward 6.0 by at most 1 (at 60 fps).
2. `M.desired = speed * n + vz * up`, with `vz` = 9.49 (the jump's vertical speed).
3. The integrator (`FUN_006453DC`) measures Ratchet's real motion and stores its projection onto
   `n` as `M.velocity` (`FUN_006B3DE4` is a projection).

So `|velocity| ~= speed + vz * n.y`. Off a vertical wall `n.y ~= 0` and speed settles at 6.0
(measured 5.86 with `n.y = -0.02`). When `n` tilts upward, `vz * n.y` is added every frame while the
push-off can only take 1 away, so speed runs away: measured 6 -> 10.9 -> 12.1 -> 13.0 ... -> ~14.7
with `n.y = 0.52`, without the ravager. That is the glitched wallbounce. The threshold is
`vz * n.y > 1`, i.e. `n.y > ~0.105`, a tilt of about 6 degrees from vertical; more tilt grows faster.

## Which surfaces can do it

The probe (1.0 above the feet) has to land on the tilted surface while the head line (2.75 above)
still finds something within 1.5. On the Stratus City spot measured live (world collision instance
8, triangle normal (-0.81, 0.52, -0.26), flags 0, material 1) the head line does **not** hit the
slope: it hits the vertical wall above it (instance 37, normal (-0.95, -0.02, -0.31)). So the typical
glitch spot is a tilted surface at the foot of a wall. A tilted surface on its own only passes the
head check when it is steep enough to reach 2.75 high within 1.5, roughly within 20-25 degrees of
vertical.

The same corner gives a regular jump when the probe happens to hit the wall instead of the slope.

## Simulating it over a level

`dev/wbsim_np.py` (numpy, not in the repo) places a virtual Ratchet against every static, bit-0-clear
triangle tilted more than 6 degrees from vertical: probe origin 0.35 or 0.55 off the surface at four
points on the triangle, facing into it. A triangle passes when the probe swept along the facing
reaches it first, the column from the feet to the head line is open, and the head line hits a
surface with bit 0 clear.

Stratus City (321,642 placed triangles): 131,898 tilted candidates, 24,943 pass, including the
measured spot. By area that is 2.7% of the level's collision, and most of it is gently tilted:

| Tilt from vertical | Passing area | Boost per frame (`9.49 * n.y - 1`) |
| --- | --- | --- |
| 6-10 degrees | 13,832 | up to 0.65 |
| 10-20 degrees | 53,286 | 0.65 to 2.2 |
| 20-30 degrees | 2,870 | 2.2 to 3.7 |
| 30-40 degrees | 2,832 | 3.7 to 5.1 |
| 40-60 degrees | 2,906 | 5.1 to 7.2 |

Most passing triangles (23,556 of 24,943) are TIE collision. The model ignores Ratchet's real
approach path and the timer check, so it marks where the entry check can pass, not where it is easy
to set up.

## In ReLunacy

Render > Collision Colours > Show Glitched Wallbounces runs the same check
(`ReLunacy.Engine/Assets/Collision/WallbounceAnalysis.cs`, a parallel C# port of the numpy script:
1.0 s for Stratus City on 12 threads, against 289 s in numpy) and colours passing triangles yellow
to red by tilt, above a minimum tilt set by a slider (20 degrees by default). It runs once per level,
the first time the mode is used, and caches the result in
`%LOCALAPPDATA%\ReLunacy\Cache\wallbounce-v1-<hash of collision.dat>.bin`.
