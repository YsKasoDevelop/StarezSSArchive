# 第三者ソフトウェアの利用条件

本アプリ独自のソースコードはMITライセンスです。次の条件は、本アプリの独自コードのMIT許諾を変更するものではありません。

同梱Microsoftコンポーネントは、本アプリの一部としてのみ提供します。利用者・再配布者は、該当コンポーネントについて `licenses/DOTNET-LIBRARY-LICENSE.html` および `licenses/WINDOWS-SDK-LICENSE.html` に記載された適用条件を遵守することに同意する必要があります。法令および適用されるオープンソースライセンスが認める権利は制限しません。独立した製品としての再配布、著作権表示の除去、適用条件に反する改変・使用は認められません。再配布する場合は、この条件と同梱のライセンス・通知を保持し、再配布先にも適用条件への同意を求めてください。

MicrosoftのWindows向け.NETの構成別条件: https://github.com/dotnet/core/blob/main/license-information-windows.md

- coreclr.dll、PresentationNative_cor3.dll、vcruntime140_cor3.dll、wpfgfx_cor3.dll等: .NET Library License。
- D3DCompiler_47_cor3.dll: Windows SDK License。
- その他の.NETファイル: MITおよび第三者通知。詳細は `licenses` 内を参照。

AIエンジン・モデル・vcomp140.dllは本配布物には含めず、初回セットアップ時に利用者のPCで公式GitHubから取得します。これらには配布元の条件が適用され、本アプリのMITライセンスでは再許諾しません。詳細は `engine/THIRD-PARTY-NOTICES.md` を参照してください。
