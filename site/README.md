# MemEco 公式サイト

`site/` は Cloudflare Pages で配信する静的サイトです。ビルド不要（HTML / CSS / JS のみ）。

- 公開 URL: https://memeco.moyashi.xyz
- 構成: `index.html` / `assets/` / `_headers` / `robots.txt` / `sitemap.xml` / `404.html`

## ローカル確認

```powershell
python -m http.server 8080 --directory site
# http://127.0.0.1:8080/ を開く
```

## デプロイ（Cloudflare Pages）

```powershell
# 初回のみ
wrangler login
wrangler pages project create memeco --production-branch main

# デプロイ
wrangler pages deploy site --project-name memeco --branch main
```

カスタムドメイン `memeco.moyashi.xyz` は Pages プロジェクトに追加し、
`moyashi.xyz` ゾーンに CNAME（`memeco` → `memeco.pages.dev`）を作成します。

## 画像の再生成

```powershell
python tools/make_ogp.py
```

`docs/preview.png`（アプリの `--preview` 出力）を元に `site/assets/ogp.png` を生成します。
