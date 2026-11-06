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

PACKAGE_NAME = os.environ.get("PACKAGE_NAME", "com.chikumatateshina.unityassetsoptimizer")
LISTING_ID = "com.chikumatateshina.vpm"
LISTING_NAME = "UnityAssetsOptimizer"
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


TEMPLATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "pages", "index.template.html")


def version_key(v):
    return [int(x) if x.isdigit() else -1 for x in v.split("-")[0].split(".")]


def render_html(listing, pages_url, repo):
    listing_url = pages_url + "index.json"
    vcc = "vcc://vpm/addRepo?url=" + urllib.parse.quote(listing_url, safe="")
    rows = []
    latest = "-"
    for pkg, entry in listing["packages"].items():
        versions = sorted(entry["versions"], key=version_key, reverse=True)
        if versions and pkg == PACKAGE_NAME:
            latest = versions[0]
        for version in versions:
            m = entry["versions"][version]
            rows.append("<tr><td>" + html.escape(m.get("displayName", pkg)) + "</td><td>" + html.escape(version) +
                        "</td><td><a href=\"" + html.escape(m["url"]) + "\">zip</a></td></tr>")
    page = open(TEMPLATE, encoding="utf-8").read()
    values = {
        "{{LISTING_URL}}": html.escape(listing_url),
        "{{VCC_URL}}": html.escape(vcc),
        "{{REPO}}": html.escape(repo),
        "{{LATEST_VERSION}}": html.escape(latest),
        "{{VERSION_ROWS}}": "\n        ".join(rows) or "<tr><td colspan=\"3\">まだ公開されたバージョンはありません</td></tr>",
    }
    for key, value in values.items():
        page = page.replace(key, value)
    return page


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
