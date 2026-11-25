"""Builds the RetroArcDo HD Studio synthetic NES test cartridges.

Variants (all NROM, legally distributable, generated from this source):
  hdstudio_test.nes         8x8 sprites, CHR-ROM, 2-frame player, same-looking enemies
  hdstudio_test_8x16.nes    8x16 sprite mode, CHR-ROM
  hdstudio_test_mirror.nes  8x8 sprites with horizontal / vertical / both-axis flips
  hdstudio_test_chrram.nes  8x8 sprites, CHR-RAM initialized at runtime, animated CHR update

The scene intentionally uses simple geometric graphics so wrong dimensions, flips or
tile segmentation are visible without any game-specific knowledge.

Usage:
  python test-roms/build_synthetic_roms.py --out test-roms
"""
import argparse
import os

OPS = {
    ("sei", None): 0x78, ("cld", None): 0xD8, ("txs", None): 0x9A, ("inx", None): 0xE8, ("dex", None): 0xCA,
    ("iny", None): 0xC8, ("clc", None): 0x18, ("pha", None): 0x48, ("pla", None): 0x68, ("rti", None): 0x40,
    ("tax", None): 0xAA, ("txa", None): 0x8A,
    ("lda", "imm"): 0xA9, ("ldx", "imm"): 0xA2, ("ldy", "imm"): 0xA0, ("cpx", "imm"): 0xE0, ("cpy", "imm"): 0xC0,
    ("adc", "imm"): 0x69, ("and", "imm"): 0x29,
    ("sta", "abs"): 0x8D, ("lda", "abs"): 0xAD, ("bit", "abs"): 0x2C, ("jmp", "abs"): 0x4C,
    ("lda", "absx"): 0xBD, ("lda", "absy"): 0xB9, ("sta", "absx"): 0x9D,
    ("sta", "zp"): 0x85, ("lda", "zp"): 0xA5, ("inc", "zp"): 0xE6,
    ("bpl", "rel"): 0x10, ("bne", "rel"): 0xD0, ("beq", "rel"): 0xF0,
}
SIZE = {None: 1, "imm": 2, "zp": 2, "rel": 2, "abs": 3, "absx": 3, "absy": 3}


def assemble(lines, origin):
    labels, pc = {}, origin
    for item in lines:
        if isinstance(item, str):
            labels[item] = pc
        elif item[0] == ".bytes":
            pc += len(item[1])
        else:
            pc += SIZE[item[1]]
    out, pc = bytearray(), origin
    for item in lines:
        if isinstance(item, str):
            continue
        if item[0] == ".bytes":
            out += bytes(item[1])
            pc += len(item[1])
            continue
        op, mode = item[0], item[1]
        arg = item[2] if len(item) > 2 else None
        if isinstance(arg, str):
            arg = labels[arg]
        out.append(OPS[(op, mode)])
        if mode in ("imm", "zp"):
            out.append(arg & 0xFF)
        elif mode == "rel":
            off = arg - (pc + 2)
            assert -128 <= off <= 127, (op, off)
            out.append(off & 0xFF)
        elif mode in ("abs", "absx", "absy"):
            out += bytes([arg & 0xFF, arg >> 8])
        pc += SIZE[mode]
    return out, labels


def tile(rows):
    lo, hi = [], []
    for r in rows:
        l = h = 0
        for c in r:
            v = ".123".index(c)
            l = (l << 1) | (v & 1)
            h = (h << 1) | (v >> 1)
        lo.append(l)
        hi.append(h)
    return bytes(lo + hi)


def split16(rows):
    return [tile([r[:8] for r in rows[:8]]), tile([r[8:] for r in rows[:8]]),
            tile([r[:8] for r in rows[8:]]), tile([r[8:] for r in rows[8:]])]


PLAYER_A = [
    "......1111......", ".....122221.....", "....12222221....", "....12322321....",
    "....12222221....", ".....122221.....", "......1331......", "....11333311....",
    "...1.133331.1...", "...1.133331.1...", ".....133331.....", ".....111111.....",
    ".....11..11.....", "....11....11....", "...11......11...", "..111......111..",
]
PLAYER_B = PLAYER_A[:12] + [
    ".....11..11.....", ".....11..11.....", ".....11..11.....", "....111..111....",
]
ENEMY = [
    "................", ".....111111.....", "...1122222211...", "..122222222221..",
    ".12233222233221.", ".12231222231221.", ".12222222222221.", ".12222222222221.",
    "..122211112221..", "...1222222221...", "....11111111....", "...1.1....1.1...",
    "..11.11..11.11..", "..1...1..1...1..", "................", "................",
]
BRICK = tile(["33333333", "21112111", "21112111", "33333333", "11211121", "11211121", "33333333", "21112111"])
GROUND = tile(["22222222", "21212121", "12121212", "11111111", "11311111", "11111131", "11111111", "13111111"])
#A deliberately asymmetric 2x2 background metatile (tiles 1,2,17,18) for block replacement.
BG_BLOCK_TOP = tile(["33333333", "32112112", "32112112", "33333333", "11111111", "31213121", "31213121", "33333333"])
BG_BLOCK_BOTTOM = tile(["12211221", "12211221", "33333333", "31113111", "31113111", "33333333", "21212121", "21212121"])
UNUSED = [tile([("%s" % "123"[(i + y) % 3]) * 8 for y in range(8)]) for i in range(6)]

PALETTE = [0x21, 0x07, 0x17, 0x27] * 4 + [0x21, 0x0F, 0x16, 0x36, 0x21, 0x0F, 0x1A, 0x2A, 0x21, 0x0F, 0x13, 0x23, 0x21, 0x0F, 0x16, 0x36]
ROWS = [0] * 22 + [2] + [1] * 7
PLAYER_Y = 22 * 8 - 16 - 1
PX, FRAME = 0x10, 0x11


def build_chr():
    chr_rom = bytearray(8192)
    def put(index, data):
        chr_rom[index * 16:index * 16 + 16] = data
    for i, t in enumerate(split16(PLAYER_A)):
        put(1 + i, t)
    for i, t in enumerate(split16(ENEMY)):
        put(5 + i, t)
    for i, t in enumerate(split16(PLAYER_B)):
        put(11 + i, t)
    for i, t in enumerate(UNUSED):
        put(20 + i, t)
    put(256 + 1, GROUND)
    put(256 + 2, BRICK)
    put(256 + 17, BG_BLOCK_TOP)
    put(256 + 18, BG_BLOCK_BOTTOM)
    for i, t in enumerate(UNUSED[:4]):
        put(256 + 3 + i, t)
    return chr_rom


def build_chr_8x16():
    chr_rom = bytearray(8192)
    def put(index, data):
        chr_rom[index * 16:index * 16 + 16] = data
    a = split16(PLAYER_A)
    put(2, a[0]); put(3, a[2]); put(4, a[1]); put(5, a[3])
    b = split16(PLAYER_B)
    put(10, b[0]); put(11, b[2]); put(12, b[1]); put(13, b[3])
    e = split16(ENEMY)
    put(6, e[0]); put(7, e[2]); put(8, e[1]); put(9, e[3])
    put(256 + 1, GROUND)
    put(256 + 2, BRICK)
    put(256 + 17, BG_BLOCK_TOP)
    put(256 + 18, BG_BLOCK_BOTTOM)
    return chr_rom


def build(variant, out_dir):
    chr_ram = variant == "chrram"
    chr_data = build_chr_8x16() if variant == "8x16" else build_chr()
    sprite_size_8x16 = variant == "8x16"

    # OAM tile indices differ between 8x8 and 8x16 sprite modes.
    p_tile_a = (2, 4) if sprite_size_8x16 else (1, 2)
    p_tile_b = (10, 12) if sprite_size_8x16 else (11, 12)
    e_tile = (6, 8) if sprite_size_8x16 else (5, 6)
    player_y = PLAYER_Y
    enemy_y = PLAYER_Y - 40

    # Optional per-enemy OAM flips for the mirroring scene.
    e1_attr, e2_attr = 0, 0
    if variant == "mirror":
        #Same pattern and palette, opposite flip axes: the only distinguishing context
        #is the OAM flip, which standard hmirror/vmirror conditions can express.
        e1_attr, e2_attr = 0x40, 0x80

    sprites = []
    if sprite_size_8x16:
        sprites += [player_y, p_tile_a[0], 0, 40, player_y, p_tile_a[1], 0, 48]
        sprites += [enemy_y, e_tile[0], 1 | e1_attr, 80, enemy_y, e_tile[1], 1 | e1_attr, 88]
        sprites += [enemy_y, e_tile[0], 1 | e2_attr, 170, enemy_y, e_tile[1], 1 | e2_attr, 178]
    else:
        sprites += [player_y, 1, 0, 40, player_y, 2, 0, 48, player_y + 8, 3, 0, 40, player_y + 8, 4, 0, 48]
        sprites += [enemy_y, 5, 1 | e1_attr, 80, enemy_y, 6, 1 | e1_attr, 88,
                    enemy_y + 8, 7, 1 | e1_attr, 80, enemy_y + 8, 8, 1 | e1_attr, 88]
        sprites += [enemy_y, 5, 1 | e2_attr, 170, enemy_y, 6, 1 | e2_attr, 178,
                    enemy_y + 8, 7, 1 | e2_attr, 170, enemy_y + 8, 8, 1 | e2_attr, 178]

    ppu_ctrl = 0xB0 if sprite_size_8x16 else 0x90

    prg = [
        "reset", ("sei", None), ("cld", None), ("ldx", "imm", 0xFF), ("txs", None),
        ("lda", "imm", 0), ("sta", "abs", 0x2000), ("sta", "abs", 0x2001),
        "vw1", ("bit", "abs", 0x2002), ("bpl", "rel", "vw1"),
        "vw2", ("bit", "abs", 0x2002), ("bpl", "rel", "vw2"),
    ]

    if chr_ram:
        # Initialize sprite and background pattern tables from PRG data.
        prg += [
            ("lda", "imm", 0x00), ("sta", "abs", 0x2006), ("sta", "abs", 0x2006),
            ("ldx", "imm", 0),
            "copy0", ("lda", "absx", "chrlo"), ("sta", "abs", 0x2007), ("inx", None), ("bne", "rel", "copy0"),
            ("lda", "imm", 0x10), ("sta", "abs", 0x2006), ("lda", "imm", 0x00), ("sta", "abs", 0x2006),
            ("ldx", "imm", 0),
            "copy1", ("lda", "absx", "chrhi"), ("sta", "abs", 0x2007), ("inx", None), ("bne", "rel", "copy1"),
        ]

    prg += [
        ("lda", "imm", 0x3F), ("sta", "abs", 0x2006), ("lda", "imm", 0x00), ("sta", "abs", 0x2006), ("ldx", "imm", 0),
        "pal", ("lda", "absx", "palette"), ("sta", "abs", 0x2007), ("inx", None), ("cpx", "imm", 32), ("bne", "rel", "pal"),
        ("lda", "imm", 0x20), ("sta", "abs", 0x2006), ("lda", "imm", 0x00), ("sta", "abs", 0x2006), ("ldy", "imm", 0),
        "row", ("lda", "absy", "rows"), ("ldx", "imm", 32),
        "col", ("sta", "abs", 0x2007), ("dex", None), ("bne", "rel", "col"),
        ("iny", None), ("cpy", "imm", 30), ("bne", "rel", "row"),
        #A known 2x2 background metatile at row 10, columns 16-17.
        ("lda", "imm", 0x21), ("sta", "abs", 0x2006), ("lda", "imm", 0x50), ("sta", "abs", 0x2006),
        ("lda", "imm", 1), ("sta", "abs", 0x2007), ("lda", "imm", 2), ("sta", "abs", 0x2007),
        ("lda", "imm", 0x21), ("sta", "abs", 0x2006), ("lda", "imm", 0x70), ("sta", "abs", 0x2006),
        ("lda", "imm", 0x11), ("sta", "abs", 0x2007), ("lda", "imm", 0x12), ("sta", "abs", 0x2007),
        ("lda", "imm", 0), ("ldx", "imm", 64),
        "attr", ("sta", "abs", 0x2007), ("dex", None), ("bne", "rel", "attr"),
        ("lda", "imm", 0xFF), ("ldx", "imm", 0),
        "hide", ("sta", "absx", 0x0200), ("inx", None), ("bne", "rel", "hide"),
        "spr", ("lda", "absx", "sprites"), ("sta", "absx", 0x0200), ("inx", None), ("cpx", "imm", len(sprites)), ("bne", "rel", "spr"),
        ("lda", "imm", 40), ("sta", "zp", PX),
        "vw3", ("bit", "abs", 0x2002), ("bpl", "rel", "vw3"),
        ("lda", "imm", 0), ("sta", "abs", 0x2005), ("sta", "abs", 0x2005),
        ("lda", "imm", ppu_ctrl), ("sta", "abs", 0x2000), ("lda", "imm", 0x1E), ("sta", "abs", 0x2001),
        "forever", ("jmp", "abs", "forever"),

        "nmi", ("pha", None), ("txa", None), ("pha", None),
        ("inc", "zp", FRAME),
        ("lda", "zp", PX), ("clc", None), ("adc", "imm", 1), ("sta", "zp", PX),
        # Only the first two OAM entries are the player's vertical halves in 8x16 mode.
        ("sta", "abs", 0x0203),
    ]
    if sprite_size_8x16:
        prg += [
            ("clc", None), ("adc", "imm", 8), ("sta", "abs", 0x0207),
        ]
    else:
        prg += [
            ("sta", "abs", 0x020B),
            ("clc", None), ("adc", "imm", 8), ("sta", "abs", 0x0207), ("sta", "abs", 0x020F),
        ]

    if sprite_size_8x16:
        prg += [
            ("ldx", "imm", p_tile_a[0]), ("lda", "zp", FRAME), ("and", "imm", 0x08), ("beq", "rel", "frameA"), ("ldx", "imm", p_tile_b[0]),
            "frameA", ("txa", None), ("sta", "abs", 0x0201), ("clc", None), ("adc", "imm", 2), ("sta", "abs", 0x0205),
        ]
    else:
        prg += [
            ("ldx", "imm", 1), ("lda", "zp", FRAME), ("and", "imm", 0x08), ("beq", "rel", "frameA"), ("ldx", "imm", 11),
            "frameA", ("txa", None), ("sta", "abs", 0x0201), ("clc", None), ("adc", "imm", 1), ("sta", "abs", 0x0205),
            ("clc", None), ("adc", "imm", 1), ("sta", "abs", 0x0209), ("clc", None), ("adc", "imm", 1), ("sta", "abs", 0x020D),
        ]

    if chr_ram:
        # Animate one CHR-RAM pattern at runtime: the capture must identity it by content.
        prg += [
            ("lda", "abs", 0x2002),  # dummy read is intentionally harmless; keeps cycle shape stable
            ("lda", "zp", FRAME), ("and", "imm", 0x40), ("beq", "rel", "dynA"),
            ("lda", "imm", 0x00), ("sta", "abs", 0x2006), ("lda", "imm", 0x50), ("sta", "abs", 0x2006),
            ("ldy", "imm", 0),
            "dynB", ("lda", "absy", "dynpatB"), ("sta", "abs", 0x2007), ("iny", None), ("cpy", "imm", 16), ("bne", "rel", "dynB"),
            ("jmp", "abs", "dynDone"),
            "dynA", ("lda", "imm", 0x00), ("sta", "abs", 0x2006), ("lda", "imm", 0x50), ("sta", "abs", 0x2006),
            ("ldy", "imm", 0),
            "dynAloop", ("lda", "absy", "dynpatA"), ("sta", "abs", 0x2007), ("iny", None), ("cpy", "imm", 16), ("bne", "rel", "dynAloop"),
            "dynDone",
        ]

    prg += [
        ("lda", "imm", 0), ("sta", "abs", 0x2003), ("lda", "imm", 2), ("sta", "abs", 0x4014),
        ("lda", "imm", 0), ("sta", "abs", 0x2005), ("sta", "abs", 0x2005), ("lda", "imm", ppu_ctrl), ("sta", "abs", 0x2000),
        ("pla", None), ("tax", None), ("pla", None),
        "irq", ("rti", None),
        "palette", (".bytes", PALETTE),
        "rows", (".bytes", ROWS),
        "sprites", (".bytes", sprites),
    ]
    if chr_ram:
        prg += [
            "chrlo", (".bytes", list(chr_data[:256])),
            "chrhi", (".bytes", list(chr_data[0x1000:0x1100])),
            "dynpatA", (".bytes", list(chr_data[5 * 16:6 * 16])),
            "dynpatB", (".bytes", list(chr_data[5 * 16:6 * 16][::-1])),
        ]

    code, labels = assemble(prg, 0xC000)
    prg_rom = bytearray([0xFF]) * 16384
    prg_rom[:len(code)] = code
    for addr, label in ((0xFFFA, "nmi"), (0xFFFC, "reset"), (0xFFFE, "irq")):
        v = labels[label]
        prg_rom[addr - 0xC000] = v & 0xFF
        prg_rom[addr - 0xC000 + 1] = v >> 8

    chr_banks = 0 if chr_ram else 1
    header = bytes([0x4E, 0x45, 0x53, 0x1A, 1, chr_banks, 0, 0] + [0] * 8)
    name = {
        "nrom": "hdstudio_test.nes",
        "8x16": "hdstudio_test_8x16.nes",
        "mirror": "hdstudio_test_mirror.nes",
        "chrram": "hdstudio_test_chrram.nes",
    }[variant]
    path = os.path.join(out_dir, name)
    with open(path, "wb") as f:
        f.write(header + bytes(prg_rom))
        if not chr_ram:
            f.write(bytes(chr_data))
    print(f"{name}: code {len(code)} bytes, sprites {len(sprites)//4}, chr-ram {chr_ram}, 8x16 {sprite_size_8x16}")
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument("--variant", choices=["nrom", "8x16", "mirror", "chrram", "all"], default="all")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    variants = ["nrom", "8x16", "mirror", "chrram"] if args.variant == "all" else [args.variant]
    for variant in variants:
        build(variant, args.out)


if __name__ == "__main__":
    main()
