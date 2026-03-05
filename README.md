# MunicipalityWebSiteCheckTool

自治体サイト監視用のコンソールアプリです。  

## 役割

- `MunicipalityWebSiteCheckTool`: アプリ本体、テスト、ビルド、実行バイナリの Release 配布

設定リポジトリ:

- `https://github.com/AFEEjp/MunicipalityWebSiteCheckTool.Config`

## ディレクトリ

- `MunicipalityWebSiteCheckTool/`: アプリ本体
- `MunicipalityWebSiteCheckTool.Tests/`: 単体テスト
- `.github/workflows/build.yml`: ビルド、テスト、Release 更新
- `local-testdata/`: ローカル統合確認用の固定データ
- `scripts/`: 補助スクリプト

このリポジトリには、運用用の `feeds/` `pages/` `feed-settings.json` を常設しない前提です。  
ローカル実行時は、別リポジトリの設定ファイルを引数で参照します。

## ローカル実行例

設定リポジトリを同じ親ディレクトリに clone している場合の例です。

`type: browser` の feed を監視する場合は、先に Playwright Chromium をインストールしてください。

```powershell
dotnet build .\MunicipalityWebSiteCheckTool\MunicipalityWebSiteCheckTool.csproj -c Debug
pwsh .\MunicipalityWebSiteCheckTool\bin\Debug\net10.0\playwright.ps1 install chromium
```

feed モード:

```powershell
dotnet run --project .\MunicipalityWebSiteCheckTool\MunicipalityWebSiteCheckTool.csproj -- `
  --mode feed `
  --cadence normal `
  --feeds-dir ..\MunicipalityWebSiteCheckTool.Config\feeds `
  --pages-dir ..\MunicipalityWebSiteCheckTool.Config\pages `
  --feed-settings ..\MunicipalityWebSiteCheckTool.Config\feed-settings.json
```

page モード:

```powershell
dotnet run --project .\MunicipalityWebSiteCheckTool\MunicipalityWebSiteCheckTool.csproj -- `
  --mode page `
  --feeds-dir ..\MunicipalityWebSiteCheckTool.Config\feeds `
  --pages-dir ..\MunicipalityWebSiteCheckTool.Config\pages `
  --feed-settings ..\MunicipalityWebSiteCheckTool.Config\feed-settings.json
```

dry-run:

```powershell
dotnet run --project .\MunicipalityWebSiteCheckTool\MunicipalityWebSiteCheckTool.csproj -- `
  --mode feed `
  --cadence fast `
  --feeds-dir ..\MunicipalityWebSiteCheckTool.Config\feeds `
  --pages-dir ..\MunicipalityWebSiteCheckTool.Config\pages `
  --feed-settings ..\MunicipalityWebSiteCheckTool.Config\feed-settings.json `
  --dry-run
```

## 必須環境変数

feed モード:

- `DISCORD_WEBHOOK_PUBCOM`
- `DISCORD_WEBHOOK_ERROR`

page モード:

- `DISCORD_WEBHOOK_ERROR`
- `pages/*.json` の `webhookSecretKey` で参照する各環境変数

必要に応じて、`feeds/*.json` の `webhookKey` で個別通知先を参照できます。

## 設定ファイルの扱い

- `feed-settings.json` の `defaultMatch` を feed 共通の既定値として使う
- 各 `feeds/*.json` に `match` を書いた場合は、そちらを優先する
- 各 `feeds/*.json` に `match` が無く、`feed-settings.json` にも `defaultMatch` が無い場合は fail-fast する
- `feeds/` 配下はサブディレクトリを再帰的に読み込む
