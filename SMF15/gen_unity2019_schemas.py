#!/usr/bin/env python3
import argparse, hashlib, json, os, struct

try:
    from Crypto.Hash import MD4
except ImportError as e:
    raise SystemExit('pycryptodome is required for MD4 type-tree hashes') from e

UNITY = '2019.4.30f1'
SERIALIZED = 21


def pick(obj, *names):
    for name in names:
        if name in obj:
            return obj[name]
    return None


def node_children(n):
    return pick(n, 'SubNodes', 'subNodes', 'Children', 'children') or []


def node_name(n):
    return pick(n, 'Name', 'name') or ''


def node_type(n):
    return pick(n, 'TypeName', 'typeName', 'Type', 'type') or ''


def node_int(n, *names):
    value = pick(n, *names)
    return int(value or 0)


def md4_tree(root):
    h = MD4.new()
    def rec(n):
        h.update(node_type(n).encode('utf-8'))
        h.update(node_name(n).encode('utf-8'))
        h.update(struct.pack('<i', node_int(n, 'ByteSize', 'byteSize')))
        h.update(struct.pack('<i', node_int(n, 'TypeFlags', 'typeFlags')))
        h.update(struct.pack('<i', node_int(n, 'Version', 'version')))
        h.update(struct.pack('<i', node_int(n, 'MetaFlag', 'metaFlag') & 0x4000))
        for c in node_children(n):
            rec(c)
    rec(root)
    return h.hexdigest().upper()


def compact(n):
    return {
        'n': node_name(n),
        't': node_type(n),
        'v': node_int(n, 'Version', 'version'),
        'f': node_int(n, 'TypeFlags', 'typeFlags'),
        'm': node_int(n, 'MetaFlag', 'metaFlag'),
        'c': [compact(c) for c in node_children(n)],
    }


def find_class(data, class_id):
    classes = pick(data, 'Classes', 'classes')
    if not isinstance(classes, list):
        raise RuntimeError('InfoJson has no Classes array')
    for c in classes:
        cid = pick(c, 'TypeID', 'TypeId', 'typeID', 'typeId')
        if cid is not None and int(cid) == class_id:
            root = pick(c, 'ReleaseRootNode', 'releaseRootNode') or pick(c, 'EditorRootNode', 'editorRootNode')
            if root is None:
                raise RuntimeError(f'class {class_id} has no root node')
            return c, root
    raise RuntimeError(f'class {class_id} not found')


def collect_paths(root):
    out = set()
    def rec(n, prefix):
        name = node_name(n)
        cur = f'{prefix}/{name}' if prefix else name
        out.add(cur)
        for c in node_children(n):
            rec(c, cur)
    rec(root, '')
    return out


def write_schema(out_path, class_id, root):
    schema = {
        'Format': 1,
        'UnityVersion': UNITY,
        'SerializedVersion': SERIALIZED,
        'ClassId': class_id,
        'TypeHash': md4_tree(root),
        'Root': compact(root),
    }
    raw = json.dumps(schema, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, 'wb') as f:
        f.write(raw)
    return schema, hashlib.sha256(raw).hexdigest().upper(), len(raw)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--input', required=True)
    ap.add_argument('--shader-out', required=True)
    ap.add_argument('--bundle-out', required=True)
    ap.add_argument('--info-out')
    args = ap.parse_args()

    with open(args.input, 'r', encoding='utf-8-sig') as f:
        data = json.load(f)

    _, shader_root = find_class(data, 48)
    _, bundle_root = find_class(data, 142)
    shader, shader_sha, shader_len = write_schema(args.shader_out, 48, shader_root)
    bundle, bundle_sha, bundle_len = write_schema(args.bundle_out, 142, bundle_root)

    shader_paths = collect_paths(shader_root)
    bundle_paths = collect_paths(bundle_root)
    required_shader_suffixes = [
        'Base/m_Name',
        'Base/m_Dependencies/Array',
        'Base/m_NonModifiableTextures/Array',
        'Base/m_ParsedForm/m_Dependencies/Array',
        'Base/m_ParsedForm/m_FallbackName',
        'Base/m_ParsedForm/m_SubShaders/Array/data/m_Passes/Array/data/m_State/rtBlend0/srcBlend/val',
        'Base/m_ParsedForm/m_SubShaders/Array/data/m_Passes/Array/data/m_State/rtBlend0/destBlend/val',
        'Base/m_ParsedForm/m_SubShaders/Array/data/m_Passes/Array/data/m_State/rtBlend0/srcBlendAlpha/val',
        'Base/m_ParsedForm/m_SubShaders/Array/data/m_Passes/Array/data/m_State/rtBlend0/destBlendAlpha/val',
        'Base/platforms/Array',
        'Base/compressedBlob/Array',
    ]
    required_bundle_suffixes = [
        'Base/m_Name', 'Base/m_PreloadTable/Array', 'Base/m_Container/Array', 'Base/m_AssetBundleName'
    ]
    missing_shader = [p for p in required_shader_suffixes if p not in shader_paths]
    missing_bundle = [p for p in required_bundle_suffixes if p not in bundle_paths]

    lines = [
        f'unity={UNITY}', f'serialized={SERIALIZED}',
        f'shaderTypeHash={shader["TypeHash"]}', f'shaderSchemaSha256={shader_sha}', f'shaderSchemaBytes={shader_len}',
        f'bundleTypeHash={bundle["TypeHash"]}', f'bundleSchemaSha256={bundle_sha}', f'bundleSchemaBytes={bundle_len}',
        'shaderMissing=' + (','.join(missing_shader) if missing_shader else 'none'),
        'bundleMissing=' + (','.join(missing_bundle) if missing_bundle else 'none'),
        'shaderRoot=' + node_name(shader_root) + ':' + node_type(shader_root),
        'bundleRoot=' + node_name(bundle_root) + ':' + node_type(bundle_root),
    ]
    text = '\n'.join(lines) + '\n'
    print(text, end='')
    if args.info_out:
        with open(args.info_out, 'w', encoding='utf-8') as f:
            f.write(text)
    if missing_shader or missing_bundle:
        raise SystemExit('required 2019 schema paths are missing')

if __name__ == '__main__':
    main()
