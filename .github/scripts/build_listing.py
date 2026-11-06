"""GitHub Releases から VPM リスティング (index.json) と案内ページ (index.html) を生成する。

各リリースの `<package>-<version>.zip` を取得し、zip 内の package.json に
`url` と `zipSHA256` を付与して versions に登録する。
"""

import argparse
import hashlib
import html
import io
import json
import os
import sys
import urllib.parse
import urllib.request
import zipfile

PACKAGE_NAME = os.environ.get("PACKAGE_NAME", "com.chikumatateshina.unity-assets-optimizer")
LISTING_ID = "com.chikumatateshina.vpm"
LISTING_NAME = "ChikumaTateshina VPM Repository"
AUTHOR = "ChikumaTateshina"


def api(url, token):
    req = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json"})
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req) as res:
        return json.load(res)


def download(url):
    with urllib.request.urlopen(url) as res:
        return res.read()


def list_releases(repo, token):
    page = 1
    while True:
        items = api(f"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}", token)
        if not items:
            return
        yield from items
        page += 1


def build(repo, token):
    owner, name = repo.split("/")
    pages_url = f"https://{owner.lower()}.github.io/{name}/"
    versions = {}
    for release in list_releases(repo, token):
        if release.get("draft"):
            continue
        for asset in release.get("assets", []):
            asset_name = asset["name"]
            if not (asset_name.startswith(PACKAGE_NAME + "-") and asset_name.endswith(".zip")):
                continue
            data = download(asset["browser_download_url"])
            with zipfile.ZipFile(io.BytesIO(data)) as zf:
                manifest = json.loads(zf.read("package.json").decode("utf-8-sig"))
            if manifest.get("name") != PACKAGE_NAME:
                print(f"skip {asset_name}: package name mismatch", file=sys.stderr)
                continue
            manifest["url"] = asset["browser_download_url"]
            manifest["zipSHA256"] = hashlib.sha256(data).hexdigest()
            versions[manifest["version"]] = manifest
            print(f"added {PACKAGE_NAME}@{manifest['version']}")

    listing = {
        "name": LISTING_NAME,
        "id": LISTING_ID,
        "url": pages_url + "index.json",
        "author": AUTHOR,
        "packages": {PACKAGE_NAME: {"versions": versions}} if versions else {},
    }
    return listing, pages_url


def render_html(listing, pages_url, repo):
    listing_url = pages_url + "index.json"
    vcc = "vcc://vpm/addRepo?url=" + urllib.parse.quote(listing_url, safe="")
    rows = []
    for pkg, entry in listing["packages"].items():
        for version in sorted(entry["versions"], key=lambda v: [int(x) if x.isdigit() else x for x in v.replace("-", ".").split(".")], reverse=True):
            m = entry["versions"][version]
            rows.append(f"<tr><td>{html.escape(m.get('displayName', pkg))}</td><td>{html.escape(version)}</td>"
                        f"<td><a href=\"{html.escape(m['url'])}\">zip</a></td></tr>")
    return f"""<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(LISTING_NAME)}</title>
<style>body{{font-family:system-ui,sans-serif;max-width:720px;margin:2rem auto;padding:0 16px;line-height:1.6}}
code{{background:#f2f2f2;padding:2px 4px;border-radius:4px;word-break:break-all}}
a.button{{display:inline-block;padding:.6rem 1rem;background:#2563eb;color:#fff;border-radius:6px;text-decoration:none}}
table{{border-collapse:collapse;width:100%}}td,th{{border-bottom:1px solid #ddd;padding:.4rem;text-align:left}}</style></head>
<body><h1>{html.escape(LISTING_NAME)}</h1>
<p><a class="button" href="{html.escape(vcc)}">VCC に追加</a></p>
<p>手動で追加する場合は VCC の Settings → Packages → Add Repository に次の URL を入力してください。</p>
<p><code>{html.escape(listing_url)}</code></p>
<table><tr><th>Package</th><th>Version</th><th>Download</th></tr>{''.join(rows)}</table>
<p><a href="https://github.com/{html.escape(repo)}">GitHub</a></p>
</body></html>
"""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default="_site")
    args = parser.parse_args()
    repo = os.environ["GITHUB_REPOSITORY"]
    token = os.environ.get("GITHUB_TOKEN")

    listing, pages_url = build(repo, token)
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "index.json"), "w", encoding="utf-8") as f:
        json.dump(listing, f, ensure_ascii=False, indent=2)
    with open(os.path.join(args.out, "index.html"), "w", encoding="utf-8") as f:
        f.write(render_html(listing, pages_url, repo))
    open(os.path.join(args.out, ".nojekyll"), "w").close()


if __name__ == "__main__":
    main()
