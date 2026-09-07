# スタレゾ SS アーカイブ

Star Resonanceのスクリーンショットを監視し、ローカルでAI高画質化・自然なレタッチ・原本保存を自動で行うWindows向け非公式ファンツールです。ゲームの権利者による承認・提携・保証はありません。ゲームの画像・ロゴ・名称などの権利は各権利者に帰属します。

## まず使う

配布物は[v1.00リリースページ](https://github.com/YsKasoDevelop/StarezSSArchive/releases/tag/v1.00)からダウンロードできます。ZIPをすべて展開し、フォルダ内の`StarezSSArchive.exe`を起動してください。.NETランタイムの別途インストールは不要です。

初回起動時に利用条件を確認し、「同意してセットアップ」を押すと、AI処理に必要なファイルをReal-ESRGAN公式GitHubから取得・検証します。写真は外部へ送信せず、処理はPC内で行います。詳しい手順は[かんたん説明書](docs/USER_GUIDE.md)をご覧ください。

## 特徴

- `OriginalPhoto`に追加されたPNGを自動検知
- Real-ESRGANのアニメ向けモデルで内部4倍超解像（TTAは設定可能、初期設定はOFF）
- 保存時のピクセルサイズは元画像のまま維持
- 元の色合いを保ちながら、髪・瞳・衣装などの輪郭を強調
- 軽いノイズ除去と白い輪郭の抑制
- 元画像を`original`フォルダへ同じ名前で移動
- 処理後の画像を監視先へ同じ名前で保存
- 監視先のPNGが100枚を超えた場合、古い画像を`backup`へ移動
- タスクトレイ常駐、SSフォルダを開く、設定、バージョン情報、終了
- 待機中・処理中・処理待ち件数・失敗をタスクトレイに表示
- タスクトレイから一時停止・再開が可能
- 監視先・バックアップ先・原本保存先・レタッチ・TTAを設定可能
- 監視先・原本保存先・バックアップ先が重なる設定を拒否
- 二重起動を防止し、終了時は処理中の画像を安全に完了
- Windowsログオン時のタスクトレイ自動起動に対応

## 動作環境

- Windows 10 version 2004以降またはWindows 11
- Windows x64
- 初回セットアップにはインターネット接続
- AI処理にはVulkan対応GPUとドライバー
- 開発・ビルドには.NET 8 SDK

TTA（テスト時拡張）は設定画面からON/OFFを切り替えられます。ONにすると複数方向から推論するため、通常設定より処理時間が長くなります。見た目の差が小さい場合もあるため、通常はOFFをおすすめします。

## 起動と保存先

配布ZIPを展開したフォルダ内の`StarezSSArchive.exe`を直接起動します。EXEだけを別の場所へ移さないでください。初回セットアップ後はタスクトレイに常駐します。

初期状態では、現在のWindowsユーザーの次のフォルダを監視します。

    %USERPROFILE%\AppData\LocalLow\bokura\StarASIA\OriginalPhoto

設定は次の場所に保存されます。

    %LOCALAPPDATA%\StarezSSArchive\settings.json

原本と、100枚を超えた画像の初期保存先は、ピクチャ内のフォルダを次の優先順位で選びます。

    ピクチャ\StarASIA\original と backup
    （StarASIA がない場合）ピクチャ\StarASIA_STEAM\original と backup

どちらも存在しない場合は、`ピクチャ\StarASIA`を初期候補として使用します。既存の設定ファイルがある場合は、ユーザーが指定した保存先を維持します。

## タスクトレイメニュー

タスクトレイのアイコンを右クリックすると、次の操作ができます。

- 状態表示：待機中、処理中、処理待ち件数、失敗を表示
- SSフォルダを開く：現在設定されている監視先を開く
- 一時停止／再開：新しい画像の処理を止め、再開時に未処理画像を確認
- 設定：監視先、原本保存先、バックアップ保存先、レタッチ、TTA、自動起動を変更
- バージョン情報：アプリ名とバージョン（1.00）を表示
- 終了：処理中の画像があれば完了を待って終了

レタッチをOFFにすると、新しく追加された画像の自動処理を停止します。すでに処理待ちになっている画像は、現在の処理が完了する場合があります。

## 動作仕様と注意

PNGの書き込み完了後に処理が始まります。処理前の画像は`original`保存先に保持され、処理後の画像は監視先に同じ名前で保存されます。元画像と同じピクセル寸法で保存するため、AI処理によってファイルの寸法自体は変わりません。

AIは元画像に存在しない情報を正確に復元するものではありません。細い模様や輪郭が元画像から少し変わる場合があります。画像処理は1件ずつ順番に行い、TTAをONにすると処理時間が長くなります。終了操作を行った場合も、処理中の画像は最大30秒間完了を待ちます。

処理状況とエラーの記録は次の場所に保存されます。

    %LOCALAPPDATA%\StarezSSArchive\app.log

## AIエンジンについて

AIエンジン、モデル、`vcomp140.dll`は公開用ZIPに含めません。初回セットアップ時に、次のReal-ESRGAN公式配布物を公式GitHubからダウンロードし、固定SHA-256で検証したうえで必要なファイルだけをユーザーのローカルデータフォルダへ配置します。

- 公式プロジェクト: https://github.com/xinntao/Real-ESRGAN
- ncnn-vulkan実装: https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan
- Windows配布版: `realesrgan-ncnn-vulkan-20220424-windows.zip`（v0.2.5.0）
- ZIP SHA-256: `ABC02804E17982A3BE33675E4D471E91EA374E65B70167ABC09E31ACB412802D`
- 使用モデル: `realesrgan-x4plus-anime`

AIエンジン・モデル・Microsoft DLLはアプリ本体とは別のライセンス・配布条件です。詳細は[第三者ソフトウェアの通知](engine/THIRD-PARTY-NOTICES.md)、[第三者コンポーネントの条件](THIRD-PARTY-TERMS.md)、`licenses`フォルダを確認してください。本アプリのMITライセンスを第三者コンポーネントへ一括適用するものではありません。

## ソースからビルド

    dotnet restore .\src\StarResonanceUpscaler\StarResonanceUpscaler.csproj
    dotnet build .\src\StarResonanceUpscaler\StarResonanceUpscaler.csproj -c Release

自己完結型のWindows x64配布版を作る場合:

    .\build.ps1

`build.ps1`は毎回新しい`dist\StarezSSArchive-<識別子>`フォルダへ、.NETランタイムを含む単一の`StarezSSArchive.exe`を出力します。公開用ビルドにはゲーム画像、AIエンジン、モデル、`vcomp140.dll`を含めません。利用条件とMicrosoftランタイムの通知は`THIRD-PARTY-TERMS.md`と`licenses`に収録しています。

セットアップ処理と画像処理の簡易テストは次のコマンドで実行できます。

    dotnet run --project .\tests\SetupSmoke\SetupSmoke.csproj -c Release

## ソース構成

    src/StarResonanceUpscaler/
      Program.cs                  タスクトレイ、設定画面、ファイル監視
      Enhancer.cs                 AI超解像と最終画像補正
      EngineInstallation.cs       公式AI配布物の取得・検証・初回セットアップ
      RuntimeTerms.cs             同梱ランタイムの利用条件確認
      StarResonanceUpscaler.csproj
      icon.ico
    engine/
      THIRD-PARTY-NOTICES.md      AIエンジン等の第三者通知
    licenses/                      .NET / Windows SDKのライセンス・通知
    docs/USER_GUIDE.md             かんたん説明書
    tests/SetupSmoke/              セットアップ・画像処理の簡易テスト

## ライセンス

このアプリのオリジナルソースコードは[MIT License](LICENSE)で公開します。

本アプリは非公式ファンツールです。Star Resonanceおよび関連するゲーム内コンテンツの権利者による承認・提携・保証を示すものではありません。ゲーム内画像を含むサンプル画像の利用・再配布は、各権利者の利用規約と著作権に従ってください。

ただし、配布物に含まれる.NET / WPF / Windows Formsランタイムには、Microsoftの.NET Library License、Windows SDK License、MITライセンスおよび各第三者通知が適用されます。詳細は`THIRD-PARTY-TERMS.md`と`licenses`フォルダの原文を確認してください。

AIエンジン、モデル、`vcomp140.dll`は初回セットアップ時に公式配布元から取得する別コンポーネントです。各配布元の条件が適用され、本アプリのMITライセンスで再許諾されるものではありません。
