# Encodes a set of strings with the QR encoder taken straight from web/index.html (run in
# Node) and decodes every result with qr_decode.py. Needs Node and Python.
#   python tests/qr/check_qr.py
import json, os, random, re, subprocess, sys, tempfile

here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
from qr_decode import decode

page = open(os.path.join(here, '..', '..', 'web', 'index.html'), encoding='utf-8').read()
start = page.index('const QR = (() => {')
end = page.index('})();', start) + len('})();')
encoder = page[start:end]

random.seed(7)
texts = ['A', 'http://192.168.178.81:8123/?t=0123456789abcdef0123456789abcdef',
         'http://10.0.0.5:8123/?t=ffffffffffffffffffffffffffffffff', 'Grüße ✓']
texts += ['x' * n for n in (14, 15, 26, 27, 62, 63, 106, 107, 122, 123, 180, 213)]
alphabet = 'abcdefghijklmnopqrstuvwxyz0123456789:/.?=&-_ '
texts += [''.join(random.choice(alphabet) for _ in range(n)) for n in range(1, 214, 3)]

runner = encoder + """
const texts = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf-8'));
const out = texts.map(t => { const q = QR.encode(t); return { text: t, rows: q.modules.map(r => r.map(v => v ? 1 : 0).join('')) }; });
process.stdout.write(JSON.stringify(out));
"""
with tempfile.TemporaryDirectory() as tmp:
    js, data = os.path.join(tmp, 'run.js'), os.path.join(tmp, 'texts.json')
    open(js, 'w', encoding='utf-8').write(runner)
    json.dump(texts, open(data, 'w', encoding='utf-8'))
    result = subprocess.run(['node', js, data], capture_output=True, text=True, encoding='utf-8')
    if result.returncode != 0:
        print(result.stderr); sys.exit(1)
    encoded = json.loads(result.stdout)

failed = 0
for item in encoded:
    try:
        text, version, mask = decode(item['rows'])
        if text != item['text']:
            raise AssertionError('text differs')
    except AssertionError as error:
        failed += 1
        print('FAIL', repr(item['text'][:30]), error)
print(len(encoded) - failed, 'of', len(encoded), 'codes decode back to the exact text')
sys.exit(1 if failed else 0)
