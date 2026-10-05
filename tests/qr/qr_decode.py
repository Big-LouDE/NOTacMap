# Independent QR decoder for checking the encoder in web/index.html. It reads a module
# matrix back using the standard's own block table and a separate Reed-Solomon check,
# so it does not share code with the encoder. Used by check_qr.py.
import json, sys
from collections import Counter

# Block layout for error correction level M, from the standard's table:
# version -> (error correction codewords per block, [(blocks, data codewords per block), ...])
M_TABLE = {
    1: (10, [(1, 16)]), 2: (16, [(1, 28)]), 3: (26, [(1, 44)]), 4: (18, [(2, 32)]),
    5: (24, [(2, 43)]), 6: (16, [(4, 27)]), 7: (18, [(4, 31)]),
    8: (22, [(2, 38), (2, 39)]), 9: (22, [(3, 36), (2, 37)]), 10: (26, [(4, 43), (1, 44)]),
}
ALIGN = {1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34], 7: [6, 22, 38],
         8: [6, 24, 42], 9: [6, 26, 46], 10: [6, 28, 50]}

# GF(256) with the QR polynomial 0x11D, via log/exp tables (the encoder multiplies bitwise instead)
EXP, LOG = [0] * 512, [0] * 256
x = 1
for i in range(255):
    EXP[i] = x
    LOG[x] = i
    x <<= 1
    if x & 0x100:
        x ^= 0x11D
for i in range(255, 512):
    EXP[i] = EXP[i - 255]


def gmul(a, b):
    return 0 if a == 0 or b == 0 else EXP[LOG[a] + LOG[b]]


def bch_ok(value, gen, gen_bits):
    # a valid BCH word divides evenly by the generator
    for shift in range(value.bit_length() - gen_bits, -1, -1):
        if value >> (shift + gen_bits - 1) & 1:
            value ^= gen << shift
    return value == 0


def decode(rows):
    size = len(rows)
    m = [[c == '1' for c in r] for r in rows]
    ver = (size - 17) // 4
    assert 1 <= ver <= 10 and size == ver * 4 + 17, 'bad size'

    # ---- format information, both copies
    def fmt_copy1():
        pos = [(8, i) for i in range(6)] + [(8, 7), (8, 8), (7, 8)] + [(14 - i, 8) for i in range(9, 15)]
        return sum(int(m[y][x]) << i for i, (x, y) in enumerate(pos))

    def fmt_copy2():
        pos = [(size - 1 - i, 8) for i in range(8)] + [(8, size - 15 + i) for i in range(8, 15)]
        return sum(int(m[y][x]) << i for i, (x, y) in enumerate(pos))

    f1, f2 = fmt_copy1() ^ 0x5412, fmt_copy2() ^ 0x5412
    assert f1 == f2, 'format copies differ'
    assert bch_ok(f1, 0x537, 11), 'format BCH invalid'
    assert (f1 >> 13) == 0, 'not level M'
    mask = (f1 >> 10) & 7

    # ---- which modules are function patterns
    fn = [[False] * size for _ in range(size)]

    def block(x0, y0, w, h):
        for y in range(y0, y0 + h):
            for x in range(x0, x0 + w):
                if 0 <= x < size and 0 <= y < size:
                    fn[y][x] = True
    block(0, 0, 9, 9); block(size - 8, 0, 8, 9); block(0, size - 8, 9, 8)   # finders + separators + format
    block(6, 0, 1, size); block(0, 6, size, 1)                              # timing
    pos = ALIGN[ver]
    for i, cx in enumerate(pos):
        for j, cy in enumerate(pos):
            if (i == 0 and j == 0) or (i == 0 and j == len(pos) - 1) or (i == len(pos) - 1 and j == 0):
                continue
            block(cx - 2, cy - 2, 5, 5)
    if ver >= 7:
        block(size - 11, 0, 3, 6); block(0, size - 11, 6, 3)
        # version information must be valid too
        bits = 0
        for i in range(18):
            a, b = size - 11 + i % 3, i // 3
            bits |= int(m[b][a]) << i
        assert bch_ok(bits, 0x1F25, 13) and (bits >> 12) == ver, 'version info invalid'
    fn[size - 8][8] = True   # dark module

    # ---- unmask and read the data bits in the zigzag order
    def inv(x, y):
        return [(x + y) % 2 == 0, y % 2 == 0, x % 3 == 0, (x + y) % 3 == 0,
                (x // 3 + y // 2) % 2 == 0, x * y % 2 + x * y % 3 == 0,
                (x * y % 2 + x * y % 3) % 2 == 0, ((x + y) % 2 + x * y % 3) % 2 == 0][mask]

    bits = []
    right, upward = size - 1, True
    while right >= 1:
        if right == 6:
            right = 5
        ys = range(size - 1, -1, -1) if upward else range(size)
        for y in ys:
            for x in (right, right - 1):
                if not fn[y][x]:
                    bits.append(m[y][x] ^ inv(x, y))
        upward = not upward
        right -= 2
    ecc_len, groups = M_TABLE[ver]
    block_data_lens = [d for count, d in groups for _ in range(count)]
    n_blocks = len(block_data_lens)
    total_cw = sum(block_data_lens) + ecc_len * n_blocks
    cws = [int(''.join('1' if b else '0' for b in bits[i * 8:i * 8 + 8]), 2) for i in range(total_cw)]
    assert len(bits) >= total_cw * 8, 'not enough modules for the codewords'

    # ---- de-interleave
    pos_i = 0
    data_blocks = [[] for _ in range(n_blocks)]
    for col in range(max(block_data_lens)):
        for b in range(n_blocks):
            if col < block_data_lens[b]:
                data_blocks[b].append(cws[pos_i]); pos_i += 1
    ecc_blocks = [[] for _ in range(n_blocks)]
    for col in range(ecc_len):
        for b in range(n_blocks):
            ecc_blocks[b].append(cws[pos_i]); pos_i += 1
    assert pos_i == total_cw

    # ---- error correction must check out: the codeword polynomial vanishes at alpha^0..alpha^(n-1)
    for b in range(n_blocks):
        word = data_blocks[b] + ecc_blocks[b]
        for k in range(ecc_len):
            acc = 0
            for c in word:
                acc = gmul(acc, EXP[k]) ^ c
            assert acc == 0, f'Reed-Solomon check failed (block {b}, root {k})'

    # ---- read the text
    stream = ''.join(f'{c:08b}' for blk in data_blocks for c in blk)
    assert stream[:4] == '0100', 'not byte mode'
    cc_bits = 8 if ver < 10 else 16
    n = int(stream[4:4 + cc_bits], 2)
    body = stream[4 + cc_bits:4 + cc_bits + 8 * n]
    data = bytes(int(body[i:i + 8], 2) for i in range(0, 8 * n, 8))
    return data.decode('utf-8'), ver, mask


if __name__ == '__main__':
    tests = json.load(open(sys.argv[1], encoding='utf-8'))
    ok, bad = 0, []
    masks, versions = Counter(), Counter()
    for t in tests:
        try:
            text, ver, mask = decode(t['rows'])
            if text == t['text']:
                ok += 1; masks[mask] += 1; versions[ver] += 1
            else:
                bad.append((t['text'][:30], 'text differs'))
        except AssertionError as e:
            bad.append((t['text'][:30], str(e)))
    print(f'{ok} of {len(tests)} decode back to the exact text')
    print('versions covered:', dict(sorted(versions.items())))
    print('masks used:', dict(sorted(masks.items())))
    for b in bad[:10]:
        print('FAIL', b)
    sys.exit(0 if not bad else 1)
