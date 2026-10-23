"""Check the package's local reference closure and synthetic asset layout.

Run with Python 3: python Tools~/verify_package.py
Does not import assets, compile shaders or certify runtime rendering.
"""
from pathlib import Path
import json
import math
import re
import struct


ROOT = Path(__file__).resolve().parents[1]
errors = []


def require(condition, message):
    if not condition:
        errors.append(message)


def included(path):
    return not any(part.startswith('.') for part in path.relative_to(ROOT).parts)


manifest = json.loads((ROOT / 'package.json').read_text(encoding='utf-8'))
require(manifest['name'] == 'com.markus.gaussian-raytracing', 'Unexpected package identity; update paths and checks if intentionally renamed.')
require(manifest['unity'] == '6000.3', 'Unexpected minimum Unity version.')
for package in ('com.unity.render-pipelines.high-definition', 'com.unity.render-pipelines.core',
                'com.unity.burst', 'com.unity.collections', 'com.unity.mathematics'):
    require(package in manifest['dependencies'], 'Missing dependency: ' + package)
for sample in manifest.get('samples', []):
    require((ROOT / sample['path']).is_dir(), 'Missing sample: ' + sample['path'])

guid_paths = {}
files = [p for p in ROOT.rglob('*') if p.is_file() and included(p)]
for meta in (p for p in files if p.suffix == '.meta'):
    match = re.search(r'^guid: ([0-9a-f]{32})$', meta.read_text(encoding='utf-8'), re.M)
    require(match is not None, 'Missing GUID: ' + str(meta.relative_to(ROOT)))
    if not match:
        continue
    guid = match[1]
    require(guid not in guid_paths, 'Duplicate GUID: ' + guid)
    guid_paths[guid] = Path(str(meta)[:-5])
    require(guid_paths[guid].exists(), 'Orphan metadata: ' + str(meta.relative_to(ROOT)))

for file in files:
    relative = file.relative_to(ROOT)
    if file.suffix != '.meta' and not any(part.endswith('~') for part in relative.parts):
        require(Path(str(file) + '.meta').is_file(), 'Missing asset metadata: ' + str(relative))
    if file.suffix in ('.meta', '.asset', '.mat', '.prefab', '.unity'):
        for guid in re.findall(r'guid: ([0-9a-f]{32})', file.read_text(encoding='utf-8')):
            if guid.startswith('0000000000000000'):
                continue  # Unity built-in assets.
            require(guid in guid_paths, f'Unresolved GUID in {relative}: {guid}')

assemblies = {json.loads(f.read_text())['name'] for f in files if f.suffix == '.asmdef'}
unity_assemblies = {'Unity.Burst', 'Unity.Collections', 'Unity.Mathematics',
                   'Unity.RenderPipelines.Core.Runtime', 'Unity.RenderPipelines.HighDefinition.Runtime'}
for file in (p for p in files if p.suffix == '.asmdef'):
    definition = json.loads(file.read_text())
    for reference in definition.get('references', []):
        require(reference in assemblies | unity_assemblies,
                f'Unknown assembly reference in {file.name}: {reference}')

for file in (p for p in files if p.suffix in ('.shader', '.hlsl', '.compute', '.raytrace')):
    for include in re.findall(r'#include\s+"([^"]+)"', file.read_text(encoding='utf-8')):
        if include in ('UnityCG.cginc', 'UnityRayTracingMeshUtils.cginc'):
            continue
        if include.startswith('Packages/'):
            package = include.split('/')[1]
            require(package in manifest['dependencies'] or package == manifest['name'],
                    f'Undeclared package include in {file.name}: {include}')
            if package != manifest['name']:
                continue
            resolved = ROOT / '/'.join(include.split('/')[2:])
        else:
            resolved = file.parent / include
        require(resolved.is_file(), f'Missing include in {file.name}: {include}')

resources = ROOT / 'Runtime/Resources/Gaussian3DGRT'
for name in ('Gaussian3DGRayTracing.raytrace', 'Gaussian3DGRTCommon.hlsl',
             'Gaussian3DGRTProcedural.shader', 'Gaussian3DGRTTransmission.shader', 'Gaussian3DGRTComposite.shader'):
    require((resources / name).is_file(), 'Missing ray resource: ' + name)
for variant in ('DeviceRadixSort', 'FidelityFX'):
    compute = (ROOT / f'Shaders/SplatUtilities_{variant}.compute').read_text()
    require('#pragma kernel CSDecode3DGRT' in compute, 'Missing decode kernel in ' + variant)

data = ROOT / 'Samples~/BasicHybridScene/Data'
count = 512
for name, stride in [('position', 12), ('other', 16), ('color', 16), ('sh', 192)]:
    require((data / (name + '.bytes')).stat().st_size == count * stride, 'Unexpected sample byte layout: ' + name)
for position in struct.iter_unpack('<3f', (data / 'position.bytes').read_bytes()):
    require(all(math.isfinite(v) and abs(v) <= .72 for v in position), 'Invalid sample position')
for rotation, sx, sy, sz in struct.iter_unpack('<I3f', (data / 'other.bytes').read_bytes()):
    require(rotation >> 30 == 3 and min(sx, sy, sz) > 0, 'Invalid sample rotation/scale')
for color in struct.iter_unpack('<4f', (data / 'color.bytes').read_bytes()):
    require(all(math.isfinite(v) and 0 <= v <= 1 for v in color), 'Invalid sample RGBA')
require(not any((data / 'sh.bytes').read_bytes()), 'Expected zero higher-order SH in sample')
asset = (data / 'SyntheticGaussians.asset').read_text()
require('m_SplatCount: 512' in asset and 'm_FormatVersion: 20240801' in asset, 'Sample asset header mismatch')
require('c182f369a4014188aab4f45d74e006c9' in (data / 'SyntheticGaussians.asset.meta').read_text(), 'Sample builder GUID mismatch')

for name in ('Apache-2.0-NVIDIA.txt', 'MIT-Aras.txt', 'MIT-Anatomy.txt', 'MIT-AMD.txt',
             'MIT-DeviceRadixSort.txt', 'MIT-JSON.txt', 'MIT-Package-Contributors.txt'):
    require((ROOT / 'Licenses' / name).is_file(), 'Missing license text: ' + name)

if errors:
    raise SystemExit('\n'.join(errors))
print(f'PASS: {len(files)} files, {len(guid_paths)} GUIDs, {len(assemblies)} assemblies; local references and synthetic data layout are consistent.')
