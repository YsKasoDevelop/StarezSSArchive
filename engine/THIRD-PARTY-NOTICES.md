# 第三者コンポーネント

この公開用パッケージにはAIエンジン・モデル・vcomp140.dllを含めません。初回に公式GitHubから取得するファイルには各供給元の条件が適用されます。.NETは同梱し、そのライセンス原文・通知は `licenses` フォルダと `THIRD-PARTY-TERMS.md` に保存しています。

- Real-ESRGAN-ncnn-vulkan: MIT（Xintao Wang / nihui）。https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/blob/master/LICENSE
- Real-ESRGAN: BSD-3-Clause。https://github.com/xinntao/Real-ESRGAN/blob/master/LICENSE
- ncnn: BSD-3-Clauseと同梱依存物の条件。https://github.com/Tencent/ncnn/blob/master/LICENSE.txt
- 公式アニメモデル: https://github.com/xinntao/Real-ESRGAN/blob/master/docs/anime_model.md
- Visual C++ runtime: Microsoftの条件。https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
- .NET Desktop Runtime: Microsoftの公式配布物の条件。https://github.com/dotnet/core/blob/main/license-information-windows.md

本アプリのMIT許諾はこれらを再許諾しません。個別のモデルLICENSEがないことだけで再配布禁止とは断定できませんが、本版ではモデル許諾範囲の未確認を避けるため公式取得方式としています。

AI導入後のフォルダをそのまま再配布すると第三者ファイルも配布することになります。公開用のbuild.ps1は、利用者が追加したエンジンやモデルをコピーしません。エンジンを独自に同梱する場合は、そのリリースの全依存物を確認し、必要な著作権・条件・免責・NOTICEを保持してください。上記のリンク一覧だけを通知本文の代わりに使わないでください。
