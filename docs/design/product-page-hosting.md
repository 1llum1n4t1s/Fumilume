# 製品ページの配信先

製品ページの配信HTMLは `../vps-web/lp/fumilume/`（編集元は `../vps-web/tools/lp/templates/`）、公開実体はVPSの `/srv/www/lp/fumilume/` です。Cloudflare側の中継設定は `../vps-web/deploy/lp-gateways/fumilume/` に置きます。公開ドメインは `fumilume.kagayoi.com`（ランディングページ、固定セットアップURL、更新マニフェストとパッケージ）、配布成果物の正本は Cloudflare R2 `fumilume-updates` です。

配信手順と既存通信の維持規約は [AGENTS.md](../../AGENTS.md#製品ページの配信先) を参照してください。
