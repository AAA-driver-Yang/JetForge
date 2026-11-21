import os, sys, zipfile, hashlib
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

stamp = sys.argv[1]
root = os.environ.get("JETFORGE_BACKUP_DIR",
                      os.path.join(os.path.dirname(ROOT), "_backup"))
stage = os.path.join(root, "JetForge_src_" + stamp)
zip_path = os.path.join(root, "JetForge_src_%s.zip" % stamp)
assert os.path.isdir(stage), stage

n = 0
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for dirpath, dirnames, filenames in os.walk(stage):
        dirnames.sort()
        for f in sorted(filenames):
            full = os.path.join(dirpath, f)
            rel = os.path.relpath(full, root).replace("\\", "/")
            z.write(full, rel)
            n += 1

h = hashlib.sha256()
with open(zip_path, "rb") as fh:
    for chunk in iter(lambda: fh.read(1 << 20), b""):
        h.update(chunk)

with zipfile.ZipFile(zip_path) as z:
    bad = z.testzip()
    names = z.namelist()

print("files=%d entries=%d testzip=%s size=%.1fMB" % (n, len(names), bad, os.path.getsize(zip_path) / 1048576.0))
print("sha256=%s" % h.hexdigest())

# 校验和文件放在 stage 里，同时补进 zip？不——zip 已封口。写在备份根目录一份。
sums = os.path.join(root, "SHA256SUMS.txt")
with open(sums, "a", encoding="utf-8") as f:
    f.write("%s  JetForge_src_%s.zip\n" % (h.hexdigest(), stamp))
    bundle = os.path.join(stage, "JetForge_history_%s.bundle" % stamp)
    if os.path.isfile(bundle):
        bh = hashlib.sha256()
        with open(bundle, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                bh.update(chunk)
        f.write("%s  JetForge_src_%s/JetForge_history_%s.bundle\n" % (bh.hexdigest(), stamp, stamp))
print("校验和已追加到", sums)