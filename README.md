![GitHub release (latest by date)](https://img.shields.io/github/v/release/AmariNoa/BLM-Integration-Core?label=release)
![GitHub release (by tag)](https://img.shields.io/github/downloads/AmariNoa/BLM-Integration-Core/latest/total)
![GitHub all releases](https://img.shields.io/github/downloads/AmariNoa/BLM-Integration-Core/total?label=total%20downloads)
![GitHub issues](https://img.shields.io/github/issues/AmariNoa/BLM-Integration-Core)
![GitHub stars](https://img.shields.io/github/stars/AmariNoa/BLM-Integration-Core)

# BLM Integration Core

本パッケージは、BOOTH Library Managerがローカルに保持するデータを読み取り、UnityEditor上で利用できるように統合する非公式のUnityパッケージです。

## はじめに

本プロジェクトはBOOTHおよびpixivから正式な承認を受けておらず、非公式のツールとして開発されています。

※ BOOTH公式への個別確認を行っていない理由は、スクレイピングガイドライン等に"悪用防止の観点から詳細な基準についての個別のお問い合わせにはお答えいたしかねます"と記載されているためです。

本パッケージは非公式に開発されているもののため、使い方や不具合に関するお問い合わせをBOOTHおよびpixivへ行わないでください。

本パッケージは、下記に記載する各規約およびガイドラインに基づいて設計・開発されています。

ただし、BOOTHまたはpixivから公開停止の要請があった場合には、速やかに公開を停止します。

## BOOTH Library ManagerおよびBOOTH関連データの取り扱い

- 本パッケージは、BOOTH Library ManagerとBOOTHの利用規約、およびBOOTHのスクレイピングガイドラインに従って開発しています。
- データの取得は、一般的なデータベースツール(例: DB Browser for SQLite)で確認できる範囲に限り、ローカルデータベースからデータを読み取ります。
- BOOTH Library Manager本体のリバースエンジニアリングや改造は行っていません。
- 実用上必要な範囲において、BOOTHのサーバーから商品のサムネイルファイルをダウンロードし、ローカルにキャッシュすることで、BOOTHサーバーへのアクセス負荷を軽減しています。
- BOOTH関係者の方で懸念事項がある場合は、記載のメールアドレスまでご連絡ください: `amarinoa@outlook.jp`

## パッケージ開発における AI の取り扱い

- 本パッケージの開発にはCodexを使用しています。

---

# BLM Integration Core (EN README)

This package is an unofficial Unity package that reads data stored locally by BOOTH Library Manager and integrates it so it can be used in UnityEditor.

## Introduction

This project has not received official approval from BOOTH or pixiv, and is being developed as an unofficial tool.

* The reason we do not make individual inquiries to BOOTH is that the scraping guidelines etc. state, "From the perspective of preventing abuse, we cannot respond to individual inquiries about detailed criteria."

Because this package is developed unofficially, please do not send inquiries about usage or bugs to BOOTH or pixiv.

This package is designed and developed based on the terms and guidelines listed below.

However, if BOOTH or pixiv requests suspension of publication, publication will be suspended promptly.

## Handling of BOOTH Library Manager and BOOTH-Related Data

- This package is developed in accordance with the terms of use of BOOTH Library Manager and BOOTH, as well as BOOTH's scraping guidelines.
- Data acquisition is limited to the scope that can be checked with general database tools (e.g., DB Browser for SQLite), and data is read from the local database.
- We do not reverse engineer or modify BOOTH Library Manager itself.
- Within the practically necessary scope, product thumbnail files are downloaded from BOOTH servers and cached locally, reducing access load on BOOTH servers.
- If you are affiliated with BOOTH and have any concerns, please contact the listed email address: `amarinoa@outlook.jp`

## Use of AI in Package Development

- Codex is used in the development of this package.
